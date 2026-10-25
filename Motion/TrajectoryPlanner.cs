using Core.Motion;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Motion
{
    /// <summary>
    /// 轨迹规划结果：重排后的段列表、告警与统计。
    /// </summary>
    /// <param name="Segments">规划后的段（已克隆，未覆盖编辑器原列表）</param>
    /// <param name="Warnings">规划告警（参数、空移段太短、首段无空移等）</param>
    /// <param name="RemovedMoveCount">被删掉的空移段数量</param>
    /// <param name="InsertedMoveCount">重新插入的空移段数量</param>
    /// <param name="ValveOnCount">生成的开阀触发数量</param>
    /// <param name="ValveOffCount">生成的关阀触发数量</param>
    /// <param name="HasDispenseSegments">源轨迹里是否有点胶段</param>
    public sealed record PlannedTrajectory(
        IReadOnlyList<TrajectorySegment> Segments,
        IReadOnlyList<string> Warnings,
        int RemovedMoveCount,
        int InsertedMoveCount,
        int ValveOnCount,
        int ValveOffCount,
        bool HasDispenseSegments);

    /// <summary>
    /// 加工轨迹规划：去空移 → 在断开的点胶段之间补空移 → 生成提前开关胶 IO。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么先生成新列表再替换：</b>规划中途可能发现参数非法或没有点胶段，
    /// 这时编辑器里的轨迹必须原样保留；所以所有段先克隆，成功后才交给
    /// <see cref="TrajectoryEditor.ApplyProcess"/> 一次性替换。
    /// </para>
    /// <para>
    /// <b>IO 规则（与现场约定一致）：</b>
    /// <list type="bullet">
    /// <item>空移段 → 下一段是点胶：在空移段末段按“空移速度 × 开阀响应”提前开阀，
    /// 触发距离 = 空移段长度 − 提前量；</item>
    /// <item>点胶段 → 下一段不是点胶（空移或结束）：按“点胶速度 × 关阀响应”提前关阀；</item>
    /// <item>点胶段 → 点胶段连续：阀保持打开，不生成任何 IO。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 阀门物理提前量在这里换算成“段内行程”，下游运动卡按段内行程（PSO）触发，
    /// 因此不再需要全局 MOVEOP_DELAY 再补一次；联调时该全局延迟应保持 0。
    /// </para>
    /// </remarks>
    public static class TrajectoryPlanner
    {
        /// <summary>端点连续性阈值（mm），只吸收浮点误差。</summary>
        private const double ContinuityEpsilon = 1e-6;

        /// <summary>
        /// 按加工参数重排轨迹并生成 IO 触发。
        /// </summary>
        /// <param name="source">编辑器当前轨迹（不会被修改）</param>
        /// <param name="parameters">加工参数快照</param>
        public static PlannedTrajectory Plan(
            IReadOnlyList<TrajectorySegment> source,
            MachiningParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(parameters);

            if (!parameters.TryValidate(out string parameterError))
            {
                return Failed(new[] { parameterError });
            }

            var warnings = new List<string>();
            var dispenseSegments = new List<TrajectorySegment>();
            int removedMoves = 0;

            foreach (TrajectorySegment? segment in source)
            {
                if (segment is null)
                {
                    warnings.Add("轨迹中存在 null 段，已跳过。");
                    continue;
                }

                if (segment.MotionMode == MotionMode.Move)
                {
                    removedMoves++;
                    continue;
                }

                dispenseSegments.Add(segment.Clone());
            }

            if (dispenseSegments.Count == 0)
            {
                warnings.Add("没有点胶段：无法生成加工轨迹。");
                return Failed(warnings, removedMoves);
            }

            // 统一应用本轮的工艺参数；IO 由规划重新生成，旧触发全部丢弃。
            // 段端点的 Z 同步到 WorkZ：段长按 XY 计算（PSO 行程），避免残留的旧 Z 把段长算大。
            foreach (TrajectorySegment segment in dispenseSegments)
            {
                segment.StartPt = new Point3D(segment.StartPt.X, segment.StartPt.Y, parameters.WorkZ);
                segment.EndPt = new Point3D(segment.EndPt.X, segment.EndPt.Y, parameters.WorkZ);
                segment.Speed = parameters.DispenseSpeed;
                segment.WorkZ = parameters.WorkZ;
                segment.LiftZ = parameters.LiftZ;
                segment.IoTriggers.Clear();
                segment.CalcSegmentLength();
            }

            var result = new List<TrajectorySegment>();
            int insertedMoves = 0;
            result.Add(dispenseSegments[0]);

            //检测点胶段是否连续，不连续添加空移段
            for (int i = 1; i < dispenseSegments.Count; i++)
            {
                TrajectorySegment previous = dispenseSegments[i - 1];
                TrajectorySegment next = dispenseSegments[i];

                if (!IsContinuous(previous.EndPt, next.StartPt))
                {
                    result.Add(CreateMove(previous.EndPt, next.StartPt, parameters));
                    insertedMoves++;
                }

                result.Add(next);
            }

            int valveOnCount = 0;
            int valveOffCount = 0;
            bool valveOpen = false;
            //生成IO表
            for (int i = 0; i < result.Count; i++)
            {
                TrajectorySegment segment = result[i];
                TrajectorySegment? next = i + 1 < result.Count ? result[i + 1] : null;

                if (segment.MotionMode == MotionMode.Move)
                {
                    // 空移段末尾 → 下一点胶段：提前开阀；阀已开则不再重复
                    if (next is { MotionMode: MotionMode.Dispense } && !valveOpen)
                    {
                        AddValveOn(segment, i, parameters, warnings, ref valveOnCount);
                        valveOpen = true;
                    }

                    continue;
                }

                // 首段就是点胶：没有前置空移可用来提前开阀
                if (!valveOpen)
                {
                    segment.IoTriggers.Add(new IoTrigger
                    {
                        DistanceOnSegment = 0,
                        Action = IoActionType.ValveOn,
                        DelayMs = 0,
                    });
                    valveOnCount++;
                    valveOpen = true;
                    warnings.Add(
                        $"第 {i + 1} 段点胶之前没有空移段：开阀只能在本段起点触发，" +
                        "胶阀响应会造成起始缺胶；建议在前面补一段空移。");
                }

                // 点胶 → 非点胶（空移/结束）：提前关阀；点胶 → 点胶保持打开
                if (next is null || next.MotionMode != MotionMode.Dispense)
                {
                    AddValveOff(segment, i, parameters, warnings, ref valveOffCount);
                    valveOpen = false;
                }
            }

            // 段内触发按行程排序，保证下游按段内距离递增
            foreach (TrajectorySegment segment in result)
            {
                segment.IoTriggers.Sort((a, b) => a.DistanceOnSegment.CompareTo(b.DistanceOnSegment));
            }

            return new PlannedTrajectory(
                result,
                warnings,
                removedMoves,
                insertedMoves,
                valveOnCount,
                valveOffCount,
                HasDispenseSegments: true);
        }

        private static void AddValveOn(
            TrajectorySegment moveSegment,
            int index,
            MachiningParameters parameters,
            List<string> warnings,
            ref int count)
        {
            double length = moveSegment.SegmentLength;
            double lead = moveSegment.Speed * parameters.ValveOpenResponseMs / 1000.0;
            double distance = CalculateTriggerDistance(length, lead, index, "空移段", isOpening: true, warnings);

            moveSegment.IoTriggers.Add(new IoTrigger
            {
                DistanceOnSegment = distance,
                Action = IoActionType.ValveOn,
                DelayMs = 0,
            });
            count++;
        }

        private static void AddValveOff(
            TrajectorySegment dispenseSegment,
            int index,
            MachiningParameters parameters,
            List<string> warnings,
            ref int count)
        {
            double length = dispenseSegment.SegmentLength;
            double lead = dispenseSegment.Speed * parameters.ValveCloseResponseMs / 1000.0;
            double distance = CalculateTriggerDistance(length, lead, index, "点胶段", isOpening: false, warnings);

            dispenseSegment.IoTriggers.Add(new IoTrigger
            {
                DistanceOnSegment = distance,
                Action = IoActionType.ValveOff,
                DelayMs = 0,
            });
            count++;
        }

        private static double CalculateTriggerDistance(
            double length,
            double lead,
            int index,
            string segmentName,
            bool isOpening,
            List<string> warnings)
        {
            string label = $"第 {index + 1} 段{segmentName}";

            if (length <= 0)
            {
                warnings.Add($"{label}长度为 0，IO 触发点只能落在段首。");
                return 0;
            }

            if (lead > length + 1e-9)
            {
                // 开阀提前量超过整段时，只能压到段首（会整段空移出胶，必须靠告警提示）。
                // 关阀提前量超过整段时绝不能压到段首，否则整段点胶不出胶；改为段末关阀。
                warnings.Add(isOpening
                    ? $"{label}长度 {length:F3} mm 小于开阀提前量 {lead:F3} mm，开阀触发点被压到段首（整段空移可能出胶）。"
                    : $"{label}长度 {length:F3} mm 小于关阀提前量 {lead:F3} mm，无法在段内提前关阀，改为段末关阀。");

                return isOpening ? 0 : length;
            }

            double distance = Math.Min(length, Math.Max(0, length - lead));
            return Math.Min(length, Math.Round(distance, 6));
        }

        private static TrajectorySegment CreateMove(
            Point3D from,
            Point3D to,
            MachiningParameters parameters)
        {
            var move = new TrajectorySegment
            {
                SegType = SegmentType.Line,
                MotionMode = MotionMode.Move,
                StartPt = new Point3D(from.X, from.Y, parameters.LiftZ),
                EndPt = new Point3D(to.X, to.Y, parameters.LiftZ),
                Speed = parameters.MoveSpeed,
                WorkZ = parameters.WorkZ,
                LiftZ = parameters.LiftZ,
                IoTriggers = new List<IoTrigger>(),
            };
            move.CalcSegmentLength();
            return move;
        }

        private static bool IsContinuous(Point3D end, Point3D start) =>
            Math.Abs(end.X - start.X) <= ContinuityEpsilon &&
            Math.Abs(end.Y - start.Y) <= ContinuityEpsilon;

        private static PlannedTrajectory Failed(
            IReadOnlyList<string> warnings,
            int removedMoves = 0) =>
            new(
                Array.Empty<TrajectorySegment>(),
                warnings,
                removedMoves,
                0,
                0,
                0,
                HasDispenseSegments: false);
    }
}

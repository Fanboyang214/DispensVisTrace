using Core.Motion;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Motion
{
    /// <summary>
    /// 轨迹编辑器：拖拽重排段顺序、增删段、批量修改工艺、轨迹校验告警。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只操作 <see cref="DispenseProgram.Segments"/> 的列表顺序与段内容：
    /// <b>列表索引顺序就是设备执行顺序</b>，因此"拖拽重排"本质就是改索引。
    /// </para>
    /// <para>
    /// 校验返回的是告警列表而不是异常：这类问题多数是"能跑但会出次品"，
    /// 现场需要看到清单再决定改不改，而不是被一句异常挡在编辑界面外面。
    /// </para>
    /// </remarks>
    public class TrajectoryEditor
    {
        /// <summary>端点连续性判定阈值（mm），只用于吸收浮点误差</summary>
        private const double ContinuityEpsilon = 1e-6;

        private readonly DispenseProgram _program;

        /// <param name="program">被编辑的配方，直接改它的 <see cref="DispenseProgram.Segments"/></param>
        public TrajectoryEditor(DispenseProgram program)
        {
            _program = program ?? throw new ArgumentNullException(nameof(program));
        }

        /// <summary>当前轨迹段列表（与配方共享同一份）</summary>
        public List<TrajectorySegment> Segments => _program.Segments;

        /// <summary>
        /// 拖拽重排：把 <paramref name="oldIndex"/> 处的段移到 <paramref name="newIndex"/>。
        /// </summary>
        /// <param name="oldIndex">被移动段的当前下标</param>
        /// <param name="newIndex">移动后的目标下标</param>
        /// <remarks>
        /// 语义是"移动到该位置"，不是"与某个元素交换"：先摘出来再按移除后的坐标插入，
        /// 所以往后移时不需要调用方自己扣 1。
        /// </remarks>
        public void MoveSegment(int oldIndex, int newIndex)
        {
            List<TrajectorySegment> segments = Segments;
            CheckIndex(oldIndex, segments.Count, nameof(oldIndex));

            if (newIndex < 0 || newIndex >= segments.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(newIndex), newIndex, $"目标下标必须在 [0, {segments.Count - 1}] 内。");
            }

            if (oldIndex == newIndex)
            {
                return;
            }

            TrajectorySegment moved = segments[oldIndex];
            segments.RemoveAt(oldIndex);
            segments.Insert(newIndex, moved);
        }

        /// <summary>删除指定索引的轨迹段。</summary>
        /// <param name="index">要删除的段下标</param>
        public void RemoveSegment(int index)
        {
            List<TrajectorySegment> segments = Segments;
            CheckIndex(index, segments.Count, nameof(index));

            segments.RemoveAt(index);
        }

        /// <summary>在指定位置插入新的轨迹段。</summary>
        /// <param name="index">插入位置，允许等于当前段数（追加到末尾）</param>
        /// <param name="seg">要插入的段</param>
        public void InsertSegment(int index, TrajectorySegment seg)
        {
            ArgumentNullException.ThrowIfNull(seg);

            List<TrajectorySegment> segments = Segments;
            if (index < 0 || index > segments.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, $"插入位置必须在 [0, {segments.Count}] 内。");
            }

            // 段长是派生量，编辑期就补齐，避免校验拿到 0 长度而误判 IO 越界
            seg.CalcSegmentLength();
            segments.Insert(index, seg);
        }

        /// <summary>
        /// 批量选中轨迹段，统一修改工艺参数。
        /// </summary>
        /// <param name="segIndexList">要修改的段下标集合，允许乱序与重复</param>
        /// <param name="setAction">对每段执行的修改动作</param>
        /// <remarks>
        /// 修改动作可能改到速度 / 高度，但不会改几何，因此段长无需重算。
        /// 若某项修改确实动了几何，调用方应自行调用 <see cref="TrajectorySegment.CalcSegmentLength"/>。
        /// </remarks>
        public void BatchSetProperty(List<int> segIndexList, Action<TrajectorySegment> setAction)
        {
            ArgumentNullException.ThrowIfNull(segIndexList);
            ArgumentNullException.ThrowIfNull(setAction);

            List<TrajectorySegment> segments = Segments;

            // 先全部校验再统一执行：setAction 中途抛异常时不会留下"改了一半"的轨迹
            foreach (int index in segIndexList)
            {
                CheckIndex(index, segments.Count, nameof(segIndexList));
            }

            foreach (int index in segIndexList.Distinct())
            {
                setAction(segments[index]);
            }
        }

        /// <summary>
        /// 全轨迹校验，返回告警信息。
        /// </summary>
        /// <returns>告警列表，空列表表示未发现问题</returns>
        /// <remarks>
        /// <para>校验项：</para>
        /// <list type="bullet">
        /// <item>端点不连续：相邻段之间会多出一条图纸上没有的直线（撞针风险）；</item>
        /// <item>空轨迹；</item>
        /// <item>工艺参数缺失：速度非正、点胶段工作高度非正；</item>
        /// <item>IO 触发越界：距离为负或超出本段长度，卡按段内行程触发时永远等不到；</item>
        /// <item>IO 时钟顺序：同段内触发距离未递增，实际触发次序会和列表顺序不一致；</item>
        /// <item>圆弧几何：半径非正、圆心与起点重合、整圆未拆分；</item>
        /// <item>空移段开阀：空移不该出胶。</item>
        /// </list>
        /// <para>
        /// <b>空移段允许带 IO</b>（例如提前开阀 / 提前关阀），所以不再把"Move 段存在 IO 触发"
        /// 当作告警；只有"空移段开阀"这种会滴胶的动作用才提示。
        /// </para>
        /// </remarks>
        public List<string> ValidateAllSegments()
        {
            var warnings = new List<string>();
            List<TrajectorySegment> segments = Segments;

            if (segments.Count == 0)
            {
                warnings.Add("轨迹为空：没有任何可执行的段。");
                return warnings;
            }

            for (int i = 0; i < segments.Count; i++)
            {
                TrajectorySegment seg = segments[i];

                if (seg == null)
                {
                    warnings.Add($"第 {i} 段为 null。");
                    continue;
                }

                // 段长是派生量：可能是导入后还没算过，或几何被外部改过，这里按几何刷新
                seg.CalcSegmentLength();

                if (double.IsNaN(seg.Speed) || double.IsInfinity(seg.Speed) || seg.Speed <= 0)
                {
                    warnings.Add($"第 {i} 段速度非法（{seg.Speed}）：卡无法规划。");
                }

                if (seg.MotionMode == MotionMode.Dispense &&
                    (double.IsNaN(seg.WorkZ) || double.IsInfinity(seg.WorkZ) || seg.WorkZ <= 0))
                {
                    warnings.Add($"第 {i} 段是点胶段但工作高度 WorkZ={seg.WorkZ} 非法。");
                }

                ValidateIoTriggers(i, seg, warnings);
                ValidateArc(i, seg, warnings);

                // 端点连续性：索引顺序就是执行顺序，相邻段必须首尾相接
                if (i > 0)
                {
                    TrajectorySegment prev = segments[i - 1];
                    if (prev != null && !IsSamePlace(prev.EndPt, seg.StartPt))
                    {
                        warnings.Add(
                            $"第 {i - 1} 段终点 {prev.EndPt} 与第 {i} 段起点 {seg.StartPt} 不连续：" +
                            "卡会在两段之间额外走一条直线。");
                    }
                }
            }

            return warnings;
        }

        /// <summary>单段 IO 触发校验</summary>
        private static void ValidateIoTriggers(int index, TrajectorySegment seg, List<string> warnings)
        {
            double previousDistance = double.NegativeInfinity;

            for (int k = 0; k < seg.IoTriggers.Count; k++)
            {
                IoTrigger trigger = seg.IoTriggers[k];
                if (trigger == null)
                {
                    warnings.Add($"第 {index} 段第 {k} 个 IO 触发为 null。");
                    continue;
                }

                if (double.IsNaN(trigger.DistanceOnSegment) || trigger.DistanceOnSegment < 0)
                {
                    warnings.Add($"第 {index} 段第 {k} 个 IO 触发距离非法（{trigger.DistanceOnSegment}）。");
                }
                else if (trigger.DistanceOnSegment > seg.SegmentLength)
                {
                    warnings.Add(
                        $"第 {index} 段第 {k} 个 IO 触发距离 {trigger.DistanceOnSegment:F4} 超出本段长度 " +
                        $"{seg.SegmentLength:F4}：卡按段内行程触发时会永远等不到。");
                }
                else if (trigger.DistanceOnSegment < previousDistance)
                {
                    warnings.Add(
                        $"第 {index} 段第 {k} 个 IO 触发距离 {trigger.DistanceOnSegment:F4} 小于前一个 " +
                        $"{previousDistance:F4}：实际触发次序会与列表顺序不一致。");
                }

                if (trigger.DistanceOnSegment > previousDistance)
                {
                    previousDistance = trigger.DistanceOnSegment;
                }

                // 空移段允许带 IO（提前开阀 / 提前关阀），但空移段开阀会沿路滴胶
                if (seg.MotionMode == MotionMode.Move && trigger.Action == IoActionType.ValveOn)
                {
                    warnings.Add($"第 {index} 段是空移段却要开胶阀（行程 {trigger.DistanceOnSegment:F4}）：空移出胶会滴在过渡路径上。");
                }
            }
        }

        /// <summary>单段圆弧几何校验</summary>
        private static void ValidateArc(int index, TrajectorySegment seg, List<string> warnings)
        {
            if (seg.SegType != SegmentType.Arc)
            {
                return;
            }

            if (double.IsNaN(seg.Radius) || double.IsInfinity(seg.Radius) || seg.Radius <= 0)
            {
                warnings.Add($"第 {index} 段圆弧半径非法（{seg.Radius}）。");
            }

            double centerToStart = Math.Sqrt(
                (seg.StartPt.X - seg.ArcCenter.X) * (seg.StartPt.X - seg.ArcCenter.X) +
                (seg.StartPt.Y - seg.ArcCenter.Y) * (seg.StartPt.Y - seg.ArcCenter.Y));
            if (double.IsNaN(centerToStart) || centerToStart <= 0)
            {
                warnings.Add($"第 {index} 段圆弧的圆心与起点重合，几何非法（圆心 {seg.ArcCenter}）。");
                return;
            }

            double sweep = seg.CircleSweep;
            if (sweep <= 0 || sweep >= 2 * Math.PI - 1e-9)
            {
                warnings.Add(
                    $"第 {index} 段圆弧是整圆（起止角相等）：卡无法用单段圆弧表达整圈，" +
                    "需先拆成两段半圆弧。");
            }
        }

        private static bool IsSamePlace(Point3D a, Point3D b) =>
            Math.Abs(a.X - b.X) <= ContinuityEpsilon &&
            Math.Abs(a.Y - b.Y) <= ContinuityEpsilon;

        private static void CheckIndex(int index, int count, string paramName)
        {
            if (index < 0 || index >= count)
            {
                throw new ArgumentOutOfRangeException(
                    paramName, index, count == 0
                        ? "轨迹段列表为空，没有可操作的下标。"
                        : $"下标必须在 [0, {count - 1}] 内。");
            }
        }
    }
}

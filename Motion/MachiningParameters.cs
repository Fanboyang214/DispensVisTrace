using System;

namespace Motion
{
    /// <summary>
    /// 加工参数快照：轨迹重排与提前开关胶 IO 生成用的工艺值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是<b>不可变快照</b>。界面上可编辑的值放在
    /// <see cref="ViewModel.MachiningParametersViewModel"/>，生成轨迹时先取一份快照，
    /// 避免生成过程中被界面改到一半。
    /// </para>
    /// <para>
    /// 单位约定：高度 mm、速度 mm/s、胶阀响应 ms。提前量 = 当前段速度 × 响应时间 / 1000，
    /// 由 <see cref="TrajectoryPlanner"/> 换算成段内行程距离，写进 <see cref="IoTrigger"/>。
    /// </para>
    /// </remarks>
    public sealed class MachiningParameters
    {
        /// <summary>默认点胶高度（mm）。</summary>
        public const double DefaultWorkZ = 5;

        /// <summary>默认抬刀高度（mm）。</summary>
        public const double DefaultLiftZ = 30;

        /// <summary>默认点胶速度（mm/s）。</summary>
        public const double DefaultDispenseSpeed = 500;

        /// <summary>默认空移速度（mm/s）。</summary>
        public const double DefaultMoveSpeed = 800;

        /// <summary>默认开阀响应（ms）。</summary>
        public const double DefaultValveOpenResponseMs = 100;

        /// <summary>默认关阀响应（ms）。</summary>
        public const double DefaultValveCloseResponseMs = 100;

        /// <summary>点胶工作高度（mm）。</summary>
        public required double WorkZ { get; init; }

        /// <summary>抬刀高度（mm）。</summary>
        public required double LiftZ { get; init; }

        /// <summary>点胶段速度（mm/s）。</summary>
        public required double DispenseSpeed { get; init; }

        /// <summary>空移段速度（mm/s）。</summary>
        public required double MoveSpeed { get; init; }

        /// <summary>开阀响应时间（ms）：从 IO 触发到胶实际流出。</summary>
        public required double ValveOpenResponseMs { get; init; }

        /// <summary>关阀响应时间（ms）：从 IO 触发到胶实际停止。</summary>
        public required double ValveCloseResponseMs { get; init; }

        /// <summary>开阀提前量（mm）：空移速度 × 开阀响应。</summary>
        public double OpenLeadMm => MoveSpeed * ValveOpenResponseMs / 1000.0;

        /// <summary>关阀提前量（mm）：点胶速度 × 关阀响应。</summary>
        public double CloseLeadMm => DispenseSpeed * ValveCloseResponseMs / 1000.0;

        /// <summary>默认参数。</summary>
        public static MachiningParameters CreateDefault() => new()
        {
            WorkZ = DefaultWorkZ,
            LiftZ = DefaultLiftZ,
            DispenseSpeed = DefaultDispenseSpeed,
            MoveSpeed = DefaultMoveSpeed,
            ValveOpenResponseMs = DefaultValveOpenResponseMs,
            ValveCloseResponseMs = DefaultValveCloseResponseMs,
        };

        /// <summary>
        /// 校验几何/速度/时序是否可用于生成轨迹。
        /// </summary>
        /// <param name="error">失败原因；成功时为空字符串</param>
        public bool TryValidate(out string error)
        {
            if (!IsFinite(WorkZ) || WorkZ <= 0)
            {
                error = $"点胶高度必须为正数（当前 {WorkZ}）。";
                return false;
            }

            if (!IsFinite(LiftZ) || LiftZ <= WorkZ)
            {
                error = $"抬刀高度必须大于点胶高度（当前抬刀 {LiftZ}，点胶 {WorkZ}）。";
                return false;
            }

            if (!IsFinite(DispenseSpeed) || DispenseSpeed <= 0)
            {
                error = $"点胶速度必须为正数（当前 {DispenseSpeed}）。";
                return false;
            }

            if (!IsFinite(MoveSpeed) || MoveSpeed <= 0)
            {
                error = $"空移速度必须为正数（当前 {MoveSpeed}）。";
                return false;
            }

            if (!IsFinite(ValveOpenResponseMs) || ValveOpenResponseMs < 0)
            {
                error = $"开阀响应必须为非负数（当前 {ValveOpenResponseMs}）。";
                return false;
            }

            if (!IsFinite(ValveCloseResponseMs) || ValveCloseResponseMs < 0)
            {
                error = $"关阀响应必须为非负数（当前 {ValveCloseResponseMs}）。";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

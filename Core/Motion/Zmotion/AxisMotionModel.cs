namespace Core.Motion.Zmotion
{
    /// <summary>
    /// 单轴运行状态快照：一次批量读给界面用，避免 10 Hz 轮询逐项读把通信打满。
    /// </summary>
    /// <param name="Axis">轴号（0 = X，1 = Y，2 = Z）。</param>
    /// <param name="CommandPosition">指令位置（DPOS，mm）。</param>
    /// <param name="FeedbackPosition">反馈位置（编码器，mm）。</param>
    /// <param name="Speed">当前速度（mm/s）。</param>
    /// <param name="IsMoving">是否在运动。</param>
    /// <param name="IsHomed">是否已回零。</param>
    /// <param name="PositiveLimit">正向硬限位信号。</param>
    /// <param name="NegativeLimit">负向硬限位信号。</param>
    /// <param name="Alarm">驱动器报警。</param>
    public sealed record AxisMotionSnapshot(
        int Axis,
        double CommandPosition,
        double FeedbackPosition,
        double Speed,
        bool IsMoving,
        bool IsHomed,
        bool PositiveLimit,
        bool NegativeLimit,
        bool Alarm);

    /// <summary>
    /// 控制器轴参数：每个成员对应一个 ZMotion BASIC 轴参数（括号里是参数名）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这些名字是<b>控制器侧的事实</b>（见 <c>ZAux_Direct_SetParam/GetParam</c>），本枚举只是把它们
    /// 收窄成类型安全的子集，让界面不出现字符串拼写错误。新增字段时同步
    /// <c>Motion.Zmotion.ZauxZmotionApi</c> 的映射。
    /// </para>
    /// <para>
    /// <b>待联调：</b>回零相关（<see cref="HomeFastSpeed"/> / <see cref="Creep"/>）与前瞻相关
    /// （<see cref="CornerMode"/> / <see cref="DecelAngle"/> / <see cref="StopAngle"/> /
    /// <see cref="FullSpeedRadius"/> / <see cref="SpeedLimitRadius"/>）的语义按 ZMotion 手册命名推断，
    /// 首次上机需逐项读回确认；写错会真动轴，保存前请先空跑确认。
    /// </para>
    /// </remarks>
    public enum AxisParameter
    {
        /// <summary>脉冲当量（UNITS）：多少个脉冲走一个单位（mm）。</summary>
        Units,

        /// <summary>起始/最小速度（LSPEED）。</summary>
        StartSpeed,

        /// <summary>运行速度（SPEED）。</summary>
        Speed,

        /// <summary>加速度（ACCEL）。</summary>
        Accel,

        /// <summary>减速度（DECEL）。</summary>
        Decel,

        /// <summary>S 曲线时间（SRAMP，秒）。</summary>
        Sramp,

        /// <summary>快减/急停减速度（FASTDEC）。</summary>
        FastDec,

        /// <summary>正向软限位（FS_LIMIT，mm）。</summary>
        PositiveLimit,

        /// <summary>负向软限位（RS_LIMIT，mm）。</summary>
        NegativeLimit,

        /// <summary>回零反找（慢速）速度（CREEP，mm/s）。</summary>
        Creep,

        /// <summary>回零快速搜索速度（FHSPEED，mm/s）。<b>待联调</b>确认固件是否支持该参数。</summary>
        HomeFastSpeed,

        /// <summary>原点输入号（DATUM_IN）。</summary>
        DatumInput,

        /// <summary>正向限位输入号（FWD_IN）。</summary>
        ForwardLimitInput,

        /// <summary>负向限位输入号（REV_IN）。</summary>
        ReverseLimitInput,

        /// <summary>前瞻拐角模式（CORNER_MODE）。</summary>
        CornerMode,

        /// <summary>前瞻减速角度（DECEL_ANGLE，度）。</summary>
        DecelAngle,

        /// <summary>前瞻停止角度（STOP_ANGLE，度）。</summary>
        StopAngle,

        /// <summary>前瞻限制（全速）半径（FULL_SP_RADIUS，mm）。</summary>
        FullSpeedRadius,

        /// <summary>前瞻限速半径（SPLIMIT_RADIUS，mm）。</summary>
        SpeedLimitRadius
    }
}

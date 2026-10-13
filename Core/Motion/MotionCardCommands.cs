using System;
using System.Collections.Generic;

namespace Core.Motion
{
    /// <summary>
    /// IO 动作类型：既是轨迹段的段内触发动作（<c>Motion.IoTrigger</c>），
    /// 也是下发指令里的动作（<see cref="MotionCardIoCommand"/>）。
    /// </summary>
    /// <remarks>
    /// 定义在 Core 而不是 Motion：厂商无关指令集要引用它（IO 指令的动作字段），
    /// 而 Core 不能反向依赖 Motion。轨迹段的触发列表与下发指令共用同一个枚举，
    /// 避免两边各一套再写映射。
    /// </remarks>
    public enum IoActionType
    {
        /// <summary>开胶阀</summary>
        ValveOn,

        /// <summary>关胶阀</summary>
        ValveOff,

        /// <summary>延时等待</summary>
        Delay
    }

    /// <summary>
    /// 下发给运动控制卡的一条路径指令（几何与目标点）。
    /// </summary>
    /// <remarks>
    /// 用 <c>init</c> 属性而不是位置记录参数：几何参数由基类持有、圆弧额外参数由派生类持有，
    /// 位置参数无法在对象初始化器里给派生类型赋值。
    /// </remarks>
    public abstract record MotionCardCommand
    {
        /// <summary>指令序号，等于配方里段的下标，用于对位与产线追溯</summary>
        public int Mark { get; init; }

        /// <summary>目标点，按轴序排列（当前为 X, Y 绝对坐标 mm）</summary>
        public double[] Target { get; init; } = Array.Empty<double>();

        /// <summary>本段速度 mm/s</summary>
        public double Speed { get; init; }

        /// <summary>卡侧运动模式：0=空移，1=点胶</summary>
        public ushort Mode { get; init; }

        /// <summary>本段该用的高度：点胶取 WorkZ，空移取 LiftZ</summary>
        public double Z { get; init; }
    }

    /// <summary>
    /// 直线指令；转换器把它落成该品牌的直线插补（如正运动的 MOVEA 一类）。
    /// </summary>
    public sealed record MotionCardLineCommand : MotionCardCommand;

    /// <summary>
    /// 圆弧指令：由"终点 + 圆心"定义，转换器落到该品牌的原生圆弧插补。
    /// </summary>
    /// <remarks>
    /// 圆弧保留原生圆心 / 半径，不做离散，保住卡的圆弧插补能力。
    /// 整圆已在下发前被拒绝（终点与起点重合，卡分不清走整圈还是不走），
    /// 所以 <see cref="CircleCount"/> 恒为 0。
    /// </remarks>
    public sealed record MotionCardArcCommand : MotionCardCommand
    {
        /// <summary>圆心（X, Y）</summary>
        public double[] Center { get; init; } = Array.Empty<double>();

        /// <summary>半径 mm，仅作核对，卡侧用圆心时不需要</summary>
        public double Radius { get; init; }

        /// <summary>圆弧方向：0=顺时针，1=逆时针（卡侧约定值，首次联调需确认）</summary>
        public ushort ArcDirection { get; init; }

        /// <summary>整圈数，单段圆弧为 0（卡侧参数语义，首次联调需确认）</summary>
        public int CircleCount { get; init; }
    }

    /// <summary>
    /// 下发给运动控制卡的一条 IO 指令。
    /// </summary>
    public sealed record MotionCardIoCommand
    {
        /// <summary>所属段的下标，与路径指令的 <see cref="MotionCardCommand.Mark"/> 对应</summary>
        public int Mark { get; init; }

        /// <summary>输出位号（胶阀），取决于现场接线</summary>
        public ushort BitNo { get; init; }

        /// <summary>true=开（ValveOn），false=关（ValveOff）</summary>
        public bool On { get; init; }

        /// <summary>
        /// 自所属段起点起的行进距离 mm。转换器把它交给该品牌的
        /// "按行程触发输出"（位置比较输出 / 位置同步输出 PSO 一类）；
        /// 段内相对行程与位姿补偿无关，所以补偿后无需改这里。
        /// </summary>
        public double DistanceOnSegment { get; init; }

        /// <summary>原始动作类型，保留以便卡侧区分 ValveOn / ValveOff / Delay</summary>
        public IoActionType Action { get; init; }

        /// <summary>延时毫秒，仅 Delay 类型有意义</summary>
        public double DelayMs { get; init; }
    }

    /// <summary>
    /// 一次下发的完整指令集：路径与 IO 分成两个列表。
    /// </summary>
    /// <param name="Path">路径指令，按加工顺序排列</param>
    /// <param name="Io">IO 指令，按所属段号与行程距离排列</param>
    /// <remarks>
    /// 之所以不把 IO 混进路径点：路径点是纯坐标，放不下开阀事件；
    /// 用哨兵值编码则不可读、不可校验。分开表达后，
    /// 路径每条指令对应卡的直线 / 圆弧指令，IO 走卡的行程触发指令。
    /// </remarks>
    public sealed record MotionCardCommandSet(
        IReadOnlyList<MotionCardCommand> Path, IReadOnlyList<MotionCardIoCommand> Io);
}

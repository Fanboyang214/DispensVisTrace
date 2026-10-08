using Core.Motion;

namespace Motion
{
    /// <summary>
    /// 转换器：<b>厂商无关的指令集</b> → 某个品牌控制器能执行的程序载荷。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一层是"换品牌时唯一要重写的东西"：轨迹与 IO 的语义（<see cref="MotionCardCommandSet"/>）
    /// 不变，变的是怎么把它说成目标控制器听得懂的程序——正运动 = ZBasic 程序；
    /// 换成别的品牌（自研控制器、或只吃 G 代码文件的卡）时再添一个实现即可。
    /// </para>
    /// <para>
    /// 实现必须是<b>纯函数</b>：不碰卡、不碰磁盘、无状态。这样格式能单测、能离线比对、
    /// 能进版本管理；联网与下发是 <c>IMotionCardService</c> 的事。
    /// </para>
    /// <para>
    /// <b>表达不了就抛异常，绝不降级。</b>静默降级意味着阀门时序被悄悄挪了位置——
    /// 在点胶机上就是过胶或撞针，属于必须在动作之前挡住的错误。
    /// </para>
    /// </remarks>
    public interface IMotionProgramWriter
    {
        /// <summary>格式名（日志 / 诊断用），例如 <c>"Zmotion-ZBasic"</c>。</summary>
        string FormatName { get; }

        /// <summary>
        /// 生成程序载荷。
        /// </summary>
        /// <param name="commands">
        /// 由 <c>DispenseProgram.BuildMotionCardCommands</c> 生成的厂商无关指令集。
        /// </param>
        /// <exception cref="System.InvalidOperationException">指令集不完整（坐标不足、圆弧缺圆心等）。</exception>
        /// <exception cref="System.NotSupportedException">该格式表达不了这条轨迹（骨架阶段的已知缺口）。</exception>
        MotionProgramPayload Write(MotionCardCommandSet commands);
    }
}

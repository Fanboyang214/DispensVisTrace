namespace Core.Motion
{
    /// <summary>
    /// 厂商格式的"运动程序"载荷：整段轨迹在某个品牌控制器上的<b>最终可执行形态</b>。
    /// </summary>
    /// <param name="Name">
    /// 控制器内的程序名（正运动 = 程序/任务名；只吃文件的控制器 = 文件名）。
    /// </param>
    /// <param name="Text">
    /// 程序正文。正运动 = ZBasic 源程序；只吃文件的控制器 = 该品牌的文本格式。
    /// </param>
    /// <remarks>
    /// <para>
    /// 与 <c>Motion.MotionCardCommandSet</c> 的分工：后者是<b>厂商无关</b>的指令集
    /// （几何 + 工艺 + IO 行程），本类型是某个品牌的最终形态，
    /// 由 <c>Motion.IMotionProgramWriter</c> 产出、由卡侧整包下载。
    /// </para>
    /// <para>
    /// 只有 <see cref="Text"/> 没有二进制字段：目前两家都支持"整包下发一个文本程序"。
    /// 真出现必须二进制下发的品牌，再加字段（现在加就是空转）。
    /// </para>
    /// </remarks>
    public sealed record MotionProgramPayload(string Name, string Text);
}

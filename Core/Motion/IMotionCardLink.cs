namespace Core.Motion
{
    /// <summary>
    /// 运动控制卡的连接通道 —— 厂商无关的最小公分母。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只管"连上 / 断开 / 是否在线"。<b>不做业务</b>：位姿下发、程序装载、启停都在
    /// <c>Motion.IMotionCardService</c>。
    /// </para>
    /// <para>
    /// <b>连接所有权必须唯一。</b>一张卡只有一份原生状态（句柄、当前坐标、缓冲/程序区、单位与轴映射），
    /// 谁都不能在自己那侧悄悄再 init/close 一次——曾经的教训：数据点通道与工艺通道各连一次，
    /// 任一方断开都会掐断对方正在用的通道。连接由组合根建立一次，注入给需要的人。
    /// </para>
    /// <para>
    /// 实现必须<b>幂等</b>：重复 <see cref="Connect"/> 不报错，重复 <see cref="Disconnect"/> 不报错。
    /// </para>
    /// </remarks>
    public interface IMotionCardLink
    {
        /// <summary>卡是否在线（事实来源是连接持有者，不是调用方的局部状态）。</summary>
        bool IsConnected { get; }

        /// <summary>
        /// 建立连接（幂等）。
        /// </summary>
        /// <param name="endpoint">
        /// 连接串：网口填 IP，串口填串口号，本机板卡填空串。具体解释权在实现方。
        /// </param>
        void Connect(string endpoint);

        /// <summary>关闭连接（幂等）。<b>只有组合根该调它。</b></summary>
        void Disconnect();
    }
}

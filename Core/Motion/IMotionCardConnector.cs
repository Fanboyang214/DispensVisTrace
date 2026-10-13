namespace Core.Motion
{
    /// <summary>
    /// 运动控制卡的"请求连接"窄接口：<b>只能要求连接，不能断开</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存在的意义是把"连"和"断"拆开授权。数据点驱动需要一个"我来了，确保卡是通的"能力
    /// （它的 <c>ConnectAsync</c> 会被采集器反复调用，采集失败还会不断重试），
    /// 但它<b>绝不该</b>有"随手把卡关掉"的能力——一张卡上可能正跑着另一条通道的轨迹。
    /// </para>
    /// <para>
    /// 关闭只归连接持有者（<see cref="IMotionCardLink.Disconnect"/>，由组合根在进程退出 / 热重载时调用）。
    /// </para>
    /// <para>
    /// 实现必须<b>幂等</b>：已连接时 <see cref="EnsureConnected"/> 只做校验，绝不真的再连一次；
    /// 已连着 A 却要求连 B 应直接抛异常，而不是悄悄换通道。
    /// </para>
    /// </remarks>
    public interface IMotionCardConnector
    {
        /// <summary>卡是否已连接。</summary>
        bool IsConnected { get; }

        /// <summary>
        /// 确保已连接到 <paramref name="endpoint"/>（幂等，重复调用不产生第二个连接）。
        /// </summary>
        /// <param name="endpoint">连接串（网口为 IP）。</param>
        /// <exception cref="System.InvalidOperationException">
        /// 已连接但 endpoint 不同：不允许悄悄改连。
        /// </exception>
        void EnsureConnected(string endpoint);
    }
}

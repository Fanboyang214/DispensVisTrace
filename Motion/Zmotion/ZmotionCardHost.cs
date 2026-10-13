using Core.Motion;
using Core.Motion.Zmotion;
using System;
using System.Collections.Generic;

namespace Motion.Zmotion
{
    /// <summary>
    /// 正运动卡的<b>进程内唯一连接宿主</b>：谁要连都从这里拿，谁都不许自己连。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 要解决的问题：数据点驱动（<c>IProtocolDriver</c>）与工艺通道（<see cref="ZmotionCardService"/>）
    /// 都要用同一张卡。一张卡只有一份原生状态（句柄、坐标、程序区、VR、单位与轴映射），
    /// <b>各连一次 = 两个句柄打同一个控制器</b>，表现是"我下发的程序不生效""状态读出来是别人改的"，
    /// 而且任一方 Close 都会掐断对方正在用的通道。
    /// </para>
    /// <para><b>四道保证（从强到弱）</b>：
    /// <list type="number">
    /// <item><b>能力收窄（编译期）</b>：<see cref="ZmotionCardService"/> 只拿 <see cref="IZmotionCard"/>
    /// （读 VR / 下载 / 启停，<b>没有 Open</b>）；数据点驱动只拿 <see cref="IMotionCardConnector"/>
    /// （幂等 <see cref="EnsureConnected"/>，<b>没有 Close</b>）。"各连一次"和"随手关掉对方"都写不出来。</item>
    /// <item><b>进程内唯一</b>：<see cref="ForEndpoint"/> 以 endpoint 为键，同一 IP 永远返回同一个宿主，
    /// 背后只有一个句柄——即使有人到处 new 也不会再多一个连接。</item>
    /// <item><b>幂等 Open</b>：已连接时 <see cref="EnsureConnected"/> 只校验 endpoint，绝不再连；
    /// 已连着 A 却要求连 B 直接抛异常（防"悄悄换通道"）。</item>
    /// <item><b>可验证</b>：<see cref="OpenCount"/> 记录真正 Open 过几次，联调断言它恒为 1。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>关闭</b>只发生在宿主释放时（<see cref="Dispose"/>，或组合根显式调
    /// <see cref="IMotionCardLink.Disconnect"/>）。数据点驱动自己的 <c>DisconnectAsync</c> 必须是 no-op——
    /// 采集器在采集失败时会调它，那里一旦真关，正在跑轨迹的工艺通道就被端掉了（这个坑踩过）。
    /// </para>
    /// <para>
    /// <b>组合根接线</b>（DI 容器注册为单例）：
    /// <c>AddSingleton(_ =&gt; ZmotionCardHost.ForEndpoint(ip, () =&gt; new ZauxZmotionApi()))</c>；
    /// 数据点驱动注入 <c>host</c>（当作 <see cref="IMotionCardConnector"/> 用），
    /// 工艺通道注入 <c>host.Card</c>。
    /// </para>
    /// </remarks>
    public sealed class ZmotionCardHost : IMotionCardLink, IMotionCardConnector, IZmotionCard, IDisposable
    {
        private static readonly object RegistryGate = new object();

        private static readonly Dictionary<string, ZmotionCardHost> Registry =
            new Dictionary<string, ZmotionCardHost>(StringComparer.OrdinalIgnoreCase);

        private readonly IZmotionApi api;
        private readonly object gate = new object();

        private string? endpoint;
        private int openCount;

        private ZmotionCardHost(IZmotionApi api)
        {
            this.api = api;
        }

        /// <summary>
        /// 取该 endpoint 的<b>进程内唯一</b>宿主。
        /// </summary>
        /// <param name="endpoint">卡地址（网口 IP）。</param>
        /// <param name="apiFactory">真库实现的工厂；<b>只在首次创建时调用一次</b>。</param>
        public static ZmotionCardHost ForEndpoint(string endpoint, Func<IZmotionApi> apiFactory)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("endpoint 不能为空", nameof(endpoint));
            }

            if (apiFactory is null)
            {
                throw new ArgumentNullException(nameof(apiFactory));
            }

            lock (RegistryGate)
            {
                if (Registry.TryGetValue(endpoint, out ZmotionCardHost? existing))
                {
                    return existing;
                }

                var created = new ZmotionCardHost(apiFactory());
                Registry[endpoint] = created;
                return created;
            }
        }

        /// <summary>宿主对应的卡地址（尚未连接时为 <c>null</c>）。</summary>
        public string? Endpoint
        {
            get
            {
                lock (gate)
                {
                    return endpoint;
                }
            }
        }

        /// <summary>
        /// 真正 Open 过几次。<b>联调时应恒为 1</b>；大于 1 说明有人在绕过宿主自己连。
        /// </summary>
        public int OpenCount
        {
            get
            {
                lock (gate)
                {
                    return openCount;
                }
            }
        }

        /// <summary>业务能力视图：交给工艺通道（没有 Open / Close）。</summary>
        public IZmotionCard Card => this;

        // ─────────────── IMotionCardLink：连接生命周期（只有组合根该调） ───────────────

        /// <inheritdoc />
        public bool IsConnected => api.IsConnected;

        /// <inheritdoc />
        public void Connect(string endpoint) => EnsureConnected(endpoint);

        /// <inheritdoc />
        public void Disconnect()
        {
            lock (gate)
            {
                if (!api.IsConnected)
                {
                    return;
                }

                api.Close();
            }
        }

        // ─────────────── IMotionCardConnector：只能要求连，不能断 ───────────────

        /// <inheritdoc />
        public void EnsureConnected(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("endpoint 不能为空", nameof(endpoint));
            }

            lock (gate)
            {
                if (api.IsConnected)
                {
                    if (this.endpoint is not null &&
                        !string.Equals(this.endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"卡已连接到 {this.endpoint}，拒绝改连 {endpoint}（同一张卡只能有一个连接）");
                    }

                    return;   // 幂等：绝不再连一次
                }

                api.Open(endpoint);
                this.endpoint = endpoint;
                openCount++;
            }
        }

        // ─────────────── IZmotionCard：业务能力，全部转给 API 实现 ───────────────

        /// <inheritdoc />
        public void DownloadProgram(string name, string text) => api.DownloadProgram(name, text);

        /// <inheritdoc />
        public bool SupportsProgramReadBack => api.SupportsProgramReadBack;

        /// <inheritdoc />
        public bool ProgramExists(string name) => api.ProgramExists(name);

        /// <inheritdoc />
        public void SetCurrentProgram(string name) => api.SetCurrentProgram(name);

        /// <inheritdoc />
        public void Run() => api.Run();

        /// <inheritdoc />
        public void Stop() => api.Stop();

        /// <inheritdoc />
        public void WriteVr(int index, float value) => api.WriteVr(index, value);

        /// <inheritdoc />
        public float ReadVr(int index) => api.ReadVr(index);

        // ─────────────── IZmotionCard：轴状态与单轴运动（数据点驱动读点 / jog 用） ───────────────

        /// <inheritdoc />
        public float GetDpos(int axis) => api.GetDpos(axis);

        /// <inheritdoc />
        public float GetSpeed(int axis) => api.GetSpeed(axis);

        /// <inheritdoc />
        public bool GetIsIdle(int axis) => api.GetIsIdle(axis);

        /// <inheritdoc />
        public bool GetHomeStatus(int axis) => api.GetHomeStatus(axis);

        /// <inheritdoc />
        public void MoveAbsolute(int axis, float position) => api.MoveAbsolute(axis, position);

        /// <inheritdoc />
        public void Jog(int axis, bool forward) => api.Jog(axis, forward);

        /// <inheritdoc />
        public void StopJog(int axis) => api.StopJog(axis);

        // ─────────────── IZmotionCard：单轴参数 / 回零 / 批量状态（单轴调试界面用） ───────────────

        /// <inheritdoc />
        public IReadOnlyList<AxisMotionSnapshot> ReadAxisStates(int axisCount) => api.ReadAxisStates(axisCount);

        /// <inheritdoc />
        public float ReadAxisParameter(int axis, AxisParameter parameter) => api.ReadAxisParameter(axis, parameter);

        /// <inheritdoc />
        public void WriteAxisParameter(int axis, AxisParameter parameter, float value)
            => api.WriteAxisParameter(axis, parameter, value);

        /// <inheritdoc />
        public void MoveRelative(int axis, float distance) => api.MoveRelative(axis, distance);

        /// <inheritdoc />
        public void GoHome(int axis, int mode) => api.GoHome(axis, mode);

        /// <inheritdoc />
        public void SetHomeOffset(int axis, float offset) => api.SetHomeOffset(axis, offset);

        /// <inheritdoc />
        public float GetHomeOffset(int axis) => api.GetHomeOffset(axis);

        /// <summary>
        /// 释放宿主：真正关闭连接，并把本 endpoint 从注册表摘掉（进程退出 / 热重载）。
        /// </summary>
        public void Dispose()
        {
            string? key;

            lock (gate)
            {
                key = endpoint;

                if (api.IsConnected)
                {
                    api.Close();
                }
            }

            if (key is null)
            {
                return;
            }

            lock (RegistryGate)
            {
                if (Registry.TryGetValue(key, out ZmotionCardHost? current) && ReferenceEquals(current, this))
                {
                    Registry.Remove(key);
                }
            }
        }
    }
}

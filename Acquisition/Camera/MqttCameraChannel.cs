using Core.Acquisition;
using Core.Logging;

namespace Acquisition.Camera;

/// <summary>
/// 每台相机的<b>设备侧 MQTT 通道</b>装配：一条 MQTT 连接 + 一个数据点桥。
/// </summary>
/// <remarks>
/// <para>
/// 一台相机 = 一个 <see cref="IDeviceDriver"/>（连自己的 broker，客户端标识取相机名）+
/// 一个 <see cref="MqttCameraBridge"/>（把采集事件映射成 <c>cam/*</c> 数据点）。
/// 两台相机共用一个连接会被 broker 互相顶掉，共用主题会互相覆盖——所以刻意是"一台一路"。
/// </para>
/// <para>
/// <b>驱动由外部注入</b>（<c>Func&lt;CameraSetup, ILogService, IDeviceDriver&gt;</c>）：
/// 实现（MQTTnet）在 Plugins 层，本模块只认识 <see cref="IDeviceDriver"/> 这个 Core 契约，
/// 因此采集模块不依赖任何 MQTT 库——换协议只换组合根里注册的那个工厂。
/// </para>
/// <para>
/// <b>没配 mqtt 段的相机</b>直接跳过（纯本机采集与检测）；<b>某台连不上不阻断其它台</b>，
/// 失败的那台留日志，驱动的后台重连循环会继续尝试。
/// </para>
/// <para>
/// <see cref="StartAsync"/> 必须在相机 <c>StartAsync</c> <b>之前</b>调用：
/// 桥要在第一帧之前订阅成帧事件、并在任何触发到来之前订好 <c>cam/trigger</c>。
/// </para>
/// </remarks>
public sealed class MqttCameraChannel : IAsyncDisposable
{
    private readonly ILogService _log;
    private readonly List<CameraMqttChannel> _channels = new();
    private bool _started;

    /// <param name="topology">相机拓扑（决定哪些相机有 mqtt 段、各自的角色）</param>
    /// <param name="cameras">相机运行时（提供每台相机的采集服务）</param>
    /// <param name="driverFactory">设备侧驱动工厂（组合根注册，MQTTnet 实现）</param>
    /// <param name="log">日志服务</param>
    public MqttCameraChannel(
        CameraTopology topology,
        CameraRuntime cameras,
        Func<CameraSetup, ILogService, IDeviceDriver> driverFactory,
        ILogService log)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(cameras);
        ArgumentNullException.ThrowIfNull(driverFactory);

        ILogService sharedLog = log ?? throw new ArgumentNullException(nameof(log));
        _log = sharedLog.ForContext<MqttCameraChannel>();

        var seenEndpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (CameraSetup setup in topology.Cameras)
        {
            if (setup.Mqtt is null)
            {
                continue;
            }

            string cameraName = setup.Config.CameraName;

            if (!cameras.TryGet(cameraName, out ICameraAcquisitionService? service) || service is null)
            {
                _log.Error("{Camera} 配了 mqtt 段但相机运行时里没有它，MQTT 通道跳过", cameraName);
                continue;
            }

            // 主题 = 数据点地址（不带前缀），所以两台相机连同一个 broker 会在 cam/trigger 上撞车。
            // 这种配置错不会有任何报错，只会表现成"触发乱套"，必须在这里留下警告
            if (seenEndpoints.TryGetValue(setup.Mqtt.Endpoint, out string? other))
            {
                _log.Warn(
                    "{Camera} 与 {Other} 用的是同一个 MQTT broker（{Endpoint}），而主题不带相机名前缀：" +
                    "两台的 cam/trigger、cam/frame-ready 会互相覆盖；请给每台相机各自的 broker，或与上位机另行约定主题前缀",
                    cameraName, other, setup.Mqtt.Endpoint);
            }
            else
            {
                seenEndpoints[setup.Mqtt.Endpoint] = cameraName;
            }

            IDeviceDriver driver;
            try
            {
                driver = driverFactory(setup, sharedLog);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "{Camera} 的设备侧 MQTT 驱动创建失败，该相机不上报资源树数据点", cameraName);
                continue;
            }

            var bridge = new MqttCameraBridge(
                driver,
                service,
                sharedLog,
                CameraDataPoints.ImageUrlFor(setup.Role));

            _channels.Add(new CameraMqttChannel(cameraName, setup.Mqtt, driver, bridge));
        }
    }

    /// <summary>已接入 MQTT 的相机名（诊断 / 自检用）。</summary>
    public IReadOnlyList<string> Cameras => _channels.Select(c => c.CameraName).ToList();

    /// <summary>接入 MQTT 的相机台数。</summary>
    public int Count => _channels.Count;

    /// <summary>取某台相机的桥；没接入时返回 null。</summary>
    public MqttCameraBridge? BridgeOf(string cameraName) =>
        _channels.FirstOrDefault(c => string.Equals(c.CameraName, cameraName, StringComparison.OrdinalIgnoreCase))?.Bridge;

    /// <summary>取某台相机的驱动；没接入时返回 null。</summary>
    public IDeviceDriver? DriverOf(string cameraName) =>
        _channels.FirstOrDefault(c => string.Equals(c.CameraName, cameraName, StringComparison.OrdinalIgnoreCase))?.Driver;

    /// <summary>
    /// 连接各台相机的 broker 并订好触发点（幂等）。
    /// </summary>
    /// <remarks>一台失败不影响其它台：失败的那台由驱动的后台循环自愈。</remarks>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        if (_channels.Count == 0)
        {
            _log.Info("没有相机配置 mqtt 段，资源树数据点不上报");
            return;
        }

        _log.Info("MQTT 通道启动：{Count} 台相机（{Cameras}）", _channels.Count, string.Join("、", Cameras));

        foreach (CameraMqttChannel channel in _channels)
        {
            try
            {
                await channel.Driver.ConnectAsync(channel.Link.Endpoint, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 连不上也要继续把桥接上：桥的订阅会被驱动登记，重连成功后自动补订，
                // 期间采集照常（上报失败由桥逐条记警告）
                _log.Error(ex, "{Camera} 连接 MQTT broker 失败（{Endpoint}），后台重连中；采集不受影响",
                    channel.CameraName, channel.Link.Endpoint);
            }

            try
            {
                await channel.Bridge.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "{Camera} 的 MQTT 数据点桥接失败，该相机不上报", channel.CameraName);
            }
        }
    }

    /// <summary>断开全部 MQTT 通道（幂等）。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        foreach (CameraMqttChannel channel in _channels)
        {
            try
            {
                await channel.Bridge.DisconnectAsync().ConfigureAwait(false);
                await channel.Driver.DisconnectAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "{Camera} 断开 MQTT 通道失败，继续断其它相机", channel.CameraName);
            }
        }

        _started = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (CameraMqttChannel channel in _channels)
        {
            try
            {
                await channel.Bridge.DisposeAsync().ConfigureAwait(false);
                await channel.Driver.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "{Camera} 释放 MQTT 通道失败（忽略）", channel.CameraName);
            }
        }

        _channels.Clear();
        _started = false;
    }

    /// <summary>一台相机的 MQTT 通道三件套。</summary>
    private sealed record CameraMqttChannel(
        string CameraName,
        MqttDeviceLink Link,
        IDeviceDriver Driver,
        MqttCameraBridge Bridge);
}

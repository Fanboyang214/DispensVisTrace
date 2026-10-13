using Core.Acquisition;
using Core.Logging;

namespace Acquisition.Camera;

/// <summary>
/// 相机通道的运行时：把 <see cref="CameraTopology"/> 变成"已连接并 Armed 等触发的相机"。
/// </summary>
/// <remarks>
/// <para>
/// 一台相机一个 <see cref="ICameraAcquisitionService"/>，它们<b>共用同一个
/// <see cref="ProductCodeGenerator"/></b>——两台相机拍的必须是同一件产品的同一个条码，
/// 各造一个发生器会让同一件产品在两张图上出现两个条码。
/// </para>
/// <para>
/// <b>驱动与服务在构造期就建好</b>（都是纯对象，不碰设备）：这样 <see cref="Cameras"/> 从头到尾稳定，
/// 帧→视觉的桥可以在 <see cref="StartAsync"/> 之前订阅，不必关心装配顺序。
/// </para>
/// <para>
/// <b>一台失败不阻断其它台</b>：另一台可能照样拍得到图，一台连不上就整线不采集反而更糟；
/// 失败的那台会置 <see cref="CameraState.Error"/> 并留下日志，是否停机由上层决定。
/// </para>
/// </remarks>
public sealed class CameraRuntime : IAsyncDisposable
{
    private readonly CameraTopology _topology;
    private readonly ILogService _log;
    private readonly List<CameraChannel> _channels = new();
    private readonly Dictionary<string, CameraChannel> _byName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<ICameraAcquisitionService> _cameras;

    private bool _started;

    /// <param name="topology">相机拓扑（cameras.json）</param>
    /// <param name="log">日志服务</param>
    /// <param name="productCodes">产品条码发生器，<b>全局一个</b>，两台相机共用</param>
    /// <param name="driverFactory">驱动工厂；缺省用 <see cref="CameraDriverFactory.Create"/>（自检时替换成替身）</param>
    public CameraRuntime(
        CameraTopology topology,
        ILogService log,
        ProductCodeGenerator productCodes,
        Func<CameraConfig, ILogService, ICameraDriver>? driverFactory = null)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        ArgumentNullException.ThrowIfNull(productCodes);

        ILogService sharedLog = log ?? throw new ArgumentNullException(nameof(log));
        _log = sharedLog.ForContext<CameraRuntime>();

        Func<CameraConfig, ILogService, ICameraDriver> factory = driverFactory ?? CameraDriverFactory.Create;

        foreach (CameraSetup setup in topology.Cameras)
        {
            ICameraDriver driver = factory(setup.Config, sharedLog);
            var service = new CameraAcquisitionService(driver, sharedLog, productCodes);

            var channel = new CameraChannel(setup, service);
            _channels.Add(channel);
            _byName[setup.Config.CameraName] = channel;
        }

        _cameras = _channels.Select(c => c.Service).ToList();
    }

    /// <summary>本机全部相机服务（顺序与 cameras.json 一致）。</summary>
    public IReadOnlyList<ICameraAcquisitionService> Cameras => _cameras;

    /// <summary>拓扑里配置的相机台数。</summary>
    public int Count => _channels.Count;

    /// <summary>取某台相机的产线角色；没这台相机时返回 null。</summary>
    public CameraRole? RoleOf(string cameraName) =>
        _byName.TryGetValue(cameraName, out CameraChannel? channel) ? channel.Setup.Role : null;

    /// <summary>按相机名取采集服务。</summary>
    public bool TryGet(string cameraName, out ICameraAcquisitionService? service)
    {
        if (_byName.TryGetValue(cameraName, out CameraChannel? channel))
        {
            service = channel.Service;
            return true;
        }

        service = null;
        return false;
    }

    /// <summary>
    /// 连接全部相机并进入 Armed（幂等：已启动时直接返回）。
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _log.Info("相机通道启动：{Count} 台，图像目录 {Directory}", _channels.Count, _topology.Storage.RootDirectory);

        foreach (CameraChannel channel in _channels)
        {
            try
            {
                await channel.Service.StartAsync(channel.Setup.Config, _topology.Storage, ct)
                                   .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 这台已是 Error 状态，原因在异常里；其余相机继续启动
                _log.Error(ex, "{Camera} 启动采集失败，其余相机继续", channel.Setup.Config.CameraName);
            }
        }
    }

    /// <summary>停止全部相机并断开（幂等）。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        foreach (CameraChannel channel in _channels)
        {
            try
            {
                await channel.Service.StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "{Camera} 停止采集失败，继续停其它相机", channel.Setup.Config.CameraName);
            }
        }

        _started = false;
    }

    /// <summary>
    /// 给某台相机发一次软触发（调试补拍、或上位机经 MQTT 触发时的入口）。
    /// </summary>
    /// <remarks>硬触发模式下相机不响应软触发，采集服务会记一条警告后忽略。</remarks>
    /// <exception cref="InvalidOperationException">没有这台相机</exception>
    public Task TriggerAsync(string cameraName, CancellationToken ct)
    {
        if (!TryGet(cameraName, out ICameraAcquisitionService? service) || service is null)
        {
            throw new InvalidOperationException($"没有名为 {cameraName} 的相机（见 config/cameras.json）");
        }

        return service.SoftwareTriggerAsync(ct);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    /// <summary>一台相机：配置 + 服务。</summary>
    private sealed record CameraChannel(CameraSetup Setup, ICameraAcquisitionService Service);
}

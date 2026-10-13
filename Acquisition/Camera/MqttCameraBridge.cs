using Core.Acquisition;
using Core.Logging;

namespace Acquisition.Camera;

/// <summary>
/// 一台相机的 MQTT 通道：把资源树里的 <c>cam/*</c> 数据点接到采集服务上。
/// </summary>
/// <remarks>
/// <para>
/// <b>主题就是数据点地址，不加任何前缀</b>（<c>cam/trigger</c> 就是 <c>cam/trigger</c>，
/// 见 <see cref="CameraDataPoints"/>）。宿主（采集宿主 / 上位机）用 <c>IProtocolDriver</c>
/// 按资源树的 <c>TagPoint.Address</c> 逐字订阅与写入，两侧多一个字符都对不上，而且两边都不会报错
/// ——只会表现成"上位机点了触发，相机没反应"、"图像就绪了但上位机看不到"。
/// </para>
/// <para>
/// <b>同 broker 上区分设备靠的是每条连接</b>：每台相机连自己的 broker
/// （cameras.json 的 <c>mqtt.endpoint</c>）。所以<b>不要</b>为了让两台相机共用一个 broker
/// 而给主题加 <c>相机名/</c> 前缀——那需要同时改宿主侧的订阅映射，只改设备侧等于把线剪断。
/// </para>
/// <para>
/// 它只订阅采集服务的事件，不被采集服务反向依赖。连接与订阅由调用方先建立
/// （<see cref="ConnectAsync"/>，见 <see cref="MqttCameraChannel"/>）。
/// </para>
/// <para>
/// 本类只负责<b>采集侧</b>产出的数据点（触发状态、帧就绪、图像 URL）；
/// 置信度、检测完成等由视觉侧通过 <c>IDataPointPublisher</c> 走同一条连接上报
/// （见 <c>DeviceDataPointPublisher</c>）。
/// </para>
/// </remarks>
public sealed class MqttCameraBridge : IAsyncDisposable
{
    private readonly IDeviceDriver _driver;
    private readonly ICameraAcquisitionService _acquisition;
    private readonly ILogService _log;
    private readonly string _imageUrlDataPoint;
    private bool _connected;

    /// <param name="driver">设备侧 MQTT 驱动（地址即主题）</param>
    /// <param name="acquisition">这台相机的采集服务</param>
    /// <param name="log">日志服务</param>
    /// <param name="imageUrlDataPoint">
    /// 图像 URL 数据点：Camera01 用 <see cref="CameraDataPoints.LocateImageUrl"/>，
    /// Camera02 用 <see cref="CameraDataPoints.InspectImageUrl"/>（见 <see cref="CameraDataPoints.ImageUrlFor"/>）。
    /// </param>
    public MqttCameraBridge(
        IDeviceDriver driver,
        ICameraAcquisitionService acquisition,
        ILogService log,
        string imageUrlDataPoint)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<MqttCameraBridge>();

        ArgumentException.ThrowIfNullOrWhiteSpace(imageUrlDataPoint);
        _imageUrlDataPoint = imageUrlDataPoint;
    }

    /// <summary>这台相机的名字（诊断用）。</summary>
    public string CameraName => _acquisition.CameraName;

    /// <summary>图像 URL 数据点（诊断 / 自检用）。</summary>
    public string ImageUrlDataPoint => _imageUrlDataPoint;

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_connected)
        {
            return;
        }

        _driver.DataPointReceived += OnDataPointReceived;
        _acquisition.FrameCaptured += OnFrameCaptured;
        _acquisition.StateChanged += OnStateChanged;
        _acquisition.ErrorOccurred += OnErrorOccurred;

        await _driver.SubscribeAsync(CameraDataPoints.Trigger, ct).ConfigureAwait(false);
        _connected = true;

        _log.Info("{Camera} 的 MQTT 通道已接通（驱动 {Driver}，触发点 {Trigger}）",
            _acquisition.CameraName, _driver.DriverType, CameraDataPoints.Trigger);
    }

    public async Task DisconnectAsync()
    {
        if (!_connected)
        {
            return;
        }

        _connected = false;
        _driver.DataPointReceived -= OnDataPointReceived;
        _acquisition.FrameCaptured -= OnFrameCaptured;
        _acquisition.StateChanged -= OnStateChanged;
        _acquisition.ErrorOccurred -= OnErrorOccurred;

        await _driver.UnsubscribeAsync(CameraDataPoints.Trigger, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>往资源树数据点写一个值（主题 = 地址本身）。</summary>
    private void Publish(string dataPoint, object value)
    {
        // 回报失败不能影响采集，也不能变成未观察异常
        _ = _driver.PublishAsync(dataPoint, value, CancellationToken.None)
                   .ContinueWith(t => _log.Warn(t.Exception, "MQTT 上报失败：{DataPoint}", dataPoint),
                                 CancellationToken.None,
                                 TaskContinuationOptions.OnlyOnFaulted,
                                 TaskScheduler.Default);
    }

    private void OnDataPointReceived(object? sender, DataPointReceivedEventArgs e)
    {
        if (e.Address != CameraDataPoints.Trigger)
            return;

        // 上位机可能发裸的 true，也可能发 JSON 字符串 "true"，两种都收
        var payload = e.Payload.Trim().Trim('"');

        if (payload.Equals("true", StringComparison.OrdinalIgnoreCase) || payload == "1")
        {
            // 采集服务内部会判断当前是否允许软触发，这里只负责把触发事件送进去
            _ = _acquisition.SoftwareTriggerAsync(CancellationToken.None)
                            .ContinueWith(t => _log.Warn(t.Exception, "软触发处理失败"),
                                          CancellationToken.None,
                                          TaskContinuationOptions.OnlyOnFaulted,
                                          TaskScheduler.Default);
            return;
        }

        // false/0/空串是上位机在复位触发状态，静默忽略；
        // 其它值说明两边格式对不上，必须留下痕迹——否则表现为"触发没反应"却查不到原因
        if (payload.Length > 0
            && !payload.Equals("false", StringComparison.OrdinalIgnoreCase)
            && payload != "0")
        {
            _log.Warn("{Camera} 收到无法识别的触发载荷：{Payload}", _acquisition.CameraName, e.Payload);
        }
    }

    private void OnFrameCaptured(object? sender, CameraFrameEventArgs e)
    {
        Publish(CameraDataPoints.TriggerSource, e.TriggerSource);
        Publish(CameraDataPoints.TriggerCount, e.SequenceNumber);
        Publish(_imageUrlDataPoint, e.ImagePath);

        // FrameReady 放在最后：上位机看到它时，路径已经就绪
        Publish(CameraDataPoints.FrameReady, true);
    }

    private void OnStateChanged(object? sender, CameraState state)
    {
        if (state is not CameraState.Armed)
            return;

        // ADR-0009：进入 Armed 时把触发状态复位，TriggerError 空串表示无错误
        Publish(CameraDataPoints.FrameReady, false);
        Publish(CameraDataPoints.TriggerError, string.Empty);
    }

    private void OnErrorOccurred(object? sender, CameraErrorEventArgs e)
        => Publish(CameraDataPoints.TriggerError, e.Message);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }
}

using Core.Acquisition;
using Core.Logging;

namespace Acquisition;

/// <summary>
/// 一台相机的 MQTT 通道：把资源树里的 <c>cam/*</c> 数据点接到采集服务上。
/// <para>
/// 主题 = <c>{相机名}/{数据点地址}</c>，例如 <c>Camera01/cam/trigger-count</c>。
/// 产线上两台相机连的是**同一个 broker**，而 <c>cam/trigger</c>、<c>cam/frame-ready</c>、
/// <c>cam/trigger-count</c> 这些地址两台相机是共用的——不加相机名的话，一次 <c>cam/trigger</c>
/// 会同时触发两台相机，两台的帧计数也会互相覆盖，而且不会有任何报错。
/// 相机名直接用采集服务的 <see cref="ICameraAcquisitionService.CameraName"/>，不另外配置，避免写错。
/// </para>
/// <para>
/// 它只订阅采集服务的事件，不被采集服务反向依赖。MQTT 连接由调用方先建立
/// （<see cref="IDeviceDriver.ConnectAsync"/>）。
/// </para>
/// </summary>
public sealed class MqttCameraBridge : IAsyncDisposable
{
    /// <summary>软件触发写入点，也回读最新触发状态。</summary>
    private const string TriggerDataPoint = "cam/trigger";
    private const string FrameReadyDataPoint = "cam/frame-ready";
    private const string TriggerCountDataPoint = "cam/trigger-count";
    private const string TriggerSourceDataPoint = "cam/trigger-source";
    private const string TriggerErrorDataPoint = "cam/trigger-error";
    private const string ProductIdDataPoint = "cam/product-id";

    private readonly IDeviceDriver _driver;
    private readonly ICameraAcquisitionService _acquisition;
    private readonly ILogService _log;
    private readonly string _imageUrlDataPoint;
    private bool _connected;

    /// <param name="imageUrlDataPoint">
    /// 图像 URL 数据点：Camera01 用 <c>cam/locate-image-url</c>，Camera02 用 <c>cam/inspect-image-url</c>。
    /// 从相机名猜不可靠，由调用方按资源树定义显式给。
    /// </param>
    public MqttCameraBridge(
        IDeviceDriver driver,
        ICameraAcquisitionService acquisition,
        ILogService log,
        string imageUrlDataPoint)
    {
        _driver = driver;
        _acquisition = acquisition;
        _log = log.ForContext<MqttCameraBridge>();
        _imageUrlDataPoint = imageUrlDataPoint;
    }

    /// <summary>资源树数据点地址 → 实际 MQTT 主题。</summary>
    private string TopicFor(string dataPoint) => $"{_acquisition.CameraName}/{dataPoint}";

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_connected) return;

        _driver.DataPointReceived += OnDataPointReceived;
        _acquisition.FrameCaptured += OnFrameCaptured;
        _acquisition.StateChanged += OnStateChanged;
        _acquisition.ErrorOccurred += OnErrorOccurred;

        await _driver.SubscribeAsync(TopicFor(TriggerDataPoint), ct);
        _connected = true;
    }

    public async Task DisconnectAsync()
    {
        if (!_connected) return;

        _connected = false;
        _driver.DataPointReceived -= OnDataPointReceived;
        _acquisition.FrameCaptured -= OnFrameCaptured;
        _acquisition.StateChanged -= OnStateChanged;
        _acquisition.ErrorOccurred -= OnErrorOccurred;

        await _driver.UnsubscribeAsync(TopicFor(TriggerDataPoint), CancellationToken.None);
    }

    /// <summary>往资源树数据点写一个值。</summary>
    private void Publish(string dataPoint, object value)
    {
        var topic = TopicFor(dataPoint);

        // 回报失败不能影响采集，也不能变成未观察异常
        _ = _driver.PublishAsync(topic, value, CancellationToken.None)
                   .ContinueWith(t => _log.Warn(t.Exception, "MQTT 上报失败：{DataPoint}", topic),
                                 CancellationToken.None,
                                 TaskContinuationOptions.OnlyOnFaulted,
                                 TaskScheduler.Default);
    }

    private void OnDataPointReceived(object? sender, DataPointReceivedEventArgs e)
    {
        if (e.Address != TopicFor(TriggerDataPoint))
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
        Publish(TriggerSourceDataPoint, e.TriggerSource.ToString().ToLowerInvariant());
        Publish(TriggerCountDataPoint, e.SequenceNumber);
        Publish(_imageUrlDataPoint, e.ImagePath);

        // 条码随帧上报，供上位机做图像与产品的追溯关联
        Publish(ProductIdDataPoint, e.ProductId ?? string.Empty);

        // FrameReady 放在最后：上位机看到它时，路径和条码都已经就绪
        Publish(FrameReadyDataPoint, true);
    }

    private void OnStateChanged(object? sender, CameraState state)
    {
        if (state is not CameraState.Armed)
            return;

        // ADR-0009：进入 Armed 时把触发状态复位，TriggerError 空串表示无错误
        Publish(FrameReadyDataPoint, false);
        Publish(TriggerErrorDataPoint, string.Empty);
    }

    private void OnErrorOccurred(object? sender, CameraErrorEventArgs e)
        => Publish(TriggerErrorDataPoint, e.Message);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}

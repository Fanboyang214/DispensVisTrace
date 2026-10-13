using Core.Acquisition;
using Core.Bridge;
using Core.Logging;

namespace Acquisition.Camera;

/// <summary>
/// 把"往某台设备的数据点写一个值"落到那台相机的 MQTT 连接上。
/// </summary>
/// <remarks>
/// <para>
/// 这是资源树数据点的<b>统一出口</b>：视觉侧（定位/检测结果）不需要认识 MQTT、
/// 也不需要认识 <see cref="IDeviceDriver"/>，只依赖 Core 的 <see cref="IDataPointPublisher"/>
/// 说"我给 Camera02 写 cam/inspect-complete"；具体连的哪条连接、地址怎么变成主题，
/// 由本类按相机名找到 <see cref="MqttCameraChannel"/> 里那条连接去做。
/// </para>
/// <para>
/// <b>发布是非阻塞的</b>（接口返回 void）：调用方在相机回调线程或引擎线程上，
/// 不能因为 broker 慢/断而卡住采集或检测；失败逐条记警告，绝不静默。
/// </para>
/// <para>
/// 某台相机没配 mqtt 段时，往它写数据点属于"配置与调用不一致"，
/// 每个设备只警告一次——否则每帧刷一条日志，反而看不见真正的问题。
/// </para>
/// </remarks>
public sealed class DeviceDataPointPublisher : IDataPointPublisher
{
    private readonly MqttCameraChannel _channel;
    private readonly ILogService _log;
    private readonly HashSet<string> _warnedDevices = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="channel">相机 MQTT 通道（按相机名查驱动）</param>
    /// <param name="log">日志服务</param>
    public DeviceDataPointPublisher(MqttCameraChannel channel, ILogService log)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<DeviceDataPointPublisher>();
    }

    /// <inheritdoc />
    public void Publish(string deviceName, string dataPoint, object value)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(dataPoint))
        {
            return;
        }

        IDeviceDriver? driver = _channel.DriverOf(deviceName);
        if (driver is null)
        {
            WarnOnce(deviceName, dataPoint);
            return;
        }

        _ = driver.PublishAsync(dataPoint, value, CancellationToken.None)
                  .ContinueWith(
                      t => _log.Warn(t.Exception, "{Device} 的数据点 {DataPoint} 上报失败", deviceName, dataPoint),
                      CancellationToken.None,
                      TaskContinuationOptions.OnlyOnFaulted,
                      TaskScheduler.Default);
    }

    private void WarnOnce(string deviceName, string dataPoint)
    {
        lock (_warnedDevices)
        {
            if (!_warnedDevices.Add(deviceName))
            {
                return;
            }
        }

        _log.Warn("{Device} 没有 MQTT 通道（cameras.json 里没配 mqtt 段），数据点 {DataPoint} 不会上报",
            deviceName, dataPoint);
    }
}

using Core.Acquisition;
using Core.Logging;
using MvCameraControl;

namespace Acquisition;

/// <summary>
/// 海康 MVS 相机驱动（MvCameraControl.Net 的面向对象 API）。
/// </summary>
/// <remarks>
/// <para>
/// 用厂商 SDK 而不是 HALCON 采集接口的原因：硬触发配置（TriggerMode / TriggerSource / 触发输入线）
/// 是 ADR-0007 的关键路径，MVS 按 GenICam 节点名配置、报错明确；而且 <c>FrameGrabedEvent</c> 是推模型，
/// 与 <see cref="ICameraDriver.FrameCaptured"/> 一一对应，不用自己起抓图线程。HALCON 留在图像处理侧。
/// </para>
/// <para>
/// MVS 的托管 API 全是同步的（返回错误码），没有 async 版本，所以这里直接调用并返回已完成的 Task，
/// 不假造一层 async——异步边界由 <see cref="ICameraAcquisitionService"/> 那一层负责。
/// </para>
/// <para>
/// 帧回调在 SDK 的线程上同步派发，<see cref="ICameraAcquisitionService"/> 会在回调里直接把 BMP 写盘。
/// 按点胶产线一帧/几秒的节拍，这点写入耗时可以忽略；若将来要跑连续高速采集，
/// 需要把落盘挪到独立队列（ponytail: 已知上限，升级路径=服务侧加一个有界写盘队列）。
/// </para>
/// </remarks>
public sealed class HikvisionMvsCameraDriver : ICameraDriver
{
    /// <summary>产品到位信号接的相机输入线。现场改接到 Line1/Line2 时只改这一处。</summary>
    private const string HardwareTriggerLine = "Line0";

    private readonly ILogService _log;
    private IDevice? _device;
    private TriggerSource _triggerSource = TriggerSource.Hardware;
    private bool _pixelTypeWarned;

    public bool IsConnected => _device?.IsConnected ?? false;

    public event EventHandler<CameraFrameData>? FrameCaptured;
    public event EventHandler<CameraErrorEventArgs>? ErrorOccurred;


    public string CameraName { get; }

    public CameraInfo? Info { get; private set; }
    public HikvisionMvsCameraDriver(string cameraName, ILogService log)
    {
        CameraName = cameraName;
        _log = log.ForContext<HikvisionMvsCameraDriver>();
    }

    

    public Task OpenAsync(CameraConfig config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(config);

        if (_device is not null)
            throw new InvalidOperationException($"{CameraName} 已经打开，请先 CloseAsync");

        var deviceInfo = FindDevice(config.SerialNumber);
        var device = DeviceFactory.CreateDevice(deviceInfo);

        EnsureOk(device.Open(), $"打开相机 {deviceInfo.SerialNumber}");

        try
        {
            ApplyImagingSettings(device, config);

            // 订阅放最后：前面任何一步失败都不会留下挂在回调上的半连接状态
            device.StreamGrabber.FrameGrabedEvent += OnFrameGrabbed;
            device.DeviceExceptionEvent += OnDeviceException;
        }
        catch
        {
            CloseDevice(device);
            throw;
        }

        _device = device;
        Info = new CameraInfo(
            CameraName,
            deviceInfo.ManufacturerName,
            deviceInfo.ModelName,
            deviceInfo.SerialNumber,
            deviceInfo.DeviceVersion);

        _log.Info("{Camera} 已连接：{Vendor} {Model} SN={Serial} 固件 {Firmware}",
            CameraName, Info.Vendor, Info.Model, Info.SerialNumber, Info.FirmwareVersion);

        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct)
    {
        var device = _device;
        if (device is null)
            return Task.CompletedTask;

        device.StreamGrabber.FrameGrabedEvent -= OnFrameGrabbed;
        device.DeviceExceptionEvent -= OnDeviceException;

        CloseDevice(device);

        _device = null;
        Info = null;
        _pixelTypeWarned = false;

        _log.Info("{Camera} 已断开", CameraName);
        return Task.CompletedTask;
    }

    public Task SetTriggerSourceAsync(TriggerSource source, CancellationToken ct)
    {
        var parameters = RequireDevice().Parameters;

        switch (source)
        {
            case TriggerSource.Hardware:
                // 产品到位信号 → PLC 脉冲 → 相机触发输入线，由相机自己出图，不依赖上位机实时性
                EnsureOk(parameters.SetEnumValueByString("TriggerMode", "On"), "打开触发模式");
                EnsureOk(parameters.SetEnumValueByString("TriggerSource", HardwareTriggerLine),
                    $"把触发源设为 {HardwareTriggerLine}（产品到位信号所接的输入线）");
                break;

            case TriggerSource.Software:
                EnsureOk(parameters.SetEnumValueByString("TriggerMode", "On"), "打开触发模式");
                EnsureOk(parameters.SetEnumValueByString("TriggerSource", "Software"), "把触发源设为 Software");
                break;

            case TriggerSource.FreeRun:
                EnsureOk(parameters.SetEnumValueByString("TriggerMode", "Off"), "关闭触发模式（自由运行）");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, "未知的触发方式");
        }

        _triggerSource = source;
        _log.Info("{Camera} 触发方式已设为 {Trigger}", CameraName, source);
        return Task.CompletedTask;
    }

    public Task SoftwareTriggerAsync(CancellationToken ct)
    {
        EnsureOk(RequireDevice().Parameters.SetCommandValue("TriggerSoftware"), "下发扬触发命令");
        return Task.CompletedTask;
    }

    public Task StartCaptureAsync(CancellationToken ct)
    {
        EnsureOk(RequireDevice().StreamGrabber.StartGrabbing(), "开始采集");
        return Task.CompletedTask;
    }

    public Task StopCaptureAsync(CancellationToken ct)
    {
        EnsureOk(RequireDevice().StreamGrabber.StopGrabbing(), "停止采集");
        return Task.CompletedTask;
    }

    /// <summary>按序列号挑相机。配错序列号时把在线相机都列出来，省得现场一台台试。</summary>
    private IDeviceInfo FindDevice(string serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
            throw new InvalidOperationException($"{CameraName} 未配置相机序列号（CameraConfig.SerialNumber），无法确定连哪台相机");

        var devices = new List<IDeviceInfo>();
        //枚举gige和usb相机
        var code = DeviceEnumerator.EnumDevices(
            DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice, out devices);

        if (code != MvError.MV_OK)
        {
            // 实测：本机没装 MVS 运行时（只装了 NuGet 包不够）时这里返回 MV_E_RESOURCE，
            // 光报"资源不足"会让人查错方向，所以把现场最常见的几个原因直接写出来
            throw new InvalidOperationException(
                $"枚举相机失败：{Describe(code)}。常见原因：本机未安装 MVS 运行时、" +
                "相机正被 MVS 客户端占用、或网卡与相机不在同一网段");
        }

        var wanted = serialNumber.Trim();
        var match = devices.FirstOrDefault(d =>
            string.Equals(d.SerialNumber, wanted, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            var online = devices.Count == 0
                ? "（一台都没枚举到，检查相机供电/网线与 MVS 客户端是否占用了相机）"
                : string.Join("、", devices.Select(d => $"{d.ModelName}/{d.SerialNumber}"));
            throw new InvalidOperationException($"没有找到序列号为 {wanted} 的相机。当前在线：{online}");
        }

        return match;
    }

    private void ApplyImagingSettings(IDevice device, CameraConfig config)
    {
        var parameters = device.Parameters;

        // 分辨率决定标定系数，配错了必须当场失败，不能静默用相机的默认分辨率
        if (config.Width > 0 && config.Height > 0)
        {
            // 先归零偏移，否则把宽高改大可能超出传感器范围而报错
            parameters.SetIntValue("OffsetX", 0);
            parameters.SetIntValue("OffsetY", 0);
            EnsureOk(parameters.SetIntValue("Width", config.Width), $"设置宽度 {config.Width}");
            EnsureOk(parameters.SetIntValue("Height", config.Height), $"设置高度 {config.Height}");
        }

        if (config.ExposureTimeUs > 0)
            EnsureOk(parameters.SetFloatValue("ExposureTime", (float)config.ExposureTimeUs),
                $"设置曝光 {config.ExposureTimeUs}us");

        EnsureOk(parameters.SetFloatValue("Gain", (float)config.GainDb), $"设置增益 {config.GainDb}dB");

        // 视觉侧按 8bit 单通道消费（HALCON HImage("byte",...)），Bayer/彩色进来会直接算出垃圾结果。
        // 黑白相机没有 PixelFormat 节点，这里失败不算错，逐帧还会再校验一次。
        if (parameters.SetEnumValueByString("PixelFormat", "Mono8") != MvError.MV_OK)
            _log.Debug("{Camera} 没有 PixelFormat 节点（黑白相机常见），按相机当前格式取流", CameraName);
    }

    private void OnFrameGrabbed(object? sender, FrameGrabbedEventArgs e)
    {
        var frame = e.FrameOut;

        try
        {
            var image = frame?.Image;
            if (image is null)
                return;

            if (image.PixelType != MvGvspPixelType.PixelType_Gvsp_Mono8)
            {
                if (!_pixelTypeWarned)
                {
                    _pixelTypeWarned = true;
                    Report($"当前像素格式是 {image.PixelType}，视觉侧按 Mono8 单通道消费，已丢弃该帧；" +
                           "请把相机的 PixelFormat 设为 Mono8", "PixelFormat", isFatal: false);
                }
                return;
            }

            _pixelTypeWarned = false;

            // 回调返回后 SDK 会回收这块缓冲，复制一份再交给上层：
            // 现在上层是同步写盘，但一旦将来改成异步处理就会读到已被复用的内存
            var pixels = (byte[])image.PixelData.Clone();

            FrameCaptured?.Invoke(this, new CameraFrameData(
                CameraName,
                pixels,
                (int)image.Width,
                (int)image.Height,
                8,
                "Mono8",
                frame!.FrameNum,
                DateTime.Now,
                _triggerSource));
        }
        catch (Exception ex)
        {
            // 这里绝不能让异常冒到 SDK 的回调线程上
            Report($"{CameraName} 处理图像帧异常：{ex.Message}", "FrameGrab", isFatal: false);
        }
        finally
        {
            // 必须归还，否则缓冲池很快耗尽、相机不再出图
            frame?.Dispose();
        }
    }

    private void OnDeviceException(object? sender, DeviceExceptionArgs e)
        => Report($"{CameraName} 相机连接异常（{e.MsgType}），需要重连后重新采集", "DeviceException", isFatal: true);

    private IDevice RequireDevice()
        => _device ?? throw new InvalidOperationException($"{CameraName} 尚未打开相机，请先 OpenAsync");

    private void CloseDevice(IDevice device)
    {
        try
        {
            if (device.IsConnected)
                device.StreamGrabber.StopGrabbing();
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "{Camera} 停止采集失败，继续关闭", CameraName);
        }

        try
        {
            device.Close();
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "{Camera} 关闭相机失败", CameraName);
        }
    }

    private void Report(string message, string errorCode, bool isFatal)
    {
        if (isFatal)
            _log.Error("{Camera} {Message}", CameraName, message);
        else
            _log.Warn("{Camera} {Message}", CameraName, message);

        ErrorOccurred?.Invoke(this, new CameraErrorEventArgs(CameraName, errorCode, message, isFatal));
    }

    private static void EnsureOk(int errorCode, string action)
    {
        if (errorCode != MvError.MV_OK)
            throw new InvalidOperationException($"{action}失败：{Describe(errorCode)}");
    }

    /// <summary>把 MVS 错误码翻译成人话；认不出来的保留十六进制码方便照着 SDK 手册查。</summary>
    private static string Describe(int code)
    {
        if (code == MvError.MV_E_HANDLE) return "无效句柄";
        if (code == MvError.MV_E_RESOURCE) return "资源不足";
        if (code == MvError.MV_E_NODATA) return "没有数据";
        if (code == MvError.MV_E_CALLORDER) return "调用顺序错误（相机状态不对）";
        if (code == MvError.MV_E_PARAMETER) return "参数错误";
        if (code == MvError.MV_E_ACCESS_DENIED) return "无访问权限（相机可能被 MVS 客户端占用）";
        if (code == MvError.MV_E_GC_ACCESS) return "节点访问失败（该型号没有这个节点，或当前不可写）";
        if (code == MvError.MV_E_GC_TIMEOUT) return "节点访问超时";
        if (code == MvError.MV_E_USB_DEVICE) return "USB 设备错误";
        return $"MVS 错误码 0x{code:X8}";
    }
}

using Core.Acquisition;

namespace Acquisition;

/// <summary>
/// 仿真相机：没有真实硬件时用来跑通采集 → 落盘 → 回报的整条链路。
/// <see cref="TriggerHardwareFrame"/> 用来模拟"产品到位信号触发相机出图"。
/// </summary>
public sealed class SimulationCameraDriver : ICameraDriver
{
    private readonly string _cameraName;
    private CameraConfig? _config;
    private bool _isConnected;
    private bool _isCapturing;
    private TriggerSource _triggerSource;
    private long _frameCount;
    private readonly Random _rng = new();

    public string CameraName => _cameraName;

    public CameraInfo? Info { get; private set; }

    public bool IsConnected => _isConnected;

    public event EventHandler<CameraFrameData>? FrameCaptured;
    public event EventHandler<CameraErrorEventArgs>? ErrorOccurred;

    public SimulationCameraDriver(string cameraName)
    {
        _cameraName = cameraName;
    }

    public Task OpenAsync(CameraConfig config, CancellationToken ct)
    {
        _config = config;
        _isConnected = true;
        _triggerSource = config.TriggerSource;
        _frameCount = 0;

        Info = new CameraInfo(
            CameraName: _cameraName,
            Vendor: "DispensVisTrace",
            Model: "SimulationCamera",
            SerialNumber: config.SerialNumber,
            FirmwareVersion: "1.0.0");

        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct)
    {
        _isConnected = false;
        _isCapturing = false;
        Info = null;
        return Task.CompletedTask;
    }

    public Task SetTriggerSourceAsync(TriggerSource source, CancellationToken ct)
    {
        _triggerSource = source;
        return Task.CompletedTask;
    }

    public Task SoftwareTriggerAsync(CancellationToken ct)
    {
        if (!_isConnected)
        {
            ErrorOccurred?.Invoke(this, new CameraErrorEventArgs(
                _cameraName, "NotConnected", "相机未打开，软触发被忽略", IsFatal: false));
            return Task.CompletedTask;
        }

        ProduceFrame(TriggerSource.Software);
        return Task.CompletedTask;
    }

    public Task StartCaptureAsync(CancellationToken ct)
    {
        _isCapturing = true;
        return Task.CompletedTask;
    }

    public Task StopCaptureAsync(CancellationToken ct)
    {
        _isCapturing = false;
        return Task.CompletedTask;
    }

    /// <summary>模拟产品到位信号打在相机硬触发输入上：只在采集状态下出图。</summary>
    public void TriggerHardwareFrame()
    {
        if (!_isCapturing)
            return;

        ProduceFrame(TriggerSource.Hardware);
    }

    private void ProduceFrame(TriggerSource source)
    {
        var w = _config?.Width ?? 640;
        var h = _config?.Height ?? 480;
        var data = new byte[w * h];
        _rng.NextBytes(data);

        _frameCount++;
        var args = new CameraFrameData(
            CameraName: _cameraName,
            PixelData: data,
            Width: w,
            Height: h,
            BitDepth: 8,
            PixelFormat: "Mono8",
            SequenceNumber: _frameCount,
            Timestamp: DateTime.Now,
            TriggerSource: source);

        FrameCaptured?.Invoke(this, args);
    }
}

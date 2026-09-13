namespace Core.Acquisition;

/// <summary>
/// 触发来源。既用于配置相机的工作模式，也用于标记某一帧实际是被谁触发的。
/// </summary>
/// <remarks>
/// Hardware = 产品到位信号接在相机的硬触发输入上，由相机自己出图；
/// Software = 上位机经 MQTT 发来的软触发；
/// FreeRun  = 自由运行，用于对焦、调参等调试场景。
/// 一台相机同一时刻只能工作在一种触发模式下（相机自身的限制），用配置切换。
/// </remarks>
public enum TriggerSource { Hardware, Software, FreeRun }

public enum CameraState
{
    Disconnected,
    Connecting,
    /// <summary>已连接但未采集（只配置不触发）。</summary>
    Connected,
    /// <summary>已启动采集，等待硬触发或软触发。</summary>
    Armed,
    Error
}

/// <summary>
/// 相机的身份与硬件信息。由驱动在打开相机后从 SDK 读出并回报，应用层不自行编造。
/// </summary>
public sealed record CameraInfo(
    string CameraName,
    string Vendor,
    string Model,
    string SerialNumber,
    string FirmwareVersion);

/// <summary>
/// 一台相机的采集配置：连哪台相机（SdkType + SerialNumber）、怎么触发、成像参数。
/// 相机自身的身份信息不在这里，见 <see cref="CameraInfo"/>。
/// </summary>
public sealed record CameraConfig(
    string CameraName,
    string SdkType,
    string SerialNumber,
    TriggerSource TriggerSource,
    double ExposureTimeUs,
    double GainDb,
    int Width,
    int Height,
    double? FrameRate);

/// <summary>
/// 图像落盘策略。目录可以是本地路径，也可以是 UNC 共享目录。
/// </summary>
public sealed record ImageStorageOptions(string RootDirectory)
{
    /// <summary>
    /// 文件名：{相机名}_{yyyyMMdd_HHmmss_fff}_{序号}.bmp。
    /// 带毫秒和自增序号，同一毫秒内的连拍也不会重名。
    /// </summary>
    public string BuildImagePath(string cameraName, DateTime timestamp, long sequence)
        => Path.Combine(RootDirectory, $"{cameraName}_{timestamp:yyyyMMdd_HHmmss_fff}_{sequence:D6}.bmp");
}

/// <summary>
/// 驱动层刚出的一帧：只有原始像素，还没有落盘。
/// </summary>
public sealed record CameraFrameData(
    string CameraName,
    byte[] PixelData,
    int Width,
    int Height,
    int BitDepth,
    string PixelFormat,
    long SequenceNumber,
    DateTime Timestamp,
    TriggerSource TriggerSource);

/// <summary>
/// 落盘完成后交给上层的一帧：只带路径与元数据，图像本身去共享目录取。
/// </summary>
public sealed record CameraFrameEventArgs(
    string CameraName,
    string ImagePath,
    /// <summary>该帧对应的在制品条码，用于追溯；采集时没有在制品时为 null。</summary>
    string? ProductId,
    int Width,
    int Height,
    string PixelFormat,
    long SequenceNumber,
    DateTime Timestamp,
    TriggerSource TriggerSource);

public sealed record CameraErrorEventArgs(
    string CameraName,
    string ErrorCode,
    string Message,
    bool IsFatal);

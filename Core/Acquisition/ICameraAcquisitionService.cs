namespace Core.Acquisition;

/// <summary>
/// 单台相机的采集服务：连接相机 → 按触发出图 → 把图像落到共享目录 → 回报结果。
/// 一个实例对应一台相机，按 <see cref="CameraName"/> 注册/解析。
/// </summary>
public interface ICameraAcquisitionService
{
    string CameraName { get; }

    /// <summary>相机硬件信息，由驱动打开相机后回报；未连接时为 null。</summary>
    CameraInfo? Info { get; }

    CameraState State { get; }

    /// <summary>
    /// 当前在制品的产品条码，由采集服务内部自增维护并在每次上报里带上（追溯用）。
    /// 没有在制品（未开始采集）时为 null。
    /// </summary>
    string? CurrentProductId { get; }

    /// <summary>
    /// 开一件新产品：从内部条码发生器取下一个号，设为当前在制品。
    /// <para>
    /// 硬触发即"产品到位"，服务会自动开新号；软件触发的那台相机（Camera02 多帧检测）
    /// 需要在产品进入工位时显式调一次，好让整件产品的多帧共用一个条码。
    /// </para>
    /// </summary>
    /// <returns>新产品条码。</returns>
    string BeginProduct();

    /// <summary>
    /// 直接指定当前在制品条码（将来接扫码枪/上位机下发时用）。传 null 清空，下一帧会重新自动开号。
    /// </summary>
    void SetCurrentProduct(string? productId);

    /// <summary>
    /// 连接相机、按配置设定触发方式并启动采集（进入 <see cref="CameraState.Armed"/>）。
    /// 会先创建图像目录，目录不存在或不可写时直接失败，不会留下一个"连上了但存不了图"的状态。
    /// </summary>
    Task StartAsync(CameraConfig config, ImageStorageOptions storage, CancellationToken ct);

    /// <summary>停止采集并断开相机。</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// 发一次软触发（上位机经 MQTT 触发，或调试时手动触发）。
    /// 当前配置为硬触发时忽略并记一条警告——相机在硬触发模式下不会响应软触发。
    /// </summary>
    Task SoftwareTriggerAsync(CancellationToken ct);

    /// <summary>一帧已经落到共享目录，路径见 <see cref="CameraFrameEventArgs.ImagePath"/>。</summary>
    event EventHandler<CameraFrameEventArgs> FrameCaptured;

    event EventHandler<CameraState> StateChanged;

    /// <summary>相机报错或写盘失败。写盘失败只报错不断流，避免一颗产品丢图就停线。</summary>
    event EventHandler<CameraErrorEventArgs> ErrorOccurred;
}

namespace Core.Acquisition
{
    /// <summary>
    /// 相机 SDK 的薄封装：只管把相机打开、配置、出图，不关心图像存哪里、谁来消费。
    /// 落盘和回报由 <see cref="ICameraAcquisitionService"/> 负责。
    /// </summary>
    public interface ICameraDriver
    {
        public string CameraName { get; }

        /// <summary>打开相机后由 SDK 回报的真实硬件信息；未连接时为 null。</summary>
        public CameraInfo? Info { get; }

        public bool IsConnected { get; }

        Task OpenAsync(CameraConfig config, CancellationToken ct);

        Task CloseAsync( CancellationToken ct);

        Task SetTriggerSourceAsync(TriggerSource source, CancellationToken ct);
         
        Task SoftwareTriggerAsync(CancellationToken ct);

        Task StartCaptureAsync(CancellationToken ct);

        Task StopCaptureAsync(CancellationToken ct);

        /// <summary>相机出图时回调（硬触发由相机 IO 自主出图，软触发由 <see cref="SoftwareTriggerAsync"/> 引发）。</summary>
        event EventHandler<CameraFrameData> FrameCaptured;

        event EventHandler<CameraErrorEventArgs> ErrorOccurred;


    }
}

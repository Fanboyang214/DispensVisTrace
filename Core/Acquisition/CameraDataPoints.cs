namespace Core.Acquisition;

/// <summary>
/// 相机在资源树 / MQTT 上的数据点地址：<b>设备侧发布与宿主侧订阅共用的唯一一份定义</b>。
/// </summary>
/// <remarks>
/// <para>
/// 地址就是 MQTT 主题（不加相机名前缀），宿主用 <c>TagPoint.Address</c> 逐字订阅，
/// 所以这些字符串必须只有一处定义：采集侧桥、视觉侧上报、上位机的资源树都引用它，
/// 谁也不能自己抄一份——抄一份就会在"某一侧改了名字"时静默断线。
/// </para>
/// <para>
/// <b>本机内部点不在这里：</b>触发超时（<c>cam/trigger-timeout</c>）、检测计划帧数
/// （<c>cam/inspect-plan-shots</c>）、当前帧号（<c>cam/inspect-shot-index</c>）、
/// 产品条码（<c>cam/product-id</c>）都<b>不再上报</b>：前三个是过程量，上位机看不到也用不上；
/// 条码改为随图像文件与检测结果落库，不再单独占一个数据点。
/// </para>
/// </remarks>
public static class CameraDataPoints
{
    /// <summary>软件触发写入点（上位机可写），也回读最新触发状态。</summary>
    public const string Trigger = "cam/trigger";

    /// <summary>相机抓拍完成、图像已落到共享目录。</summary>
    public const string FrameReady = "cam/frame-ready";

    /// <summary>累计触发次数（诊断触发丢失）。</summary>
    public const string TriggerCount = "cam/trigger-count";

    /// <summary>最近一次触发的来源（hardware / software / freerun）。</summary>
    public const string TriggerSource = "cam/trigger-source";

    /// <summary>最后一次触发错误，空串 = 无错误。</summary>
    public const string TriggerError = "cam/trigger-error";

    /// <summary>定位相机的置信度（定位侧产出）。</summary>
    public const string LocateConfidence = "cam/locate-conf";

    /// <summary>检测相机的置信度（检测侧产出）。</summary>
    public const string InspectConfidence = "cam/inspect-conf";

    /// <summary>定位相机的图像 URL（共享目录里的路径）。</summary>
    public const string LocateImageUrl = "cam/locate-image-url";

    /// <summary>检测相机的图像 URL（共享目录里的路径）。</summary>
    public const string InspectImageUrl = "cam/inspect-image-url";

    /// <summary>多帧检测全部完成（检测侧产出）。</summary>
    public const string InspectComplete = "cam/inspect-complete";

    /// <summary>按相机角色给出图像 URL 数据点。</summary>
    public static string ImageUrlFor(CameraRole role) => role switch
    {
        CameraRole.Locate => LocateImageUrl,
        CameraRole.Inspect => InspectImageUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未知的相机角色")
    };

    /// <summary>按相机角色给出置信度数据点。</summary>
    public static string ConfidenceFor(CameraRole role) => role switch
    {
        CameraRole.Locate => LocateConfidence,
        CameraRole.Inspect => InspectConfidence,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未知的相机角色")
    };
}

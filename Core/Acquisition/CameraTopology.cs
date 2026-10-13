namespace Core.Acquisition;

/// <summary>
/// 相机在产线上的角色：定位（找产品姿态，喂运动） / 检测（缺陷检测）。
/// </summary>
/// <remarks>
/// 角色决定"这一帧交给哪个视觉引擎"：<see cref="Locate"/> → <c>ILocateEngine</c>，
/// <see cref="Inspect"/> → <c>IInspectEngine</c>。
/// 它<b>不进</b> <see cref="CameraConfig"/>：驱动只关心怎么连、怎么触发，不关心这台相机拍来干什么；
/// 同一台相机今天做定位、明天做检测，不该动驱动配置。
/// </remarks>
public enum CameraRole
{
    /// <summary>定位相机：产品到位后拍一张，产出工件姿态（Camera01）。</summary>
    Locate,

    /// <summary>检测相机：点胶后拍一张，做胶路 / 缺陷检测（Camera02）。</summary>
    Inspect
}

/// <summary>一台相机的接入信息：驱动配置 + 产线角色 + （可选）作为 MQTT 设备上报的接入配置。</summary>
/// <remarks>
/// <see cref="Mqtt"/> 为 null = 这台相机不往资源树/MQTT 上报（纯本机采集与检测）。
/// 有值时组合根会为它建一个 <c>IDeviceDriver</c> 和一个 <c>MqttCameraBridge</c>：
/// 设备侧按数据点地址收发，宿主侧用 <c>IProtocolDriver</c> 按 Tag 读写同一条 MQTT 线。
/// </remarks>
public sealed record CameraSetup(CameraConfig Config, CameraRole Role, MqttDeviceLink? Mqtt = null);

/// <summary>
/// 相机拓扑：图像共享目录 + 本机全部相机的接入信息，以及是否随应用启动自动 Armed。
/// </summary>
/// <remarks>
/// <para>
/// 这是"本机装了几台相机、各拍什么"的<b>唯一来源</b>（cameras.json，见
/// <c>Core.Configuration.CameraTopologyLoader</c>）。组合根按它装配：
/// 每台相机一个 <see cref="CameraSetup"/> → 一个驱动 + 一个采集服务 → 一个角色对应的视觉引擎。
/// </para>
/// <para>
/// <see cref="AutoStart"/>：true = 应用启动就连接并 Armed（等触发），适合"开机即待产"；
/// 产线状态机将来接管启停时置 false，改由 <c>Acquisition.CameraRuntime.StartAsync/StopAsync</c> 控制。
/// </para>
/// </remarks>
public sealed record CameraTopology(
    ImageStorageOptions Storage,
    IReadOnlyList<CameraSetup> Cameras,
    bool AutoStart = true)
{
    /// <summary>有没有配相机。没配 = 相机通道整条不启动（不是"配了但连不上"）。</summary>
    public bool IsEmpty => Cameras.Count == 0;
}

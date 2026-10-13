using Acquisition.Camera;
using Core.Acquisition;
using Core.Bridge;
using Core.Configuration;
using Core.Logging;
using Prism.Ioc;
using Prism.Modularity;

namespace Acquisition;

/// <summary>
/// 采集模块（Prism）：装配相机通道——条码发生器、相机运行时、"图像就绪"发布者，
/// 以及（配了 mqtt 段时）每台相机的设备侧 MQTT 通道。
/// </summary>
/// <remarks>
/// <para>
/// <b>本模块的职责边界止于"图在共享目录里、通知已发出"</b>：它不引用任何视觉引擎，
/// 也不决定谁去检测。订阅方（视觉模块）只依赖 Core 的 <see cref="IImageReadySubscriber"/>，
/// 与采集互不认识；这条总线换成 MQTT / 资源树实现时，两侧都不用改。
/// </para>
/// <para>
/// 装配内容：
/// <list type="number">
/// <item><see cref="ProductCodeGenerator"/> 单例（两台相机共用，条码才不会分裂）；</item>
/// <item><see cref="CameraRuntime"/>：按 <c>config/cameras.json</c> 逐台建驱动 + 采集服务；</item>
/// <item><see cref="ImageReadyPublisher"/>：把每台相机的成帧事件（<see cref="CameraFrameEventArgs"/>）直接发到总线上；</item>
/// <item><see cref="MqttCameraChannel"/>：<b>只有配了 mqtt 段的相机</b>才建，把采集事件映射成 <c>cam/*</c> 数据点上报。</item>
/// </list>
/// </para>
/// <para>
/// <b>MQTT 驱动由组合根注入</b>（<c>Func&lt;CameraSetup, ILogService, IDeviceDriver&gt;</c>，实现在 Plugins）：
/// 本模块只依赖 Core 的 <see cref="IDeviceDriver"/> 契约，<b>不引用任何 MQTT 库</b>。没注册那个工厂
/// 也能跑（相机采集照常），只是资源树数据点不上报——这种情况会记一条明确错误，不静默。
/// </para>
/// <para>
/// <b>配置策略：</b>cameras.json 不存在 = 本机没配相机 → 一个相机服务都不注册，只记一条日志；
/// 文件存在但写错 → 启动阶段直接抛（不许假成功）。
/// </para>
/// <para>
/// <b>总线由宿主注册</b>（进程内实现 / MQTT 实现在 App 的组合根里选）；本模块只依赖发布接口。
/// </para>
/// </remarks>
public class AcquisitionModule : IModule
{
    /// <summary>RegisterTypes 阶段读到的拓扑；OnInitialized 阶段用它决定启不启并写日志。</summary>
    private CameraTopology? _topology;

    /// <inheritdoc />
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        string path = CameraTopologyLoader.DefaultPath(AppContext.BaseDirectory);

        // 内容写错在这里抛（启动阶段失败），"没配"返回 null 走下面的静默分支
        _topology = CameraTopologyLoader.TryLoadFromFile(path);

        if (_topology is null || _topology.IsEmpty)
        {
            return;
        }

        containerRegistry.RegisterInstance(_topology);
        containerRegistry.RegisterInstance<Func<CameraConfig, ILogService, ICameraDriver>>(CameraDriverFactory.Create);

        containerRegistry.RegisterSingleton<ProductCodeGenerator>();
        containerRegistry.RegisterSingleton<CameraRuntime>();
        containerRegistry.RegisterSingleton<ImageReadyPublisher>();

        // 设备侧 MQTT 通道：构造期只建驱动与桥（不碰网络），StartAsync 时才连
        containerRegistry.RegisterSingleton<MqttCameraChannel>();

        // 数据点统一出口：视觉侧只依赖 Core.Bridge.IDataPointPublisher，不认识 MQTT；
        // 具体往哪条连接写由这里按相机名查（见 DeviceDataPointPublisher）
        containerRegistry.RegisterSingleton<IDataPointPublisher, DeviceDataPointPublisher>();
    }

    /// <inheritdoc />
    public void OnInitialized(IContainerProvider containerProvider)
    {
        ILogService log = containerProvider.Resolve<ILogService>().ForContext<AcquisitionModule>();

        if (_topology is null || _topology.IsEmpty)
        {
            log.Info("未配置相机（{Path} 不存在，或 cameras 为空），相机通道不启动",
                CameraTopologyLoader.DefaultPath(AppContext.BaseDirectory));
            return;
        }

        CameraRuntime runtime = containerProvider.Resolve<CameraRuntime>();

        // 先建发布者：它在构造时订阅各相机的成帧事件，必须早于 StartAsync，
        // 否则第一帧（仿真软触发、真机硬触发）发不出去
        try
        {
            _ = containerProvider.Resolve<ImageReadyPublisher>();
        }
        catch (Exception ex)
        {
            // 总线没注册不是致命：采集照常，但通知没人收——必须让人看见，不能静默
            log.Error(ex, "图像就绪总线（IImageReadyPublisher）未注册，采集继续但不会有任何订阅者收到通知");
        }

        MqttCameraChannel? mqttChannel = null;
        if (_topology.Cameras.Any(c => c.Mqtt is not null))
        {
            // 同理：桥要在第一帧之前订阅成帧事件，必须在相机 StartAsync 之前建好
            try
            {
                mqttChannel = containerProvider.Resolve<MqttCameraChannel>();
            }
            catch (Exception ex)
            {
                // 配了 mqtt 段却没人能建驱动 = 配置与装配对不上，必须显式报出来
                log.Error(ex,
                    "cameras.json 配了 mqtt 段，但设备侧 MQTT 驱动工厂（Func<CameraSetup, ILogService, IDeviceDriver>）未注册：" +
                    "资源树数据点不会上报（相机采集不受影响）");
            }
        }

        log.Info("相机通道已装配：{Count} 台，图像目录 {Directory}，自动启动 {AutoStart}",
            runtime.Count, _topology.Storage.RootDirectory, _topology.AutoStart);

        if (!_topology.AutoStart)
        {
            log.Info("autoStart=false：相机已装配但未启动，等产线状态机调 CameraRuntime.StartAsync");
        }

        // 不在这里同步等待：StartAsync 内部有锁与 SDK 调用，在 UI 线程上 GetResult 会死锁。
        // 启动失败由下面的日志与相机状态（Error）呈现，不吞掉。
        _ = StartChannelsAsync(mqttChannel, runtime, _topology.AutoStart, log);
    }

    /// <summary>
    /// 先接 MQTT 通道、再启动相机；各自失败只记日志，不把异常抛进启动流程。
    /// </summary>
    /// <remarks>
    /// 顺序不能反：桥必须在任何一帧产生之前订阅成帧事件，也要在任何触发到来之前订好
    /// <c>cam/trigger</c>——硬触发那台的首帧往往在产品到位瞬间就产生了。
    /// </remarks>
    private static async Task StartChannelsAsync(
        MqttCameraChannel? mqttChannel,
        CameraRuntime runtime,
        bool startCameras,
        ILogService log)
    {
        if (mqttChannel is not null)
        {
            try
            {
                await mqttChannel.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Error(ex, "MQTT 通道启动失败（相机照常采集，资源树数据点不上报）");
            }
        }

        if (!startCameras)
        {
            return;
        }

        try
        {
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error(ex, "相机通道启动失败（服务已装配，可用 CameraRuntime.StartAsync 重试）");
        }
    }
}

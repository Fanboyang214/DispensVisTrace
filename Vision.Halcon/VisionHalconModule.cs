using Core.Acquisition;
using Core.Bridge;
using Core.Configuration;
using Core.Logging;
using Core.Models;
using Core.Vision;
using Prism.Ioc;
using Prism.Modularity;
using System;
using System.Collections.Generic;

namespace Vision.Halcon
{
    /// <summary>
    /// 视觉模块：登记引擎，把"图像就绪"的消费者接上总线，并把生效的置信度阈值先报给上位机。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 相机角色映射来自与采集侧同一份 <c>config/cameras.json</c>：采集按它建相机，视觉按它选引擎。
    /// 两个模块之间没有直接依赖——一个只发通知，一个只订阅。
    /// </para>
    /// <para>
    /// <b>置信度点位的两条来源（先配置、后实测）：</b>链接相机 + 配方加载完成时先发<b>配置阈值</b>
    /// （<c>cam/locate-conf</c> / <c>cam/inspect-conf</c>），让上位机立刻知道当前生效的门限；
    /// 之后每出一帧结果，<see cref="VisionFrameWorker"/> 用<b>实际置信度</b>覆盖同一个点。
    /// 两条来源取的是同一个字段（见 <see cref="CameraConfidenceConfig"/>），不会出现"显示的门限"与"实际在比的数"不一致。
    /// </para>
    /// <para>
    /// 没配相机时不登记消费者，谁 Resolve 都拿不到"会订阅总线"的对象，而不是造一个空转的消费者。
    /// </para>
    /// </remarks>
    public class VisionHalconModule : IModule
    {
        private bool _workerRegistered;
        private IReadOnlyDictionary<string, CameraRole> _roles = new Dictionary<string, CameraRole>();
        private IAlgorithmConfigProvider? _profiles;
        private IDataPointPublisher? _publisher;
        private ILogService? _log;

        public void OnInitialized(IContainerProvider containerProvider)
        {
            _log = containerProvider.Resolve<ILogService>().ForContext<VisionHalconModule>();

            if (!_workerRegistered)
            {
                return;
            }

            try
            {
                // 必须显式取一次：订阅发生在消费者的构造函数里，不 Resolve 就没人订阅总线
                _ = containerProvider.Resolve<VisionFrameWorker>();
            }
            catch (Exception ex)
            {
                // 总线没注册不是致命（视觉这一侧停摆，采集照常），但必须让人看见
                _log.Error(ex, "视觉帧消费者无法创建：图像就绪总线（IImageReadySubscriber）或视觉引擎未注册");
            }

            // 相机已链接、配方已加载：先发一次配置阈值（之后由实际置信度覆盖）
            PublishConfiguredConfidence(containerProvider);
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterSingleton<IVisionAlgorithm, HalconVisionAlgorithm>("Halcon");
            containerRegistry.RegisterSingleton<IInspectEngine, HalconInspectEngine>();

            // 监视画面（View/VisionView.xaml）的数据源：视图在 Loaded 时从容器取这一份，
            // 三块画面共用同一个总线订阅与轴轮询，不随视图实例重复建连接。
            containerRegistry.RegisterSingleton<ViewModels.VisionViewModel>();

            // 定位引擎也要登记：相机通道按角色取引擎（Camera01 = Locate），
            // 只登记检测引擎的话定位相机就只能采图——这条链会静悄悄断在第一跳
            containerRegistry.RegisterSingleton<ILocateEngine, HalconLocateEngine>();

            IReadOnlyDictionary<string, CameraRole> roles = LoadCameraRoles();
            _roles = roles;

            if (roles.Count == 0)
            {
                return;
            }

            containerRegistry.RegisterInstance(roles);
            containerRegistry.RegisterSingleton<VisionFrameWorker>();
            _workerRegistered = true;
        }

        /// <summary>
        /// 把"当前生效的置信度阈值"按相机上报一次（链接相机 / 配方热更新后都走这里）。
        /// </summary>
        /// <remarks>
        /// 上报出口（<see cref="IDataPointPublisher"/>）与配方来源（<see cref="IAlgorithmConfigProvider"/>）
        /// 都是<b>可选</b>的：没配 mqtt 段的机器上就没有出口，这不是错误，只是这些点不上报。
        /// 任何情况下都不发"猜出来的阈值"——宁可少一个点，也不给上位机一个错的数。
        /// </remarks>
        private void PublishConfiguredConfidence(IContainerProvider containerProvider)
        {
            if (_log is null || _roles.Count == 0)
            {
                return;
            }

            _publisher ??= TryResolve<IDataPointPublisher>(containerProvider);
            if (_publisher is null)
            {
                _log.Info("没有数据点出口（相机未配 mqtt 段？），{Point} 一类置信度点不上报", CameraDataPoints.LocateConfidence);
                return;
            }

            if (_profiles is null)
            {
                _profiles = TryResolve<IAlgorithmConfigProvider>(containerProvider);
                if (_profiles is null)
                {
                    _log.Warn("没有算法配置来源（IAlgorithmConfigProvider 未注册），置信度初值不发布");
                    return;
                }

                // 配方热更新后重发：上位机手里的门限必须跟着配置走
                _profiles.ProfileChanged += (_, _) => PublishConfiguredConfidence(containerProvider);
            }

            AlgorithmConfig? profile = CameraConfidenceConfig.Select(_profiles.GetAllConfigs(), out string? note);
            if (note is not null)
            {
                _log.Warn("{Note}", note);
            }

            if (profile is null)
            {
                return;
            }

            foreach (KeyValuePair<string, CameraRole> pair in _roles)
            {
                double? threshold = CameraConfidenceConfig.ThresholdOf(profile, pair.Value);
                string dataPoint = CameraDataPoints.ConfidenceFor(pair.Value);

                if (threshold is null)
                {
                    _log.Warn("{Camera}（{Role}）的配方 {Model} 里没有可用的置信度阈值，{Point} 初值不发布",
                        pair.Key, pair.Value, profile.ProductModel, dataPoint);
                    continue;
                }

                _publisher.Publish(pair.Key, dataPoint, threshold.Value);

                _log.Info("{Camera}（{Role}）生效的置信度阈值 {Value}（配方 {Model}）已上报 {Point}",
                    pair.Key, pair.Value, threshold.Value, profile.ProductModel, dataPoint);
            }
        }

        /// <summary>可选依赖：没注册时返回 null，而不是把整个模块拉不起来。</summary>
        private static T? TryResolve<T>(IContainerProvider containerProvider) where T : class
        {
            try
            {
                return containerProvider.Resolve<T>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>从 cameras.json 读"相机 → 角色"。</summary>
        private static IReadOnlyDictionary<string, CameraRole> LoadCameraRoles()
        {
            var roles = new Dictionary<string, CameraRole>(StringComparer.OrdinalIgnoreCase);

            CameraTopology? topology = CameraTopologyLoader.TryLoadFromFile(
                CameraTopologyLoader.DefaultPath(AppContext.BaseDirectory));

            if (topology is null)
            {
                return roles;
            }

            foreach (CameraSetup setup in topology.Cameras)
            {
                roles[setup.Config.CameraName] = setup.Role;
            }

            return roles;
        }
    }
}

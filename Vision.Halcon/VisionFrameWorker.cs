using Core.Acquisition;
using Core.Bridge;
using Core.Logging;
using Core.Models;
using Core.Vision;
using System;
using System.Collections.Generic;
using System.IO;

namespace Vision.Halcon
{
    /// <summary>
    /// 视觉侧的"图像就绪"消费者：订阅总线 → 按相机角色取图 → 交给定位 / 检测引擎。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一层属于<b>视觉侧</b>，不属于采集侧：采集只管把图落进共享目录并发一条通知，
    /// 是不是要检测、用哪个引擎、多久处理一帧，都是视觉的事。
    /// 依赖方向因此是干净的：Acquisition 只依赖 Core 的发布接口，
    /// Vision 只依赖 Core 的订阅接口与引擎接口，<b>两边互不认识</b>。
    /// </para>
    /// <para>
    /// <b>取图</b>：总线上走的就是采集服务对外的成帧事件 <see cref="CameraFrameEventArgs"/>，
    /// 里面只有路径（<see cref="CameraFrameEventArgs.ImagePath"/>）；本类把它包成
    /// <see cref="InspectionImage.SourcePath"/>，由引擎自己按路径读像素（HALCON <c>HImage(path)</c>）
    /// ——像素不经过总线、不在内存里复制第二份。
    /// </para>
    /// <para>
    /// <b>线程与顺序</b>：事件由总线的泵线程<b>顺序</b>派发，所以这里直接跑引擎是安全的
    /// （HALCON 引擎不保证并发安全）；顺序也保证"产品 N 先于 N+1"。
    /// 单帧处理失败只记日志，绝不抛回总线——抛出去会连累其它订阅者。
    /// </para>
    /// <para>
    /// <b>引擎要先 Initialize</b>（模板 / 标定 / 产品型号）：那属于视觉配方加载，本类不代劳；
    /// 没初始化时引擎会抛，本类会把"可能未 Initialize"写进日志。
    /// </para>
    /// </remarks>
    public sealed class VisionFrameWorker : IDisposable
    {
        private readonly IImageReadySubscriber _bus;
        private readonly IReadOnlyDictionary<string, CameraRole> _roles;
        private readonly ILocateEngine? _locate;
        private readonly IInspectEngine? _inspect;
        private readonly IDataPointPublisher? _publisher;
        private readonly ILogService _log;

        /// <summary>已经告警过的相机名：坏消息只说一次，不刷屏。</summary>
        private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

        private bool _disposed;

        /// <summary>定位成功：这一帧产出了工件姿态（后续交给运动侧）。</summary>
        public event EventHandler<ProductLocatedEventArgs>? ProductLocated;

        /// <summary>检测完成：检测引擎已跑完这一帧（结果由引擎自己落库 / 上报）。</summary>
        public event EventHandler<InspectionCompletedEventArgs>? InspectionCompleted;

        /// <param name="bus">图像就绪总线（订阅侧视图）</param>
        /// <param name="roles">相机名 → 角色；来自 cameras.json</param>
        /// <param name="log">日志服务</param>
        /// <param name="locate">定位引擎；未注册时为 null（该角色的相机只取图不处理）</param>
        /// <param name="inspect">检测引擎；未注册时为 null</param>
        /// <param name="publisher">
        /// 数据点出口（资源树 / MQTT）；未注册时为 null。检测完成时经它上报
        /// <see cref="CameraDataPoints.InspectComplete"/>——视觉侧只依赖 Core 的这个契约，
        /// 不认识 MQTT，也不需要一条自己的连接。
        /// </param>
        public VisionFrameWorker(
            IImageReadySubscriber bus,
            IReadOnlyDictionary<string, CameraRole> roles,
            ILogService log,
            ILocateEngine? locate = null,
            IInspectEngine? inspect = null,
            IDataPointPublisher? publisher = null)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _roles = roles ?? throw new ArgumentNullException(nameof(roles));
            _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<VisionFrameWorker>();
            _locate = locate;
            _inspect = inspect;
            _publisher = publisher;

            foreach (KeyValuePair<string, CameraRole> pair in _roles)
            {
                if (!HasEngine(pair.Value))
                {
                    _log.Warn("{Camera}（{Role}）没有对应的视觉引擎，其图像不会被处理", pair.Key, pair.Value);
                }
            }

            _bus.ImageReady += OnImageReady;
        }

        /// <summary>已接上总线（诊断 / 自检用）。</summary>
        public bool IsSubscribed => !_disposed;

        private bool HasEngine(CameraRole role) => role switch
        {
            CameraRole.Locate => _locate is not null,
            CameraRole.Inspect => _inspect is not null,
            _ => false
        };

        private void OnImageReady(object? sender, CameraFrameEventArgs frame)
        {
            if (_disposed)
            {
                return;
            }

            if (!_roles.TryGetValue(frame.CameraName, out CameraRole role))
            {
                WarnOnce(frame.CameraName, $"{frame.CameraName} 不在相机拓扑里，图像 {frame.ImagePath} 被忽略");
                return;
            }

            if (!HasEngine(role))
            {
                WarnOnce(frame.CameraName, $"{frame.CameraName}（{role}）没有对应的视觉引擎，图像 {frame.ImagePath} 未处理");
                return;
            }

            // 通知说图已落盘，但共享目录才是事实来源：不通/被清掉时要在这里说清楚，
            // 否则表现成引擎报一个看不出原因的"读图失败"
            if (!File.Exists(frame.ImagePath))
            {
                WarnOnce(frame.CameraName, $"{frame.CameraName} 的通知指向的图像不存在：{frame.ImagePath}（共享目录不通、被清理或权限不足？）");
                return;
            }

            var image = new InspectionImage
            {
                SourcePath = frame.ImagePath,
                RawData = null,   // 像素留在共享目录里，由引擎按路径读
                ImageWidth = frame.Width,
                ImageHeight = frame.Height,
                CaptureTime = frame.Timestamp,
                SerialNumber = frame.ProductId
            };

            try
            {
                switch (role)
                {
                    case CameraRole.Locate:
                        Locate(frame, image);
                        break;

                    case CameraRole.Inspect:
                        Inspect(frame, image);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex,
                    "{Camera} 处理第 {Sequence} 帧失败（图像 {Path}）：引擎可能未 Initialize（模板 / 标定 / 产品型号）",
                    frame.CameraName, frame.SequenceNumber, frame.ImagePath);
            }
        }

        /// <summary>定位：先过图像质量门，再求姿态——顺序按 <see cref="ILocateEngine"/> 的约定。</summary>
        private void Locate(CameraFrameEventArgs frame, InspectionImage image)
        {
            ILocateEngine engine = _locate!;

            ImageQualityResult quality = engine.CheckImageQuality(image);
            if (!quality.IsPass)
            {
                _log.Warn("{Camera} 图像质量不合格（评分 {Score:F3}，产品 {Product}，图像 {Path}），本帧不定位",
                    frame.CameraName, quality.Score, image.SerialNumber ?? "(无)", image.SourcePath);
                return;
            }

            ProductPose? pose = engine.FindProductPose(image);
            if (pose is null)
            {
                _log.Warn("{Camera} 未在图像中定位到产品（产品 {Product}，图像 {Path}）",
                    frame.CameraName, image.SerialNumber ?? "(无)", image.SourcePath);
                return;
            }

            _log.Info("{Camera} 定位完成：{Pose}（产品 {Product}）",
                frame.CameraName, pose, image.SerialNumber ?? "(无)");

            // 置信度就是与配置阈值相比的那个值（定位 = 模板匹配置信度）
            PublishConfidence(frame, CameraDataPoints.LocateConfidence, pose.Score);

            ProductLocated?.Invoke(this, new ProductLocatedEventArgs(frame, image, pose));
        }

        /// <summary>检测：引擎自己负责结果落库 / 上报，这里只保证被调到且异常不外溢。</summary>
        private void Inspect(CameraFrameEventArgs frame, InspectionImage image)
        {
            IInspectEngine engine = _inspect!;
            engine.Inspect(image);

            _log.Info("{Camera} 检测完成（产品 {Product}，图像 {Path}）",
                frame.CameraName, image.SerialNumber ?? "(无)", image.SourcePath);

            // 检测引擎产出的分数（就是与配置阈值相比的那个值）；引擎没给出就不覆盖上次的值
            PublishConfidence(frame, CameraDataPoints.InspectConfidence, engine.LastConfidence);

            // 先上报"检测完成"再抛事件：上位机看到这个点时，检测结果已经产生；
            // 上报失败只记警告——线上少一个状态点，不能让检测结果丢掉
            PublishInspectComplete(frame);

            InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(frame, image));
        }

        /// <summary>上报一个置信度数据点（引擎产出的实际值，覆盖链接时发的配置阈值）。</summary>
        private void PublishConfidence(CameraFrameEventArgs frame, string dataPoint, double? value)
        {
            if (_publisher is null || value is null)
            {
                return;
            }

            try
            {
                _publisher.Publish(frame.CameraName, dataPoint, value.Value);
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "{Camera} 上报 {DataPoint} 失败（检测结果不受影响）", frame.CameraName, dataPoint);
            }
        }

        /// <summary>上报"这一件产品检测完成"（多帧检测的最后一次检测完成即此意）。</summary>
        private void PublishInspectComplete(CameraFrameEventArgs frame)
        {
            if (_publisher is null)
            {
                return;
            }

            try
            {
                _publisher.Publish(frame.CameraName, CameraDataPoints.InspectComplete, true);
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "{Camera} 上报 {DataPoint} 失败（检测结果不受影响）",
                    frame.CameraName, CameraDataPoints.InspectComplete);
            }
        }

        private void WarnOnce(string cameraName, string message)
        {
            lock (_warned)
            {
                if (!_warned.Add(cameraName))
                {
                    return;
                }
            }

            _log.Warn("{Message}", message);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _bus.ImageReady -= OnImageReady;
        }
    }

    /// <summary>定位完成事件参数。</summary>
    public sealed record ProductLocatedEventArgs(
        CameraFrameEventArgs Frame, InspectionImage Image, ProductPose Pose);

    /// <summary>检测完成事件参数。</summary>
    public sealed record InspectionCompletedEventArgs(
        CameraFrameEventArgs Frame, InspectionImage Image);
}

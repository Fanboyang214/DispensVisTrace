using Core.Acquisition;
using Core.Bridge;
using Core.Logging;
using Core.Models;
using Core.Motion.Zmotion;
using Core.Vision;
using Prism.Ioc;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace Vision.Halcon.ViewModels
{
    /// <summary>画面上的三块：定位 / 检测 / 点胶。</summary>
    public enum VisionPanel
    {
        Locate,
        Inspect,
        Dispense
    }

    /// <summary>检测判定。引擎还没产出整体判定时是 <see cref="Unknown"/>（显示“待检”）。</summary>
    public enum InspectVerdict
    {
        Unknown,
        Ok,
        Ng
    }

    /// <summary>“某块画面来了一帧新图”的通知；由视图在 UI 线程上换成 HALCON 窗口里的显示。</summary>
    public sealed class VisionFrameReadyEventArgs : EventArgs
    {
        public VisionFrameReadyEventArgs(VisionPanel panel, string imagePath)
        {
            Panel = panel;
            ImagePath = imagePath;
        }

        public VisionPanel Panel { get; }

        public string ImagePath { get; }
    }

    /// <summary>
    /// 三块画面共有的信息：相机名与产品号。
    /// </summary>
    /// <remarks>
    /// 产品号取自这一帧对应的在制品条码（<see cref="CameraFrameEventArgs.ProductId"/>）：
    /// 相机各自的帧可能属于不同工位上的不同产品，所以每块画面自己记，不共用一份。
    /// </remarks>
    /// <remarks>
    /// 属性通知交给 Prism 的 <see cref="BindableBase"/>（<c>SetProperty</c> / <c>RaisePropertyChanged</c>），
    /// 不手写 <see cref="System.ComponentModel.INotifyPropertyChanged"/> 与 Set / Raise 帮助方法。
    /// </remarks>
    public abstract class VisionPanelViewModel : BindableBase
    {
        private string? _productId;
        private string _statusText = "等待图像";

        protected VisionPanelViewModel(string cameraName)
        {
            CameraName = cameraName;
        }

        /// <summary>这块画面对应的相机（来自 cameras.json）；没配相机时为“未配置相机”。</summary>
        public string CameraName { get; }

        /// <summary>本帧对应的在制品条码；没有在制品时为 null（界面显示“—”）。</summary>
        public string? ProductId
        {
            get => _productId;
            internal set => SetProperty(ref _productId, value);
        }

        /// <summary>画面状态（等待图像 / 已出图 / 已定位 / 检测中 / 检测完成 …）。</summary>
        public string StatusText
        {
            get => _statusText;
            internal set => SetProperty(ref _statusText, value);
        }
    }

    /// <summary>定位画面：产品号 + 位姿（行 / 列 / 偏转角 / 匹配分）。</summary>
    public sealed class LocatePanelViewModel : VisionPanelViewModel
    {
        private double? _row;
        private double? _column;
        private double? _angleDeg;
        private double? _score;

        public LocatePanelViewModel(string cameraName) : base(cameraName)
        {
        }

        public double? Row
        {
            get => _row;
            internal set
            {
                if (SetProperty(ref _row, value))
                {
                    RaisePropertyChanged(nameof(HasPose));
                }
            }
        }

        public double? Column
        {
            get => _column;
            internal set
            {
                if (SetProperty(ref _column, value))
                {
                    RaisePropertyChanged(nameof(HasPose));
                }
            }
        }

        /// <summary>偏转角（度）。</summary>
        public double? AngleDeg
        {
            get => _angleDeg;
            internal set => SetProperty(ref _angleDeg, value);
        }

        /// <summary>模板匹配得分（与配方 minScore 相比的那个值）。</summary>
        public double? Score
        {
            get => _score;
            internal set => SetProperty(ref _score, value);
        }

        /// <summary>是否已经有可用位姿（决定状态标签是否转绿）。</summary>
        public bool HasPose => _row.HasValue && _column.HasValue;

        /// <summary>换了一颗新产品：把上一颗的位姿清掉，免得界面把旧位姿当成新产品的。</summary>
        internal void ClearPose()
        {
            Row = null;
            Column = null;
            AngleDeg = null;
            Score = null;
        }
    }

    /// <summary>检测画面：产品号 + OK/NG + 置信度。</summary>
    public sealed class InspectPanelViewModel : VisionPanelViewModel
    {
        private InspectVerdict _verdict;
        private double? _confidence;

        public InspectPanelViewModel(string cameraName) : base(cameraName)
        {
        }

        /// <summary>
        /// 判定结果。检测引擎产出整体判定（配方 <c>defectClassification.overallJudgment</c>）后写这里；
        /// 在此之前保持 <see cref="InspectVerdict.Unknown"/>。
        /// </summary>
        public InspectVerdict Verdict
        {
            get => _verdict;
            set
            {
                if (SetProperty(ref _verdict, value))
                {
                    RaisePropertyChanged(nameof(VerdictText));
                }
            }
        }

        public string VerdictText => _verdict switch
        {
            InspectVerdict.Ok => "OK",
            InspectVerdict.Ng => "NG",
            _ => "待检"
        };

        /// <summary>检测引擎最近一次产出的分数（<see cref="IInspectEngine.LastConfidence"/>）。</summary>
        public double? Confidence
        {
            get => _confidence;
            internal set => SetProperty(ref _confidence, value);
        }
    }

    /// <summary>点胶画面：产品号 + 实时轴位置（X / Y / Z）。</summary>
    /// <remarks>
    /// 点胶工位本身没有独立相机，画面沿用全局定位相机的视野（产品就在它下面点胶）；
    /// 将来装了专门的相机，只要把这一块换成对应的相机即可，界面不用改。
    /// </remarks>
    public sealed class DispensePanelViewModel : VisionPanelViewModel
    {
        private double? _x;
        private double? _y;
        private double? _z;
        private bool _isConnected;
        private bool _isMoving;

        public DispensePanelViewModel(string cameraName, bool hasAxisSource) : base(cameraName)
        {
            HasAxisSource = hasAxisSource;
        }

        /// <summary>运动卡是否存在（没注册 <see cref="IZmotionCard"/> 时为 false）。</summary>
        public bool HasAxisSource { get; }

        public double? X
        {
            get => _x;
            internal set => SetProperty(ref _x, value);
        }

        public double? Y
        {
            get => _y;
            internal set => SetProperty(ref _y, value);
        }

        public double? Z
        {
            get => _z;
            internal set => SetProperty(ref _z, value);
        }

        public bool IsConnected
        {
            get => _isConnected;
            internal set
            {
                if (SetProperty(ref _isConnected, value))
                {
                    RaisePropertyChanged(nameof(ConnectionText));
                }
            }
        }

        public bool IsMoving
        {
            get => _isMoving;
            internal set
            {
                if (SetProperty(ref _isMoving, value))
                {
                    RaisePropertyChanged(nameof(ConnectionText));
                }
            }
        }

        /// <summary>轴状态一句话：未配置运动卡 / 未连接 / 运动中 / 静止。</summary>
        public string ConnectionText
        {
            get
            {
                if (!HasAxisSource)
                {
                    return "未配置运动卡";
                }

                if (!_isConnected)
                {
                    return "未连接";
                }

                return _isMoving ? "运动中" : "已连接 · 静止";
            }
        }
    }

    /// <summary>
    /// 视觉监视画面的数据源：订阅“图像就绪”总线与视觉帧消费者，把三块画面各自的信息算成可绑定属性。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>取图</b>：总线上只有路径（像素不进总线），本类只把路径转给视图；由视图用 HALCON 读文件显示，
    /// 与引擎读的是同一张归档图。
    /// </para>
    /// <para>
    /// <b>位姿</b>：来自 <see cref="VisionFrameWorker.ProductLocated"/>——定位引擎算出位姿之后才有的、
    /// 与配方门限比过的真实结果，不在界面上自己算。
    /// </para>
    /// <para>
    /// <b>OK/NG</b>：<see cref="IInspectEngine"/> 现在只产出图像质量分，<b>没有整体判定</b>，
    /// 所以这里不拿分数去猜 NG（错的 NG 比没有 NG 更坏）：判定由引擎产出后写入
    /// <see cref="InspectPanelViewModel.Verdict"/>。
    /// </para>
    /// <para>
    /// <b>轴位置</b>：按 100 ms 轮询 <see cref="IZmotionCard"/> 的 DPOS（轴 0/1/2 = X/Y/Z）；
    /// 没注册运动卡（组合根没接）时该块显示“未配置运动卡”，不会假装有值。
    /// </para>
    /// <para>
    /// <b>线程</b>：总线与轮询都在后台线程上回调，属性更新统一投递到 UI 线程——
    /// 绑定目标的属性变更必须发生在 UI 线程，否则现场可能偶发“界面卡在旧值”。
    /// </para>
    /// </remarks>
    public sealed class VisionViewModel : IDisposable
    {
        /// <summary>轴位置轮询周期：10 Hz，点胶时看得出手在动，又不会把卡读爆。</summary>
        private static readonly TimeSpan AxisPollInterval = TimeSpan.FromMilliseconds(100);

        private static readonly IReadOnlyDictionary<string, CameraRole> EmptyRoles =
            new Dictionary<string, CameraRole>();

        private readonly IReadOnlyDictionary<string, CameraRole> _roles;
        private readonly ILogService? _log;
        private readonly Dispatcher? _dispatcher;
        private readonly IImageReadySubscriber? _bus;
        private readonly VisionFrameWorker? _worker;
        private readonly IInspectEngine? _inspectEngine;
        private readonly IZmotionCard? _card;
        private readonly System.Threading.Timer? _axisTimer;

        private bool _axisErrorReported;
        private bool _disposed;

        public VisionViewModel(IContainerProvider containerProvider)
        {
            ArgumentNullException.ThrowIfNull(containerProvider);

            _dispatcher = Application.Current?.Dispatcher;
            _log = TryResolve<ILogService>(containerProvider)?.ForContext<VisionViewModel>();
            _roles = TryResolve<IReadOnlyDictionary<string, CameraRole>>(containerProvider) ?? EmptyRoles;

            _card = TryResolve<IZmotionCard>(containerProvider);
            _inspectEngine = TryResolve<IInspectEngine>(containerProvider);

            string locateCamera = FindCamera(CameraRole.Locate);
            string inspectCamera = FindCamera(CameraRole.Inspect);

            Locate = new LocatePanelViewModel(locateCamera);                      
            Inspect = new InspectPanelViewModel(inspectCamera);
            Dispense = new DispensePanelViewModel(locateCamera, hasAxisSource: _card is not null);

            _bus = TryResolve<IImageReadySubscriber>(containerProvider);
            if (_bus is not null)
            {
                _bus.ImageReady += OnImageReady;
            }
            else
            {
                _log?.Info("没有图像就绪总线（IImageReadySubscriber 未注册），三块画面不会刷新");
            }

            _worker = TryResolve<VisionFrameWorker>(containerProvider);
            if (_worker is not null)
            {
                _worker.ProductLocated += OnProductLocated;
                _worker.InspectionCompleted += OnInspectionCompleted;
            }

            if (_card is not null)
            {
                _axisTimer = new System.Threading.Timer(
                    PollAxisPositions, state: null, TimeSpan.Zero, AxisPollInterval);
            }
            else
            {
                _log?.Info("没有运动卡（IZmotionCard 未注册），点胶画面的实时轴位置不可用");
            }
        }

        public LocatePanelViewModel Locate { get; }

        public InspectPanelViewModel Inspect { get; }

        public DispensePanelViewModel Dispense { get; }

        /// <summary>某块画面有一帧新图，视图据此更新 HALCON 窗口。</summary>
        public event EventHandler<VisionFrameReadyEventArgs>? FrameReady;

        /// <summary>视图读图 / 显示失败时回报一句，状态标签会显示“图像加载失败”。</summary>
        public void ReportFrameDisplayError(VisionPanel panel, string message)
        {
            OnUi(() => PanelOf(panel).StatusText = "图像加载失败");
            _log?.Warn("画面 {Panel} 加载图像失败：{Message}", panel, message);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_bus is not null)
            {
                _bus.ImageReady -= OnImageReady;
            }

            if (_worker is not null)
            {
                _worker.ProductLocated -= OnProductLocated;
                _worker.InspectionCompleted -= OnInspectionCompleted;
            }

            _axisTimer?.Dispose();
        }

        /// <summary>相机名 → 角色（cameras.json 的唯一来源，模块注册的那份）。</summary>
        private string FindCamera(CameraRole role)
        {
            foreach (KeyValuePair<string, CameraRole> pair in _roles)
            {
                if (pair.Value == role)
                {
                    return pair.Key;
                }
            }

            return "未配置相机";
        }

        private void OnImageReady(object? sender, CameraFrameEventArgs frame)
        {
            if (_disposed || _roles.Count == 0)
            {
                return;
            }

            if (!_roles.TryGetValue(frame.CameraName, out CameraRole role))
            {
                return;
            }

            switch (role)
            {
                case CameraRole.Locate:
                    OnUi(() =>
                    {
                        // 换了产品就先清掉上一颗的位姿：旧位姿留在界面上比“暂无”更危险
                        bool newProduct = !string.Equals(Locate.ProductId, frame.ProductId, StringComparison.Ordinal);

                        Locate.ProductId = frame.ProductId;

                        if (newProduct)
                        {
                            Locate.ClearPose();
                            Locate.StatusText = "定位中";
                        }
                        else
                        {
                            Locate.StatusText = Locate.HasPose ? "已定位" : "已出图";
                        }

                        // 点胶视图沿用全局相机：产品就在这只镜头下面点胶
                        Dispense.ProductId = frame.ProductId;
                    });

                    RaiseFrameReady(VisionPanel.Locate, frame.ImagePath);
                    RaiseFrameReady(VisionPanel.Dispense, frame.ImagePath);
                    break;

                case CameraRole.Inspect:
                    OnUi(() =>
                    {
                        Inspect.ProductId = frame.ProductId;
                        Inspect.StatusText = "检测中";
                    });

                    RaiseFrameReady(VisionPanel.Inspect, frame.ImagePath);
                    break;
            }
        }

        private void OnProductLocated(object? sender, ProductLocatedEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            ProductPose pose = args.Pose;
            string? productId = args.Image.SerialNumber;

            OnUi(() =>
            {
                if (!string.IsNullOrWhiteSpace(productId))
                {
                    Locate.ProductId = productId;
                }

                Locate.Row = pose.CenterRow;
                Locate.Column = pose.CenterCol;
                Locate.AngleDeg = pose.AngleDeg;
                Locate.Score = pose.Score;
                Locate.StatusText = "已定位";
            });
        }

        private void OnInspectionCompleted(object? sender, InspectionCompletedEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            string? productId = args.Image.SerialNumber;
            double? confidence = _inspectEngine?.LastConfidence;

            OnUi(() =>
            {
                if (!string.IsNullOrWhiteSpace(productId))
                {
                    Inspect.ProductId = productId;
                }

                Inspect.Confidence = confidence;
                Inspect.StatusText = "检测完成";

                // 注意：这里不写 Verdict。检测引擎目前只产出图像质量分，
                // 整体 OK/NG 还没实现（配方里的 overallJudgment 尚未落地）；
                // 用质量分冒充产品判定会让界面报出错误的 NG。
            });
        }

        /// <summary>后台线程上读轴位置：连不上 / 读失败时显示“未连接”，不保留上一次的旧值。</summary>
        private void PollAxisPositions(object? state)
        {
            if (_disposed)
            {
                return;
            }

            IZmotionCard card = _card!;

            bool connected;
            try
            {
                connected = card.IsConnected;
            }
            catch (Exception)
            {
                connected = false;
            }

            if (!connected)
            {
                OnUi(() =>
                {
                    Dispense.IsConnected = false;
                    Dispense.IsMoving = false;
                    Dispense.X = null;
                    Dispense.Y = null;
                    Dispense.Z = null;
                });
                return;
            }

            try
            {
                // 轴映射与 production-line.json 的 motion01 一致：axis0/1/2 = X/Y/Z
                float x = card.GetDpos(0);
                float y = card.GetDpos(1);
                float z = card.GetDpos(2);
                bool idle = card.GetIsIdle(0) && card.GetIsIdle(1) && card.GetIsIdle(2);

                _axisErrorReported = false;

                OnUi(() =>
                {
                    Dispense.IsConnected = true;
                    Dispense.X = x;
                    Dispense.Y = y;
                    Dispense.Z = z;
                    Dispense.IsMoving = !idle;
                });
            }
            catch (Exception ex)
            {
                // 10 Hz 轮询失败不能刷日志：同一个原因只说一次，恢复后重新允许报
                if (!_axisErrorReported)
                {
                    _axisErrorReported = true;
                    _log?.Warn(ex, "读取轴位置失败（点胶画面暂显未连接）");
                }

                OnUi(() => Dispense.IsConnected = false);
            }
        }

        private void RaiseFrameReady(VisionPanel panel, string imagePath)
        {
            EventHandler<VisionFrameReadyEventArgs>? handlers = FrameReady;
            if (handlers is null)
            {
                return;
            }

            try
            {
                handlers(this, new VisionFrameReadyEventArgs(panel, imagePath));
            }
            catch (Exception ex)
            {
                // 订阅者是视图：它自己抛异常不能把总线泵线程带走
                _log?.Warn(ex, "画面 {Panel} 的通知处理失败", panel);
            }
        }

        private void OnUi(Action action)
        {
            if (_disposed)
            {
                return;
            }

            Dispatcher? dispatcher = _dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())//如果当前线程为UI线程
            {
                action();
                return;
            }

            try
            {
                dispatcher.BeginInvoke(action);//推送到UI线程
            }
            catch (Exception)
            {
                // 应用已退出：丢一次界面刷新，不影响业务
            }
        }

        private VisionPanelViewModel PanelOf(VisionPanel panel) => panel switch
        {
            VisionPanel.Locate => Locate,
            VisionPanel.Inspect => Inspect,
            VisionPanel.Dispense => Dispense,
            _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, "未知画面")
        };

        /// <summary>可选依赖：没注册时返回 null，而不是把整个视图拉不起来。</summary>
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
    }
}

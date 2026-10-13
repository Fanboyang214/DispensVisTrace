using Core.Logging;
using Core.Motion.Zmotion;
using Prism.Commands;
using Prism.Ioc;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace Motion.ViewModel
{
    /// <summary>
    /// 运动页（<c>View/Motion.xaml</c>）：X/Y/Z 三个单轴页面 + 控制器前瞻设置。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>轮询只有一份：</b>三个轴共用本类的 10 Hz 定时器（一次 <see cref="IZmotionCard.ReadAxisStates"/>
    /// 批量读），而不是每个单轴各开一个定时器——一张卡上的往返次数直接决定超时窗口。
    /// </para>
    /// <para>
    /// <b>连接是可选的：</b>组合根没注册 <see cref="IZmotionCard"/> 时页面照样显示，
    /// 只是状态为“未连接”、所有下发命令被禁用；不要为了显示界面去假装连上了卡。
    /// </para>
    /// <para>
    /// <b>前瞻参数：</b>只下发控制器确实有参数地址的项（拐角模式 / 限制半径 / 最小速度 /
    /// 减速角度 / 停止角度 / 限速半径）；倒角半径、误差系数、SplineDis、CornerAcc、限速值
    /// 在控制器侧还没有确认的参数地址，界面保留但不下发，避免“假保存”。
    /// </para>
    /// </remarks>
    public sealed class MotionViewModel : BindableBase, IDisposable
    {
        private const int AxisCount = 3;

        /// <summary>状态轮询周期：10 Hz，操作员看得出动，又不会把通信打满。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        private readonly IZmotionCard? _card;
        private readonly ILogService? _log;
        private readonly Dispatcher? _dispatcher;
        private readonly System.Threading.Timer? _pollTimer;

        private bool _disposed;
        private bool _connected;
        private bool _wasConnected;
        private bool _autoLoaded;
        private bool _pollErrorReported;

        private bool _lastLookaheadFailed;
        private bool _lastZTrackFailed;
        private string _lastLookaheadMessage = "就绪";
        private string _lastZTrackMessage = "就绪";

        private int? _cornerMode;
        private double? _fullSpeedRadius;
        private double? _minSpeed;
        private double? _chamferRadius;
        private double? _decelAngle;
        private double? _stopAngle;

        private double? _errorFactor;
        private double? _splineDis;
        private double? _cornerAcc;
        private double? _speedLimitRadius;
        private double? _speedLimitValue;

        public MotionViewModel(IContainerProvider containerProvider)
        {
            ArgumentNullException.ThrowIfNull(containerProvider);

            _dispatcher = Application.Current?.Dispatcher;
            ILogService? log = TryResolve<ILogService>(containerProvider);
            _log = log?.ForContext<MotionViewModel>();
            _card = TryResolve<IZmotionCard>(containerProvider);

            ILogService? axisLog = log?.ForContext<AxisMotionViewModel>();

            XAxis = new AxisMotionViewModel(0, "X", _card, axisLog);
            YAxis = new AxisMotionViewModel(1, "Y", _card, axisLog);
            ZAxis = new AxisMotionViewModel(2, "Z", _card, axisLog);
            Axes = new[] { XAxis, YAxis, ZAxis };

            SaveLookaheadCommand = new DelegateCommand(SaveLookahead, () => IsConnected);
            SaveZTrackSmoothCommand = new DelegateCommand(SaveZTrackSmooth, () => IsConnected);

            if (_card is not null)
            {
                _pollTimer = new System.Threading.Timer(Poll, state: null, TimeSpan.Zero, PollInterval);
            }
            else
            {
                _log?.Info("没有运动卡（IZmotionCard 未注册），运动页只显示界面、不刷新状态");
            }
        }

        public AxisMotionViewModel XAxis { get; }

        public AxisMotionViewModel YAxis { get; }

        public AxisMotionViewModel ZAxis { get; }

        public IReadOnlyList<AxisMotionViewModel> Axes { get; }

        // ───────────── 连接状态 ─────────────

        public bool IsConnected
        {
            get => _connected;
            private set
            {
                if (SetProperty(ref _connected, value))
                {
                    RaisePropertyChanged(nameof(CardStatusText));
                    RaisePropertyChanged(nameof(CardStatusHint));
                    SaveLookaheadCommand.RaiseCanExecuteChanged();
                    SaveZTrackSmoothCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string CardStatusText => IsConnected ? "控制器已连接" : "控制器未连接";

        public string CardStatusHint => _card is null
            ? "组合根未注册 IZmotionCard"
            : IsConnected
                ? "10 Hz 状态刷新"
                : "等待连接";

        // ───────────── 控制器前瞻 ─────────────

        /// <summary>拐角模式（CORNER_MODE）。</summary>
        public int? CornerMode
        {
            get => _cornerMode;
            set => SetProperty(ref _cornerMode, value);
        }

        /// <summary>限制（全速）半径（FULL_SP_RADIUS，mm）。</summary>
        public double? FullSpeedRadius
        {
            get => _fullSpeedRadius;
            set => SetProperty(ref _fullSpeedRadius, value);
        }

        /// <summary>最小速度 / 起始速度（LSPEED，mm/s）。</summary>
        public double? MinSpeed
        {
            get => _minSpeed;
            set => SetProperty(ref _minSpeed, value);
        }

        /// <summary>倒角半径（mm）。<b>待联调：</b>控制器侧暂无确认的参数地址。</summary>
        public double? ChamferRadius
        {
            get => _chamferRadius;
            set => SetProperty(ref _chamferRadius, value);
        }

        /// <summary>减速角度（DECEL_ANGLE，度）。</summary>
        public double? DecelAngle
        {
            get => _decelAngle;
            set => SetProperty(ref _decelAngle, value);
        }

        /// <summary>停止角度（STOP_ANGLE，度）。</summary>
        public double? StopAngle
        {
            get => _stopAngle;
            set => SetProperty(ref _stopAngle, value);
        }

        // ───────────── ZTrackSmooth 参数 ─────────────

        /// <summary>误差系数。<b>待联调。</b></summary>
        public double? ErrorFactor
        {
            get => _errorFactor;
            set => SetProperty(ref _errorFactor, value);
        }

        /// <summary>SplineDis（mm）。<b>待联调。</b></summary>
        public double? SplineDis
        {
            get => _splineDis;
            set => SetProperty(ref _splineDis, value);
        }

        /// <summary>CornerAcc（mm/s²）。<b>待联调。</b></summary>
        public double? CornerAcc
        {
            get => _cornerAcc;
            set => SetProperty(ref _cornerAcc, value);
        }

        /// <summary>限速半径（SPLIMIT_RADIUS，mm）。</summary>
        public double? SpeedLimitRadius
        {
            get => _speedLimitRadius;
            set => SetProperty(ref _speedLimitRadius, value);
        }

        /// <summary>限速值。<b>待联调。</b></summary>
        public double? SpeedLimitValue
        {
            get => _speedLimitValue;
            set => SetProperty(ref _speedLimitValue, value);
        }

        // ───────────── 命令与提示 ─────────────

        public DelegateCommand SaveLookaheadCommand { get; }

        public DelegateCommand SaveZTrackSmoothCommand { get; }

        public string LastLookaheadMessage
        {
            get => _lastLookaheadMessage;
            private set => SetProperty(ref _lastLookaheadMessage, value);
        }

        public bool LastLookaheadFailed
        {
            get => _lastLookaheadFailed;
            private set => SetProperty(ref _lastLookaheadFailed, value);
        }

        public string LastZTrackMessage
        {
            get => _lastZTrackMessage;
            private set => SetProperty(ref _lastZTrackMessage, value);
        }

        public bool LastZTrackFailed
        {
            get => _lastZTrackFailed;
            private set => SetProperty(ref _lastZTrackFailed, value);
        }

        /// <summary>定时器回调：读一帧全部轴状态，投递回 UI 线程。</summary>
        private void Poll(object? state)
        {
            if (_disposed || _card is null)
            {
                return;
            }

            bool connected;
            try
            {
                connected = _card.IsConnected;
            }
            catch (Exception ex)
            {
                ReportPollFailure(ex);
                connected = false;
            }

            if (!connected)
            {
                OnUi(ApplyDisconnected);
                return;
            }

            IReadOnlyList<AxisMotionSnapshot> snapshots;
            try
            {
                snapshots = _card.ReadAxisStates(AxisCount);
            }
            catch (Exception ex)
            {
                ReportPollFailure(ex);
                OnUi(ApplyDisconnected);
                return;
            }

            OnUi(() => ApplySnapshots(snapshots));
        }

        private void ApplySnapshots(IReadOnlyList<AxisMotionSnapshot> snapshots)
        {
            IsConnected = true;

            foreach (AxisMotionViewModel axis in Axes)
            {
                axis.SetConnected(true);
            }

            for (int index = 0; index < snapshots.Count && index < Axes.Count; index++)
            {
                int axisNumber = snapshots[index].Axis;
                if (axisNumber >= 0 && axisNumber < Axes.Count)
                {
                    Axes[axisNumber].ApplySnapshot(snapshots[index]);
                }
            }

            bool justConnected = !_wasConnected;
            _wasConnected = true;

            // 首次连上时读一次参数：界面显示的是控制器里真实的配置，而不是随手填的默认值
            if (justConnected && !_autoLoaded)
            {
                _autoLoaded = true;
                LoadAllParameters();
            }
        }

        private void ApplyDisconnected()
        {
            IsConnected = false;

            foreach (AxisMotionViewModel axis in Axes)
            {
                axis.SetConnected(false);
            }

            _wasConnected = false;
        }

        private void LoadAllParameters()
        {
            if (_card is null)
            {
                return;
            }

            foreach (AxisMotionViewModel axis in Axes)
            {
                axis.ReadParameters();
                axis.ReadHomeParameters();
            }

            LoadLookaheadParameters();

            _log?.Info("已从控制器读取三轴参数与前瞻参数");
        }

        private void LoadLookaheadParameters()
        {
            if (_card is null || !IsConnected)
            {
                return;
            }

            int? cornerMode = TryReadInt(0, AxisParameter.CornerMode, "拐角模式");
            double? fullSpeedRadius = TryRead(0, AxisParameter.FullSpeedRadius, "限制半径");
            double? minSpeed = TryRead(0, AxisParameter.StartSpeed, "最小速度");
            double? decelAngle = TryRead(0, AxisParameter.DecelAngle, "减速角度");
            double? stopAngle = TryRead(0, AxisParameter.StopAngle, "停止角度");
            double? speedLimitRadius = TryRead(0, AxisParameter.SpeedLimitRadius, "限速半径");

            if (cornerMode is int mode)
            {
                CornerMode = mode;
            }

            FullSpeedRadius = fullSpeedRadius ?? FullSpeedRadius;
            MinSpeed = minSpeed ?? MinSpeed;
            DecelAngle = decelAngle ?? DecelAngle;
            StopAngle = stopAngle ?? StopAngle;
            SpeedLimitRadius = speedLimitRadius ?? SpeedLimitRadius;
        }

        /// <summary>下发控制器前瞻参数（只写有参数地址的项）。</summary>
        private void SaveLookahead()
        {
            if (!TryGetCard(out IZmotionCard card, out string? reason))
            {
                SetLookaheadStatus(reason!, failed: true);
                return;
            }

            if (CornerMode is not int cornerMode || cornerMode < 0)
            {
                SetLookaheadStatus("拐角模式必须是非负整数", failed: true);
                return;
            }

            if (FullSpeedRadius is not >= 0 || MinSpeed is not >= 0 || DecelAngle is not >= 0 || StopAngle is not >= 0)
            {
                SetLookaheadStatus("限制半径 / 最小速度 / 减速角度 / 停止角度不能为负", failed: true);
                return;
            }

            try
            {
                foreach (AxisMotionViewModel axis in Axes)
                {
                    card.WriteAxisParameter(axis.AxisNumber, AxisParameter.CornerMode, cornerMode);
                    card.WriteAxisParameter(axis.AxisNumber, AxisParameter.FullSpeedRadius, (float)FullSpeedRadius!.Value);
                    card.WriteAxisParameter(axis.AxisNumber, AxisParameter.StartSpeed, (float)MinSpeed!.Value);
                    card.WriteAxisParameter(axis.AxisNumber, AxisParameter.DecelAngle, (float)DecelAngle!.Value);
                    card.WriteAxisParameter(axis.AxisNumber, AxisParameter.StopAngle, (float)StopAngle!.Value);
                }

                SetLookaheadStatus("前瞻参数已写入 X/Y/Z 三轴；倒角半径待联调，未下发", failed: false);
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "写前瞻参数失败");
                SetLookaheadStatus($"写前瞻参数失败：{ex.Message}", failed: true);
            }
        }

        /// <summary>下发 ZTrackSmooth 中控制器有参数地址的项（限速半径）。</summary>
        private void SaveZTrackSmooth()
        {
            if (!TryGetCard(out IZmotionCard card, out string? reason))
            {
                SetZTrackStatus(reason!, failed: true);
                return;
            }

            if (SpeedLimitRadius is not >= 0)
            {
                SetZTrackStatus("限速半径不能为负", failed: true);
                return;
            }

            try
            {
                foreach (AxisMotionViewModel axis in Axes)
                {
                    card.WriteAxisParameter(axis.AxisNumber, AxisParameter.SpeedLimitRadius, (float)SpeedLimitRadius!.Value);
                }

                SetZTrackStatus("限速半径已写入 X/Y/Z 三轴；误差系数 / SplineDis / CornerAcc / 限速值待联调，未下发", failed: false);
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "写 ZTrackSmooth 参数失败");
                SetZTrackStatus($"写 ZTrackSmooth 参数失败：{ex.Message}", failed: true);
            }
        }

        private bool TryGetCard(out IZmotionCard card, out string? reason)
        {
            card = _card!;

            if (_card is null)
            {
                reason = "没有运动卡（组合根未注册 IZmotionCard）";
                return false;
            }

            if (!IsConnected)
            {
                reason = "控制器未连接";
                return false;
            }

            reason = null;
            return true;
        }

        private double? TryRead(int axis, AxisParameter parameter, string label)
        {
            try
            {
                return Math.Round(_card!.ReadAxisParameter(axis, parameter), 4);
            }
            catch (Exception ex)
            {
                _log?.Debug(ex, "读取 {Label} 失败", label);
                return null;
            }
        }

        private int? TryReadInt(int axis, AxisParameter parameter, string label)
        {
            double? value = TryRead(axis, parameter, label);
            return value is double number ? (int)Math.Round(number) : null;
        }

        private void SetLookaheadStatus(string message, bool failed)
        {
            LastLookaheadMessage = message;
            LastLookaheadFailed = failed;
        }

        private void SetZTrackStatus(string message, bool failed)
        {
            LastZTrackMessage = message;
            LastZTrackFailed = failed;
        }

        private void ReportPollFailure(Exception exception)
        {
            if (_pollErrorReported)
            {
                return;
            }

            _pollErrorReported = true;
            _log?.Warn(exception, "读取轴状态失败（界面暂时显示未连接）");
        }

        private void OnUi(Action action)
        {
            if (_disposed)
            {
                return;
            }

            Dispatcher? dispatcher = _dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                dispatcher.BeginInvoke(action);
            }
            catch (Exception)
            {
                // 应用已退出：丢一次界面刷新，不影响业务
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pollTimer?.Dispose();
        }

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

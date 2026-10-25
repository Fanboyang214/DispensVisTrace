using Core.Logging;
using Core.Motion.Zmotion;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Motion.ViewModel
{
    /// <summary>
    /// 单轴页面（<c>View/AxisMotion.xaml</c>）：机械/运动参数、回零参数、单轴调试与实时状态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>参数为什么是“可空”的：</b>没读到或没填写的参数保持 <c>null</c>，保存时逐项校验并拒绝下发。
    /// 这样“界面上是空的”不会被当成 0 写进控制器——速度/加速度写 0 是能真停机的。
    /// </para>
    /// <para>
    /// <b>保存 = 立即写控制器参数</b>（<see cref="AxisParameter"/>）；不是本地配置文件。
    /// 机械三项（脉冲数/减速比/导程）是上位机侧的换算输入，保存时折成脉冲当量（UNITS）一起下发。
    /// </para>
    /// <para>
    /// <b>线程：</b>命令在 UI 线程执行；实时状态由 <see cref="MotionViewModel"/> 的轮询线程回调，
    /// 由父级投递回 UI 线程后写入。
    /// </para>
    /// </remarks>
    public sealed class AxisMotionViewModel : BindableBase
    {
        private const int HomeModeMin = 0;
        private const int HomeModeMax = 7;

        private readonly IZmotionCard? _card;
        private readonly ILogService? _log;

        private bool _connected;
        private bool _moving;
        private bool _homed;
        private bool _alarm;
        private bool _lastOperationFailed;

        private string _lastMessage = "就绪";

        private double? _pulsePerRev;
        private double? _gearRatio;
        private double? _lead;

        private double? _speed;
        private double? _acceleration;
        private double? _deceleration;
        private double? _sCurve;
        private double? _fastDeceleration;
        private double? _positiveLimit;
        private double? _negativeLimit;

        private int? _homeInput;
        private int? _forwardLimitInput;
        private int? _reverseLimitInput;
        private double? _homeFastSpeed;
        private double? _creepSpeed;
        private double? _homeOffset;
        private int? _homeMode;

        private double? _commandPosition;
        private double? _feedbackPosition;
        private double? _actualSpeed;
        private string _axisStatusText = "—";

        private double? _targetPosition;
        private bool _isAbsoluteMove = true;

        public AxisMotionViewModel(int axisNumber, string axisName, IZmotionCard? card, ILogService? log)
        {
            AxisNumber = axisNumber;
            AxisName = axisName;
            _card = card;
            _log = log;

            ReadParametersCommand = new DelegateCommand(ReadParameters, () => IsConnected);
            SaveParametersCommand = new DelegateCommand(SaveParameters, () => IsConnected);
            ReadHomeCommand = new DelegateCommand(ReadHomeParameters, () => IsConnected);
            SaveHomeCommand = new DelegateCommand(SaveHomeParameters, () => IsConnected);
            HomeCommand = new DelegateCommand(Home, () => IsConnected);
            MoveCommand = new DelegateCommand(Move, () => IsConnected);
            JogForwardCommand = new DelegateCommand(() => Jog(forward: true), () => IsConnected);
            JogReverseCommand = new DelegateCommand(() => Jog(forward: false), () => IsConnected);
            StopCommand = new DelegateCommand(Stop, () => IsConnected);

            if (_card is null)
            {
                _lastMessage = "未连接运动卡：组合根未注册 IZmotionCard";
            }
        }

        /// <summary>轴号：0 = X，1 = Y，2 = Z。</summary>
        public int AxisNumber { get; }

        /// <summary>轴字母：X / Y / Z。</summary>
        public string AxisName { get; }

        public string AxisTitle => $"{AxisName} 轴";

        public string AxisSubtitle => $"轴号 {AxisNumber} · 单位 mm";

        // ───────────── 连接与实时状态 ─────────────

        public bool IsConnected
        {
            get => _connected;
            private set
            {
                if (SetProperty(ref _connected, value))
                {
                    RaisePropertyChanged(nameof(ConnectionText));
                    RaiseAllCanExecuteChanged();
                }
            }
        }

        public string ConnectionText => IsConnected ? "已连接" : "未连接";

        public bool IsMoving
        {
            get => _moving;
            private set
            {
                if (SetProperty(ref _moving, value))
                {
                    RaisePropertyChanged(nameof(MotionText));
                }
            }
        }

        public string MotionText => IsMoving ? "运动中" : "静止";

        public bool IsHomed
        {
            get => _homed;
            private set
            {
                if (SetProperty(ref _homed, value))
                {
                    RaisePropertyChanged(nameof(HomedText));
                }
            }
        }

        public string HomedText => IsHomed ? "已回零" : "未回零";

        public bool IsAlarm
        {
            get => _alarm;
            private set
            {
                if (SetProperty(ref _alarm, value))
                {
                    RaisePropertyChanged(nameof(AlarmText));
                }
            }
        }

        public string AlarmText => IsAlarm ? "报警" : "正常";

        public double? CommandPosition
        {
            get => _commandPosition;
            private set => SetProperty(ref _commandPosition, value);
        }

        public double? FeedbackPosition
        {
            get => _feedbackPosition;
            private set => SetProperty(ref _feedbackPosition, value);
        }

        public double? ActualSpeed
        {
            get => _actualSpeed;
            private set => SetProperty(ref _actualSpeed, value);
        }

        public string AxisStatusText
        {
            get => _axisStatusText;
            private set => SetProperty(ref _axisStatusText, value);
        }

        /// <summary>操作结果提示（成功/失败都写这里）。</summary>
        public string LastMessage
        {
            get => _lastMessage;
            private set => SetProperty(ref _lastMessage, value);
        }

        /// <summary>上一条提示是否为失败（界面据此标红）。</summary>
        public bool LastOperationFailed
        {
            get => _lastOperationFailed;
            private set => SetProperty(ref _lastOperationFailed, value);
        }

        // ───────────── 机械换算 ─────────────

        /// <summary>电机一圈的脉冲数。</summary>
        public double? PulsePerRev
        {
            get => _pulsePerRev;
            set
            {
                if (SetProperty(ref _pulsePerRev, value))
                {
                    RaisePropertyChanged(nameof(PulseEquivalentText));
                }
            }
        }

        /// <summary>减速比（电机圈 / 负载圈）。</summary>
        public double? GearRatio
        {
            get => _gearRatio;
            set
            {
                if (SetProperty(ref _gearRatio, value))
                {
                    RaisePropertyChanged(nameof(PulseEquivalentText));
                }
            }
        }

        /// <summary>导程（负载一圈走的 mm）。</summary>
        public double? Lead
        {
            get => _lead;
            set
            {
                if (SetProperty(ref _lead, value))
                {
                    RaisePropertyChanged(nameof(PulseEquivalentText));
                }
            }
        }

        /// <summary>由机械三项算出的脉冲当量（脉冲/mm）；缺项时显示“—”。</summary>
        public string PulseEquivalentText
        {
            get
            {
                if (_pulsePerRev is not > 0 || _gearRatio is not > 0 || _lead is not > 0)
                {
                    return "—（填写脉冲数 / 减速比 / 导程后自动计算）";
                }

                double units = _pulsePerRev.Value * _gearRatio.Value / _lead.Value;
                return units.ToString("F2", CultureInfo.InvariantCulture) + " 脉冲/mm";
            }
        }

        /// <summary>控制器里当前生效的脉冲当量（读取时回填，便于核对机械换算）。</summary>
        private double? _cardUnits;

        public string CardUnitsText => _cardUnits is double units
            ? units.ToString("F2", CultureInfo.InvariantCulture) + " 脉冲/mm"
            : "—";

        private void SetCardUnits(double? units)
        {
            if (_cardUnits == units)
            {
                return;
            }

            _cardUnits = units;
            RaisePropertyChanged(nameof(CardUnitsText));
        }

        // ───────────── 运动参数 ─────────────

        public double? Speed
        {
            get => _speed;
            set => SetProperty(ref _speed, value);
        }

        public double? Acceleration
        {
            get => _acceleration;
            set => SetProperty(ref _acceleration, value);
        }

        public double? Deceleration
        {
            get => _deceleration;
            set => SetProperty(ref _deceleration, value);
        }

        /// <summary>S 曲线时间（秒）。</summary>
        public double? SCurve
        {
            get => _sCurve;
            set => SetProperty(ref _sCurve, value);
        }

        public double? FastDeceleration
        {
            get => _fastDeceleration;
            set => SetProperty(ref _fastDeceleration, value);
        }

        public double? PositiveLimit
        {
            get => _positiveLimit;
            set => SetProperty(ref _positiveLimit, value);
        }

        public double? NegativeLimit
        {
            get => _negativeLimit;
            set => SetProperty(ref _negativeLimit, value);
        }

        // ───────────── 回零参数 ─────────────

        /// <summary>原点输入号（DATUM_IN）。</summary>
        public int? HomeInput
        {
            get => _homeInput;
            set => SetProperty(ref _homeInput, value);
        }

        /// <summary>正向限位输入号（FWD_IN）。</summary>
        public int? ForwardLimitInput
        {
            get => _forwardLimitInput;
            set => SetProperty(ref _forwardLimitInput, value);
        }

        /// <summary>负向限位输入号（REV_IN）。</summary>
        public int? ReverseLimitInput
        {
            get => _reverseLimitInput;
            set => SetProperty(ref _reverseLimitInput, value);
        }

        /// <summary>回零快速搜索速度（FHSPEED）。</summary>
        public double? HomeFastSpeed
        {
            get => _homeFastSpeed;
            set => SetProperty(ref _homeFastSpeed, value);
        }

        /// <summary>回零反找（慢速）速度（CREEP）。</summary>
        public double? CreepSpeed
        {
            get => _creepSpeed;
            set => SetProperty(ref _creepSpeed, value);
        }

        /// <summary>回零偏移（mm）：回零完成后坐标置为该值。</summary>
        public double? HomeOffset
        {
            get => _homeOffset;
            set => SetProperty(ref _homeOffset, value);
        }

        /// <summary>回零方式（0..7，含义随控制器固件与现场接线）。</summary>
        public int? HomeMode
        {
            get => _homeMode;
            set => SetProperty(ref _homeMode, value);
        }

        // ───────────── 单轴调试 ─────────────

        /// <summary>目标位置 / 相对距离（mm）。</summary>
        public double? TargetPosition
        {
            get => _targetPosition;
            set => SetProperty(ref _targetPosition, value);
        }

        /// <summary>true = 绝对定位，false = 相对运动。</summary>
        public bool IsAbsoluteMove
        {
            get => _isAbsoluteMove;
            set => SetProperty(ref _isAbsoluteMove, value);
        }

        // ───────────── 命令 ─────────────

        public DelegateCommand ReadParametersCommand { get; }

        public DelegateCommand SaveParametersCommand { get; }

        public DelegateCommand ReadHomeCommand { get; }

        public DelegateCommand SaveHomeCommand { get; }

        public DelegateCommand HomeCommand { get; }

        public DelegateCommand MoveCommand { get; }

        public DelegateCommand JogForwardCommand { get; }

        public DelegateCommand JogReverseCommand { get; }

        public DelegateCommand StopCommand { get; }

        /// <summary>父级轮询回调：更新连接状态（UI 线程）。</summary>
        internal void SetConnected(bool connected)
        {
            IsConnected = connected;

            if (!connected)
            {
                CommandPosition = null;
                FeedbackPosition = null;
                ActualSpeed = null;
                AxisStatusText = "未连接";
                IsMoving = false;
                IsHomed = false;
                IsAlarm = false;
                SetCardUnits(null);
            }
        }

        /// <summary>父级轮询回调：刷新一帧轴状态（UI 线程）。</summary>
        internal void ApplySnapshot(AxisMotionSnapshot snapshot)
        {
            CommandPosition = snapshot.CommandPosition;
            FeedbackPosition = snapshot.FeedbackPosition;
            ActualSpeed = snapshot.Speed;
            IsMoving = snapshot.IsMoving;
            IsHomed = snapshot.IsHomed;
            IsAlarm = snapshot.Alarm;
            AxisStatusText = BuildAxisStatus(snapshot);
        }

        /// <summary>
        /// 从控制器读回参数（连接后的首次自动读取也走这里）。
        /// </summary>
        internal void ReadParameters()
        {
            if (!TryGetCard(out IZmotionCard card))
            {
                return;
            }

            var failed = new List<string>();

            if (TryRead(card, AxisParameter.Units, "脉冲当量", failed) is double units)
            {
                SetCardUnits(units);
            }

            Speed = TryRead(card, AxisParameter.Speed, "速度", failed) ?? Speed;
            Acceleration = TryRead(card, AxisParameter.Accel, "加速度", failed) ?? Acceleration;
            Deceleration = TryRead(card, AxisParameter.Decel, "减速度", failed) ?? Deceleration;
            SCurve = TryRead(card, AxisParameter.Sramp, "S曲线", failed) ?? SCurve;
            FastDeceleration = TryRead(card, AxisParameter.FastDec, "快减减速度", failed) ?? FastDeceleration;
            PositiveLimit = TryRead(card, AxisParameter.PositiveLimit, "正向软限位", failed) ?? PositiveLimit;
            NegativeLimit = TryRead(card, AxisParameter.NegativeLimit, "负向软限位", failed) ?? NegativeLimit;

            SetStatus(failed.Count == 0
                ? "已从控制器读取运动参数"
                : $"读取运动参数完成，{failed.Count} 项失败：{string.Join("、", failed)}", failed.Count > 0);
        }

        /// <summary>从控制器读回回零参数。</summary>
        internal void ReadHomeParameters()
        {
            if (!TryGetCard(out IZmotionCard card))
            {
                return;
            }

            var failed = new List<string>();

            HomeInput = TryReadInt(card, AxisParameter.DatumInput, "原点IN", failed) ?? HomeInput;
            ForwardLimitInput = TryReadInt(card, AxisParameter.ForwardLimitInput, "正限位IN", failed) ?? ForwardLimitInput;
            ReverseLimitInput = TryReadInt(card, AxisParameter.ReverseLimitInput, "负限位IN", failed) ?? ReverseLimitInput;
            HomeFastSpeed = TryRead(card, AxisParameter.HomeFastSpeed, "回原速度", failed) ?? HomeFastSpeed;
            CreepSpeed = TryRead(card, AxisParameter.Creep, "反找速度", failed) ?? CreepSpeed;

            try
            {
                HomeOffset = Math.Round(card.GetHomeOffset(AxisNumber), 4);
            }
            catch (Exception ex)
            {
                failed.Add("还原偏移");
                _log?.Debug(ex, "{Axis} 轴读取还原偏移失败", AxisTitle);
            }

            SetStatus(failed.Count == 0
                ? "已从控制器读取回零参数"
                : $"读取回零参数完成，{failed.Count} 项失败：{string.Join("、", failed)}", failed.Count > 0);
        }

        /// <summary>保存运动参数（机械换算 + 速度/加减速/软限位）。</summary>
        private void SaveParameters()
        {
            if (!TryGetCard(out IZmotionCard card))
            {
                return;
            }

            if (PulsePerRev is not > 0 || GearRatio is not > 0 || Lead is not > 0)
            {
                SetStatus("请先填写大于 0 的脉冲数、减速比、导程（用于折算脉冲当量）", failed: true);
                return;
            }

            if (Speed is not > 0 || Acceleration is not > 0 || Deceleration is not > 0 || FastDeceleration is not > 0)
            {
                SetStatus("速度 / 加速度 / 减速度 / 快减减速度必须大于 0", failed: true);
                return;
            }

            if (PositiveLimit is not double positive || NegativeLimit is not double negative || positive <= negative)
            {
                SetStatus("正向软限位必须大于负向软限位", failed: true);
                return;
            }

            if (SCurve is not >= 0)
            {
                SetStatus("S 曲线时间不能为负", failed: true);
                return;
            }

            double units = PulsePerRev.Value * GearRatio.Value / Lead.Value;

            try
            {
                card.WriteAxisParameter(AxisNumber, AxisParameter.Units, (float)units);
                card.WriteAxisParameter(AxisNumber, AxisParameter.Speed, (float)Speed!.Value);
                card.WriteAxisParameter(AxisNumber, AxisParameter.Accel, (float)Acceleration!.Value);
                card.WriteAxisParameter(AxisNumber, AxisParameter.Decel, (float)Deceleration!.Value);
                card.WriteAxisParameter(AxisNumber, AxisParameter.Sramp, (float)SCurve!.Value);
                card.WriteAxisParameter(AxisNumber, AxisParameter.FastDec, (float)FastDeceleration!.Value);
                card.WriteAxisParameter(AxisNumber, AxisParameter.PositiveLimit, (float)positive);
                card.WriteAxisParameter(AxisNumber, AxisParameter.NegativeLimit, (float)negative);

                SetCardUnits(units);
                SetStatus($"运动参数已写入控制器（脉冲当量 {units:F2} 脉冲/mm）", failed: false);
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "{Axis} 轴写运动参数失败", AxisTitle);
                SetStatus($"写运动参数失败：{ex.Message}", failed: true);
            }
        }

        /// <summary>保存回零参数（输入号 + 快慢速度 + 偏移）。</summary>
        private void SaveHomeParameters()
        {
            if (!TryGetCard(out IZmotionCard card))
            {
                return;
            }

            if (HomeMode is not int mode || mode < HomeModeMin || mode > HomeModeMax)
            {
                SetStatus($"回零方式必须是 {HomeModeMin}..{HomeModeMax} 的整数", failed: true);
                return;
            }

            if (HomeInput is not >= 0 || ForwardLimitInput is not >= 0 || ReverseLimitInput is not >= 0)
            {
                SetStatus("原点 / 正限位 / 负限位的输入号必须是非负整数", failed: true);
                return;
            }

            if (HomeFastSpeed is not >= 0 || CreepSpeed is not >= 0)
            {
                SetStatus("回原速度 / 反找速度不能为负", failed: true);
                return;
            }

            try
            {
                card.WriteAxisParameter(AxisNumber, AxisParameter.DatumInput, HomeInput!.Value);
                card.WriteAxisParameter(AxisNumber, AxisParameter.ForwardLimitInput, ForwardLimitInput!.Value);
                card.WriteAxisParameter(AxisNumber, AxisParameter.ReverseLimitInput, ReverseLimitInput!.Value);
                card.WriteAxisParameter(AxisNumber, AxisParameter.HomeFastSpeed, (float)HomeFastSpeed!.Value);
                card.WriteAxisParameter(AxisNumber, AxisParameter.Creep, (float)CreepSpeed!.Value);

                if (HomeOffset is double offset)
                {
                    card.SetHomeOffset(AxisNumber, (float)offset);
                }

                SetStatus("回零参数已写入控制器", failed: false);
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "{Axis} 轴写回零参数失败", AxisTitle);
                SetStatus($"写回零参数失败：{ex.Message}", failed: true);
            }
        }

        /// <summary>单轴回零。</summary>
        private void Home()
        {
            if (!TryGetCard(out IZmotionCard card))
            {
                return;
            }

            if (HomeMode is not int mode || mode < HomeModeMin || mode > HomeModeMax)
            {
                SetStatus($"回零方式必须是 {HomeModeMin}..{HomeModeMax} 的整数", failed: true);
                return;
            }

            try
            {
                card.GoHome(AxisNumber, mode);
                SetStatus($"已启动回零（方式 {mode}）", failed: false);
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "{Axis} 轴回零失败", AxisTitle);
                SetStatus($"回零失败：{ex.Message}", failed: true);
            }
        }

        /// <summary>单轴定位 / 相对运动。</summary>
        private void Move()
        {
            if (!TryGetCard(out IZmotionCard card))
            {
                return;
            }

            if (TargetPosition is not double target)
            {
                SetStatus("请填写目标位置", failed: true);
                return;
            }

            try
            {
                if (IsAbsoluteMove)
                {
                    card.MoveAbsolute(AxisNumber, (float)target);
                    SetStatus($"已下发绝对定位：{target:F3} mm", failed: false);
                }
                else
                {
                    card.MoveRelative(AxisNumber, (float)target);
                    SetStatus($"已下发相对运动：{target:F3} mm", failed: false);
                }
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "{Axis} 轴定位失败", AxisTitle);
                SetStatus($"定位失败：{ex.Message}", failed: true);
            }
        }

        /// <summary>点动：开始后持续运动，直到按“停止”。</summary>
        private void Jog(bool forward)
        {
            if (!TryGetCard(out IZmotionCard card))
            {
                return;
            }

            try
            {
                card.Jog(AxisNumber, forward);
                SetStatus($"{AxisTitle} 开始{(forward ? "正向" : "反向")}点动，按“停止”结束", failed: false);
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "{Axis} 轴点动失败", AxisTitle);
                SetStatus($"点动失败：{ex.Message}", failed: true);
            }
        }

        /// <summary>停止当前单轴运动。</summary>
        private void Stop()
        {
            if (!TryGetCard(out IZmotionCard card))
            {
                return;
            }

            try
            {
                card.StopJog(AxisNumber);
                SetStatus($"{AxisTitle} 已停止", failed: false);
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "{Axis} 轴停止失败", AxisTitle);
                SetStatus($"停止失败：{ex.Message}", failed: true);
            }
        }

        private bool TryGetCard(out IZmotionCard card)
        {
            card = _card!;

            if (_card is null)
            {
                SetStatus("没有运动卡（组合根未注册 IZmotionCard），无法操作", failed: true);
                return false;
            }

            if (!IsConnected)
            {
                SetStatus("运动卡未连接", failed: true);
                return false;
            }

            return true;
        }

        private double? TryRead(IZmotionCard card, AxisParameter parameter, string label, List<string> failed)
        {
            try
            {
                // 控制器参数是 float，直接显示会出现 0.050000000745058 一类的尾数；
                // 保留 4 位小数既够工艺精度，也不把浮点噪声摊在界面上
                return Math.Round(card.ReadAxisParameter(AxisNumber, parameter), 4);
            }
            catch (Exception ex)
            {
                failed.Add(label);
                _log?.Debug(ex, "{Axis} 轴读取 {Label} 失败", AxisTitle, label);
                return null;
            }
        }

        private int? TryReadInt(IZmotionCard card, AxisParameter parameter, string label, List<string> failed)
        {
            double? value = TryRead(card, parameter, label, failed);
            return value is double number ? (int)Math.Round(number) : null;
        }

        private void SetStatus(string message, bool failed)
        {
            LastMessage = message;
            LastOperationFailed = failed;
        }

        //通知所有命令检查修改可执行状态
        private void RaiseAllCanExecuteChanged()
        {
            ReadParametersCommand.RaiseCanExecuteChanged();
            SaveParametersCommand.RaiseCanExecuteChanged();
            ReadHomeCommand.RaiseCanExecuteChanged();
            SaveHomeCommand.RaiseCanExecuteChanged();
            HomeCommand.RaiseCanExecuteChanged();
            MoveCommand.RaiseCanExecuteChanged();
            JogForwardCommand.RaiseCanExecuteChanged();
            JogReverseCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }

        private static string BuildAxisStatus(AxisMotionSnapshot snapshot)
        {
            string status = snapshot.IsMoving ? "运动中" : "静止";
            status += snapshot.IsHomed ? " · 已回零" : " · 未回零";

            if (snapshot.PositiveLimit)
            {
                status += " · 正限位";
            }

            if (snapshot.NegativeLimit)
            {
                status += " · 负限位";
            }

            if (snapshot.Alarm)
            {
                status += " · 报警";
            }

            return status;
        }
    }
}

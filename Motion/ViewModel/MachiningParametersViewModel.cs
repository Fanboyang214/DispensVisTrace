using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Motion.ViewModel
{
    /// <summary>
    /// 加工参数页的视图模型：高度、速度、胶阀响应时间，以及由此换算的提前量。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这里用可空数值承接输入框，允许操作员清空后再填；生成轨迹前调用
    /// <see cref="TryBuild"/> 得到一份不可变快照，校验失败就把原因显示在页脚。
    /// </para>
    /// <para>
    /// 本页参数只参与“轨迹重排 + 提前开关胶 IO”的生成，不写运动控制器；
    /// 轨迹生成后 IO 已经落到各段的 <see cref="IoTrigger"/> 上，运行期不再依赖本页。
    /// </para>
    /// </remarks>
    public sealed class MachiningParametersViewModel : BindableBase
    {
        private double? _workZ = MachiningParameters.DefaultWorkZ;
        private double? _liftZ = MachiningParameters.DefaultLiftZ;
        private double? _dispenseSpeed = MachiningParameters.DefaultDispenseSpeed;
        private double? _moveSpeed = MachiningParameters.DefaultMoveSpeed;
        private double? _valveOpenResponseMs = MachiningParameters.DefaultValveOpenResponseMs;
        private double? _valveCloseResponseMs = MachiningParameters.DefaultValveCloseResponseMs;
        private string _validationMessage = string.Empty;
        private bool _validationFailed;

        public MachiningParametersViewModel()
        {
            ResetCommand = new DelegateCommand(Reset);
            UpdateValidation();
        }

        /// <summary>点胶工作高度（mm）。</summary>
        public double? WorkZ
        {
            get => _workZ;
            set
            {
                if (SetProperty(ref _workZ, value))
                {
                    OnParameterChanged();
                }
            }
        }

        /// <summary>抬刀高度（mm）。</summary>
        public double? LiftZ
        {
            get => _liftZ;
            set
            {
                if (SetProperty(ref _liftZ, value))
                {
                    OnParameterChanged();
                }
            }
        }

        /// <summary>点胶段速度（mm/s）。</summary>
        public double? DispenseSpeed
        {
            get => _dispenseSpeed;
            set
            {
                if (SetProperty(ref _dispenseSpeed, value))
                {
                    OnParameterChanged();
                }
            }
        }

        /// <summary>空移段速度（mm/s）。</summary>
        public double? MoveSpeed
        {
            get => _moveSpeed;
            set
            {
                if (SetProperty(ref _moveSpeed, value))
                {
                    OnParameterChanged();
                }
            }
        }

        /// <summary>开阀响应时间（ms）。</summary>
        public double? ValveOpenResponseMs
        {
            get => _valveOpenResponseMs;
            set
            {
                if (SetProperty(ref _valveOpenResponseMs, value))
                {
                    OnParameterChanged();
                }
            }
        }

        /// <summary>关阀响应时间（ms）。</summary>
        public double? ValveCloseResponseMs
        {
            get => _valveCloseResponseMs;
            set
            {
                if (SetProperty(ref _valveCloseResponseMs, value))
                {
                    OnParameterChanged();
                }
            }
        }

        /// <summary>开阀提前量读数（空移速度 × 开阀响应）。</summary>
        public string OpenLeadText =>
            MoveSpeed is double speed && ValveOpenResponseMs is double response
                ? $"{speed * response / 1000.0:F3} mm"
                : "—";

        /// <summary>关阀提前量读数（点胶速度 × 关阀响应）。</summary>
        public string CloseLeadText =>
            DispenseSpeed is double speed && ValveCloseResponseMs is double response
                ? $"{speed * response / 1000.0:F3} mm"
                : "—";

        /// <summary>页脚校验提示。</summary>
        public string ValidationMessage
        {
            get => _validationMessage;
            private set => SetProperty(ref _validationMessage, value);
        }

        /// <summary>校验是否失败（用于把提示染成告警色）。</summary>
        public bool ValidationFailed
        {
            get => _validationFailed;
            private set => SetProperty(ref _validationFailed, value);
        }

        /// <summary>恢复默认参数。</summary>
        public DelegateCommand ResetCommand { get; }

        /// <summary>
        /// 取一份校验通过的不可变快照。
        /// </summary>
        public bool TryBuild(
            [NotNullWhen(true)] out MachiningParameters? parameters,
            out string error)
        {
            if (WorkZ is not double workZ ||
                LiftZ is not double liftZ ||
                DispenseSpeed is not double dispenseSpeed ||
                MoveSpeed is not double moveSpeed ||
                ValveOpenResponseMs is not double openResponse ||
                ValveCloseResponseMs is not double closeResponse)
            {
                parameters = null;
                error = "请填写完整的加工参数（高度 / 速度 / 胶阀响应）。";
                return false;
            }

            var snapshot = new MachiningParameters
            {
                WorkZ = workZ,
                LiftZ = liftZ,
                DispenseSpeed = dispenseSpeed,
                MoveSpeed = moveSpeed,
                ValveOpenResponseMs = openResponse,
                ValveCloseResponseMs = closeResponse,
            };

            if (!snapshot.TryValidate(out error))
            {
                parameters = null;
                return false;
            }

            parameters = snapshot;
            return true;
        }

        private void Reset()
        {
            _workZ = MachiningParameters.DefaultWorkZ;
            _liftZ = MachiningParameters.DefaultLiftZ;
            _dispenseSpeed = MachiningParameters.DefaultDispenseSpeed;
            _moveSpeed = MachiningParameters.DefaultMoveSpeed;
            _valveOpenResponseMs = MachiningParameters.DefaultValveOpenResponseMs;
            _valveCloseResponseMs = MachiningParameters.DefaultValveCloseResponseMs;

            RaisePropertyChanged(nameof(WorkZ));
            RaisePropertyChanged(nameof(LiftZ));
            RaisePropertyChanged(nameof(DispenseSpeed));
            RaisePropertyChanged(nameof(MoveSpeed));
            RaisePropertyChanged(nameof(ValveOpenResponseMs));
            RaisePropertyChanged(nameof(ValveCloseResponseMs));
            OnParameterChanged();
        }

        private void OnParameterChanged()
        {
            RaisePropertyChanged(nameof(OpenLeadText));
            RaisePropertyChanged(nameof(CloseLeadText));
            UpdateValidation();
        }

        private void UpdateValidation()
        {
            bool valid = TryBuild(out _, out string error);
            ValidationFailed = !valid;
            ValidationMessage = valid
                ? "参数有效：生成轨迹时会按以上值统一点胶段高度/速度，并换算开关阀提前量。"
                : error;
        }
    }
}

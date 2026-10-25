using Core.Motion;
using Microsoft.Win32;
using Prism.Commands;
using Prism.Ioc;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Motion.ViewModel
{
    /// <summary>
    /// 轨迹段的显示模型：几何、端点、IO 标记 + 列表里展示的文本。
    /// </summary>
    public class TrajectorySegmentViewModel : BindableBase
    {
        private bool _isSelected;
        private Geometry _geometry = Geometry.Empty;
        private Point _startPoint;
        private Point _endPoint;

        public TrajectorySegmentViewModel(TrajectorySegment trajectory, int index)
        {
            Trajectory = trajectory ?? throw new ArgumentNullException(nameof(trajectory));
            Index = index;
        }

        /// <summary>对应的轨迹段（只读引用，显示变换不修改它）。</summary>
        public TrajectorySegment Trajectory { get; }

        /// <summary>在列表中的序号（0 起）。</summary>
        public int Index { get; }

        /// <summary>列表里展示的 1 起序号。</summary>
        public int DisplayIndex => Index + 1;

        /// <summary>是否被选中（由编辑器统一维护）。</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>画布上的段几何（已做等比缩放与 Y 翻转）。</summary>
        public Geometry Geometry
        {
            get => _geometry;
            private set => SetProperty(ref _geometry, value);
        }

        /// <summary>起点画布坐标（端点标记用）。</summary>
        public Point StartPoint
        {
            get => _startPoint;
            private set => SetProperty(ref _startPoint, value);
        }

        /// <summary>终点画布坐标（端点标记用）。</summary>
        public Point EndPoint
        {
            get => _endPoint;
            private set => SetProperty(ref _endPoint, value);
        }

        /// <summary>本段的 IO 标记（画布坐标）。</summary>
        public ObservableCollection<IoMarkerViewModel> IoMarkers { get; } = new();

        public bool IsArc => Trajectory.SegType == SegmentType.Arc;

        public bool IsMove => Trajectory.MotionMode == MotionMode.Move;

        public string TypeText => IsArc ? "圆弧" : "直线";

        public string ModeText => IsMove ? "空移" : "点胶";

        public string LengthText => $"{Trajectory.SegmentLength:F2} mm";

        public string SpeedText => $"{Trajectory.Speed:F0} mm/s";

        public string IoCountText =>
            Trajectory.IoTriggers.Count == 0 ? "无 IO" : $"{Trajectory.IoTriggers.Count} 个 IO";

        public string PointText => $"{Trajectory.StartPt} → {Trajectory.EndPt}";

        /// <summary>
        /// 按显示变换刷新几何与 IO 标记；模型坐标保持不变。
        /// </summary>
        public void UpdateDisplay(TrajectoryViewTransform transform)
        {
            ArgumentNullException.ThrowIfNull(transform);

            StartPoint = transform.Map(Trajectory.StartPt);
            EndPoint = transform.Map(Trajectory.EndPt);
            Geometry = BuildGeometry(transform);

            IoMarkers.Clear();
            foreach (IoTrigger? trigger in Trajectory.IoTriggers)
            {
                if (trigger is null)
                {
                    continue;
                }

                Point point = transform.Map(Trajectory.GetPointByDist(trigger.DistanceOnSegment));
                bool isValveOn = trigger.Action == IoActionType.ValveOn;
                IoMarkers.Add(new IoMarkerViewModel(
                    point.X,
                    point.Y,
                    isValveOn,
                    $"{(isValveOn ? "开阀" : "关阀")} · 段内 {trigger.DistanceOnSegment:F2} mm"));
            }
        }

        private Geometry BuildGeometry(TrajectoryViewTransform transform)
        {
            Point start = transform.Map(Trajectory.StartPt);
            Point end = transform.Map(Trajectory.EndPt);

            if (Trajectory.SegType != SegmentType.Arc)
            {
                return new LineGeometry(start, end);
            }

            double radius = Math.Max(0.5, Math.Abs(Trajectory.Radius) * transform.Scale);
            double sweep = Trajectory.CircleSweep;

            if (sweep >= 2 * Math.PI - 1e-9)
            {
                Point center = transform.Map(Trajectory.ArcCenter);
                return new EllipseGeometry(center, radius, radius);
            }

            var figure = new PathFigure
            {
                StartPoint = start,
                IsClosed = false,
                IsFilled = false,
            };
            figure.Segments.Add(new ArcSegment(
                end,
                new Size(radius, radius),
                0,
                sweep > Math.PI,
                SweepDirection.Clockwise,
                true));

            return new PathGeometry(new[] { figure });
        }
    }

    /// <summary>
    /// 画布上的开/关阀标记。
    /// </summary>
    public sealed class IoMarkerViewModel
    {
        private const double MarkerSize = 16;

        public IoMarkerViewModel(double x, double y, bool isValveOn, string toolTipText)
        {
            X = x;
            Y = y;
            IsValveOn = isValveOn;
            ToolTipText = toolTipText;
            Left = x - MarkerSize / 2;
            Top = y - MarkerSize / 2;
        }

        public double X { get; }

        public double Y { get; }

        /// <summary>标记左上角（供 Canvas.Left 绑定）。</summary>
        public double Left { get; }

        /// <summary>标记左上角（供 Canvas.Top 绑定）。</summary>
        public double Top { get; }

        public bool IsValveOn { get; }

        public string ToolTipText { get; }
    }

    /// <summary>
    /// 轨迹编辑器视图模型：显示、重排、导入、校验、按加工参数生成 IO。
    /// </summary>
    public sealed class TrajectoryEditorViewModel : BindableBase
    {
        /// <summary>预览画布宽（px）。</summary>
        public const double PreviewWidth = 720;

        /// <summary>预览画布高（px）。</summary>
        public const double PreviewHeight = 520;

        private const double PreviewMargin = 28;

        private readonly TrajectoryEditor _editor;
        private readonly MachiningParametersViewModel _machining;
        private readonly TrajectoryViewTransform _transform = new();

        private ObservableCollection<TrajectorySegmentViewModel> _segments = new();
        private TrajectorySegmentViewModel? _selectedSegment;
        private string _statusMessage = "就绪";
        private bool _statusFailed;
        private IReadOnlyList<string> _planWarnings = Array.Empty<string>();

        public TrajectoryEditorViewModel(IContainerProvider containerProvider)
        {
            ArgumentNullException.ThrowIfNull(containerProvider);

            _editor = containerProvider.Resolve<TrajectoryEditor>();
            _machining = TryResolve<MachiningParametersViewModel>(containerProvider)
                ?? new MachiningParametersViewModel();

            MoveUpCommand = new DelegateCommand<TrajectorySegmentViewModel?>(
                segment => Move(segment, -1),
                segment => segment is not null);
            MoveDownCommand = new DelegateCommand<TrajectorySegmentViewModel?>(
                segment => Move(segment, 1),
                segment => segment is not null);
            DeleteCommand = new DelegateCommand<TrajectorySegmentViewModel?>(
                Delete,
                segment => segment is not null);
            UndoCommand = new DelegateCommand(Undo, () => _editor.CanUndo);
            RedoCommand = new DelegateCommand(Redo, () => _editor.CanRedo);
            ValidateCommand = new DelegateCommand(Validate);
            ImportCadCommand = new DelegateCommand(ImportCad);
            GenerateProcessCommand = new DelegateCommand(
                GenerateProcess,
                () => Segments.Count > 0);

            Refresh();
        }

        public ObservableCollection<TrajectorySegmentViewModel> Segments
        {
            get => _segments;
            private set => SetProperty(ref _segments, value);
        }

        public ObservableCollection<IoMarkerViewModel> IoMarkers { get; } = new();

        public ObservableCollection<string> Warnings { get; } = new();

        public TrajectorySegmentViewModel? SelectedSegment
        {
            get => _selectedSegment;
            set
            {
                if (!SetProperty(ref _selectedSegment, value))
                {
                    return;
                }

                foreach (TrajectorySegmentViewModel segment in Segments)
                {
                    segment.IsSelected = ReferenceEquals(segment, value);
                }

                RaisePropertyChanged(nameof(HasSelection));
                RefreshCommandStates();
            }
        }

        public bool HasSelection => SelectedSegment is not null;

        public bool HasSegments => Segments.Count > 0;

        public string SegmentCountText =>
            $"{Segments.Count} 段 · 点胶 " +
            $"{Segments.Count(segment => segment.Trajectory.MotionMode == MotionMode.Dispense)} · 空移 " +
            $"{Segments.Count(segment => segment.Trajectory.MotionMode == MotionMode.Move)}";

        public string IoCountText
        {
            get
            {
                int valveOn = Segments
                    .SelectMany(segment => segment.Trajectory.IoTriggers)
                    .Count(trigger => trigger.Action == IoActionType.ValveOn);
                int valveOff = Segments
                    .SelectMany(segment => segment.Trajectory.IoTriggers)
                    .Count(trigger => trigger.Action == IoActionType.ValveOff);
                return $"开阀 {valveOn} · 关阀 {valveOff}";
            }
        }

        public string TotalLengthText =>
            $"{Segments.Sum(segment => segment.Trajectory.SegmentLength):F2} mm";

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public bool StatusFailed
        {
            get => _statusFailed;
            private set => SetProperty(ref _statusFailed, value);
        }

        public DelegateCommand<TrajectorySegmentViewModel?> MoveUpCommand { get; }

        public DelegateCommand<TrajectorySegmentViewModel?> MoveDownCommand { get; }

        public DelegateCommand<TrajectorySegmentViewModel?> DeleteCommand { get; }

        public DelegateCommand UndoCommand { get; }

        public DelegateCommand RedoCommand { get; }

        public DelegateCommand ValidateCommand { get; }

        public DelegateCommand ImportCadCommand { get; }

        /// <summary>
        /// 去掉空移段、在断开的点胶段之间补空移段，并按加工参数生成提前开关胶 IO。
        /// </summary>
        public DelegateCommand GenerateProcessCommand { get; }

        /// <summary>
        /// 重建显示模型；可保留选中项、附带本次规划的告警。
        /// </summary>
        public void Refresh(
            int? selectIndex = null,
            IReadOnlyList<string>? planWarnings = null)
        {
            int? preservedIndex = selectIndex ?? SelectedSegment?.Index;
            _planWarnings = planWarnings ?? Array.Empty<string>();

            _transform.Update(_editor.Segments, PreviewWidth, PreviewHeight, PreviewMargin);

            var segments = new ObservableCollection<TrajectorySegmentViewModel>();
            for (int i = 0; i < _editor.Segments.Count; i++)
            {
                TrajectorySegment segment = _editor.Segments[i];
                if (segment is null)
                {
                    continue;
                }

                var viewModel = new TrajectorySegmentViewModel(segment, i);
                viewModel.UpdateDisplay(_transform);
                segments.Add(viewModel);
            }

            Segments = segments;

            IoMarkers.Clear();
            foreach (TrajectorySegmentViewModel segment in Segments)
            {
                foreach (IoMarkerViewModel marker in segment.IoMarkers)
                {
                    IoMarkers.Add(marker);
                }
            }

            SelectedSegment = preservedIndex is int index && index >= 0 && index < Segments.Count
                ? Segments[index]
                : null;

            RunValidate();

            RaisePropertyChanged(nameof(HasSegments));
            RaisePropertyChanged(nameof(SegmentCountText));
            RaisePropertyChanged(nameof(IoCountText));
            RaisePropertyChanged(nameof(TotalLengthText));
            RefreshCommandStates();
        }

        private void Move(TrajectorySegmentViewModel? segment, int delta)
        {
            if (segment is null)
            {
                return;
            }

            int oldIndex = Segments.IndexOf(segment);
            int newIndex = oldIndex + delta;
            if (oldIndex < 0 || newIndex < 0 || newIndex >= Segments.Count)
            {
                return;
            }

            _editor.MoveSegment(oldIndex, newIndex);
            StatusFailed = false;
            StatusMessage = delta < 0
                ? $"第 {oldIndex + 1} 段上移到第 {newIndex + 1} 段"
                : $"第 {oldIndex + 1} 段下移到第 {newIndex + 1} 段";
            Refresh(selectIndex: newIndex);
        }

        private void Delete(TrajectorySegmentViewModel? segment)
        {
            if (segment is null)
            {
                return;
            }

            int index = Segments.IndexOf(segment);
            if (index < 0)
            {
                return;
            }

            _editor.RemoveSegment(index);
            StatusFailed = false;
            StatusMessage = $"已删除第 {index + 1} 段";

            int count = _editor.Segments.Count;
            Refresh(selectIndex: count == 0 ? null : Math.Min(index, count - 1));
        }

        private void Undo()
        {
            if (!_editor.CanUndo)
            {
                return;
            }

            _editor.Undo();
            StatusFailed = false;
            StatusMessage = "已撤销上一步修改";
            Refresh(selectIndex: SelectedSegment?.Index);
        }

        private void Redo()
        {
            if (!_editor.CanRedo)
            {
                return;
            }

            _editor.Redo();
            StatusFailed = false;
            StatusMessage = "已重做上一步修改";
            Refresh(selectIndex: SelectedSegment?.Index);
        }

        private void Validate()
        {
            RunValidate();
            StatusFailed = Warnings.Count > 0;
            StatusMessage = Warnings.Count == 0
                ? "轨迹校验通过"
                : $"发现 {Warnings.Count} 条告警";
        }

        private void ImportCad()
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入轨迹 DXF",
                Filter = "DXF 图纸 (*.dxf)|*.dxf|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                List<TrajectorySegment> segments = DxfParser.Parse(dialog.FileName);
                if (segments.Count == 0)
                {
                    StatusFailed = true;
                    StatusMessage = "DXF 中没有 Dispense / Move 图层的可用图元。";
                    return;
                }

                _editor.LoadSegments(segments);
                StatusFailed = false;
                StatusMessage = $"已导入 {segments.Count} 段：{Path.GetFileName(dialog.FileName)}";
                Refresh(selectIndex: 0);
            }
            catch (Exception ex)
            {
                StatusFailed = true;
                StatusMessage = "导入失败：" + ex.Message;
            }
        }

        private void GenerateProcess()
        {
            if (!_machining.TryBuild(out MachiningParameters? parameters, out string error))
            {
                StatusFailed = true;
                StatusMessage = "加工参数无效：" + error;
                return;
            }

            PlannedTrajectory plan = _editor.ApplyProcess(parameters);
            if (!plan.HasDispenseSegments || plan.Segments.Count == 0)
            {
                StatusFailed = true;
                StatusMessage = plan.Warnings.FirstOrDefault() ?? "没有可加工的点胶段。";
                Refresh(planWarnings: plan.Warnings);
                return;
            }

            StatusFailed = false;
            string warningSuffix = plan.Warnings.Count == 0
                ? string.Empty
                : $"（{plan.Warnings.Count} 条告警）";
            StatusMessage =
                $"已重排：删除空移 {plan.RemovedMoveCount} 段，插入空移 {plan.InsertedMoveCount} 段，" +
                $"生成开阀 {plan.ValveOnCount} / 关阀 {plan.ValveOffCount} 个触发{warningSuffix}";
            Refresh(selectIndex: 0, planWarnings: plan.Warnings);
        }

        private void RunValidate()
        {
            Warnings.Clear();

            foreach (string warning in _planWarnings)
            {
                Warnings.Add("规划：" + warning);
            }

            foreach (string warning in _editor.ValidateAllSegments())
            {
                Warnings.Add(warning);
            }
        }

        private void RefreshCommandStates()
        {
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
            RedoCommand.RaiseCanExecuteChanged();
            GenerateProcessCommand.RaiseCanExecuteChanged();
        }

        private static T? TryResolve<T>(IContainerProvider containerProvider)
            where T : class
        {
            try
            {
                return containerProvider.Resolve<T>();
            }
            catch
            {
                return null;
            }
        }
    }
}

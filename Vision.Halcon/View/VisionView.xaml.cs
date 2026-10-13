using HalconDotNet;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vision.Halcon.ViewModels;

namespace Vision.Halcon.View
{
    /// <summary>
    /// 视觉监视画面：三块 HALCON 窗口分别显示定位 / 检测 / 点胶的实时图像（2448×2048），
    /// 信息区由 <see cref="VisionViewModel"/> 的绑定驱动。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么显示放在 code-behind：</b>HALCON 的窗口显示是命令式的
    /// （读图 → <c>DispObj</c> → 按窗口宽高比 <c>SetPart</c>），没有可绑定的“当前图像”属性；
    /// 视图负责“把路径变成像素”，产品号 / 位姿 / 判定 / 轴位置仍全部走绑定。
    /// </para>
    /// <para>
    /// <b>线程与合帧：</b>“图像就绪”总线在泵线程上通知，这里统一投递回 UI 线程再读图；
    /// 同一块画面在排队期间只保留<b>最新</b>一帧，慢一帧不会排队把 UI 拖住。
    /// </para>
    /// </remarks>
    public partial class VisionView : UserControl
    {
        private readonly Dictionary<VisionPanel, HSmartWindowControlWPF> _windows;
        private readonly Dictionary<VisionPanel, string> _latestFrames = new();
        private readonly HashSet<VisionPanel> _renderQueued = new();
        private readonly Dictionary<VisionPanel, HImage> _shownImages = new();

        private VisionViewModel? _viewModel;

        public VisionView()
        {
            InitializeComponent();

            _windows = new Dictionary<VisionPanel, HSmartWindowControlWPF>
            {
                [VisionPanel.Locate] = LocateWindow,
                [VisionPanel.Inspect] = InspectWindow,
                [VisionPanel.Dispense] = DispenseWindow
            };

            // 窗口尺寸变了要重新算可见区域，否则一开始那帧的等比结果会被拉伸
            LocateWindow.SizeChanged += OnWindowSizeChanged;
            InspectWindow.SizeChanged += OnWindowSizeChanged;
            DispenseWindow.SizeChanged += OnWindowSizeChanged;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))//非程序真正运行直接返回
            {
                return;
            }

            if (_viewModel is null)
            {
                VisionViewModel? viewModel = DataContext as VisionViewModel;

                if (viewModel is null)
                {
                    // 视图是被 new 出来的（不是区域导航创建的）时自己取容器里的单例 VM；
                    // 设计器 / 没装配容器时保持空视图，而不是把异常抛到界面上。
                    try
                    {
                        viewModel = ContainerLocator.Container.Resolve<VisionViewModel>();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    DataContext = viewModel;
                }

                _viewModel = viewModel;
            }

            // Loaded 可能发生多次（切页签再切回来）：先减后加，保证只订阅一次
            _viewModel.FrameReady -= OnFrameReady;
            _viewModel.FrameReady += OnFrameReady;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel is not null)
            {
                _viewModel.FrameReady -= OnFrameReady;
            }

            // 窗口已离开视觉树：释放显示中的 HALCON 图像，别在后台留着 5 MB/块的缓冲
            foreach (HImage image in _shownImages.Values)
            {
                image.Dispose();
            }

            _shownImages.Clear();
            _latestFrames.Clear();
            _renderQueued.Clear();
        }

        private void OnFrameReady(object? sender, VisionFrameReadyEventArgs e)
        {
            if (Dispatcher.CheckAccess())//处于UI线程，直接更新UI
            {
                QueueRender(e.Panel, e.ImagePath);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => QueueRender(e.Panel, e.ImagePath)));
        }

        /// <summary>
        /// 排队渲染一块画面；排队期间又来了新帧只更新路径，不重复排队。
        /// </summary>
        private void QueueRender(VisionPanel panel, string imagePath)
        {
            _latestFrames[panel] = imagePath;

            if (!_renderQueued.Add(panel))//已经在渲染队列不重复排队
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _renderQueued.Remove(panel);//渲染图像，移出队列

                if (!_latestFrames.TryGetValue(panel, out string? latest) || latest is null)
                {
                    return;
                }

                Display(panel, latest);
            }));
        }

        private void Display(VisionPanel panel, string imagePath)
        {
            if (!_windows.TryGetValue(panel, out HSmartWindowControlWPF? window) || window is null || !window.IsLoaded)
            {
                return;
            }

            HImage? image = null;

            try
            {
                // 让 HALCON 自己按扩展名解码归档图，和引擎读的是同一张
                image = new HImage(imagePath);

                HWindow? halconWindow = window.HalconWindow;
                if (halconWindow is null)
                {
                    image.Dispose();
                    return;
                }

                // 先显示，再按窗口宽高比设可见区域：2448×2048 等比缩放进窗口，多出来的方向留黑边
                halconWindow.DispObj(image);
                FitImagePart(window, halconWindow, image);

                // 上一帧的 HImage 已不在窗口里（显示缓冲是窗口自己的），可以安全释放
                if (_shownImages.TryGetValue(panel, out HImage? previous))
                {
                    previous.Dispose();
                }

                _shownImages[panel] = image;
            }
            catch (Exception ex)
            {
                // 读图失败（共享目录不通 / 文件被清理 / 半个文件）只影响这一块画面，不能让视图崩
                image?.Dispose();
                _viewModel?.ReportFrameDisplayError(panel, ex.Message);
            }
        }

        /// <summary>窗口尺寸变化：用当前显示中的图重新算可见区域（不需要等下一帧）。</summary>
        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not HSmartWindowControlWPF control || control.HalconWindow is null)
            {
                return;
            }

            foreach (KeyValuePair<VisionPanel, HSmartWindowControlWPF> pair in _windows)
            {
                if (!ReferenceEquals(pair.Value, control))//比较是否是目标控件
                {
                    continue;
                }

                if (_shownImages.TryGetValue(pair.Key, out HImage? image))
                {
                    try
                    {
                        FitImagePart(control, control.HalconWindow, image);
                    }
                    catch (Exception)
                    {
                        // 布局过程中窗口可能正在重建：重设 part 失败不影响这一帧的显示
                    }
                }

                return;
            }
        }

        /// <summary>
        /// 把可见区域设成“整幅图 + 周围留黑边”。
        /// </summary>
        /// <remarks>
        /// <see cref="HSmartWindowControlWPF.SetFullImagePart"/> 只是把 part 设成图幅，
        /// 窗口宽高比与 2448:2048 不一致时 HALCON 会把图<b>拉扁</b>（实测：横长窗口里圆变椭圆）。
        /// 所以按窗口宽高比把 part 在一侧扩出去，让多出来的地方显示窗口背景，图像本身保持真实比例。
        /// </remarks>
        private static void FitImagePart(HSmartWindowControlWPF control, HWindow window, HImage image)
        {
            image.GetImageSize(out int imageWidth, out int imageHeight);

            // 用控件实际排版尺寸，而不是 HSmartWindowControlWPF.WindowSize：
            // 后者在布局后仍可能是旧值（实测比真实客户区宽），按它算 part 会把图压窄。
            double viewWidth = control.ActualWidth;
            double viewHeight = control.ActualHeight;

            if (imageWidth <= 0 || imageHeight <= 0 || viewWidth <= 1 || viewHeight <= 1)
            {
                control.SetFullImagePart(image);
                return;
            }

            double imageAspect = (double)imageWidth / imageHeight;
            double viewAspect = viewWidth / viewHeight;

            double row1 = 0;
            double row2 = imageHeight;
            double col1 = 0;
            double col2 = imageWidth;

            if (viewAspect > imageAspect)
            {
                // 窗口比图更宽：左右补空
                double extra = (imageHeight * viewAspect - imageWidth) / 2.0;
                col1 -= extra;
                col2 += extra;
            }
            else
            {
                // 窗口比图更高：上下补空
                double extra = (imageWidth / viewAspect - imageHeight) / 2.0;
                row1 -= extra;
                row2 += extra;
            }

            window.SetPart(new HTuple(row1), new HTuple(col1), new HTuple(row2), new HTuple(col2));
        }
    }
}

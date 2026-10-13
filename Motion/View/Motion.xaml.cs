using Motion.ViewModel;
using Prism.Ioc;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Motion.View
{
    /// <summary>
    /// 运动页（X/Y/Z 单轴 + 前瞻设置）。
    /// </summary>
    /// <remarks>
    /// 视图本身只做布局；数据与命令都在 <see cref="MotionViewModel"/>。
    /// 区域导航时 DataContext 由 Prism 注入；直接 <c>&lt;local:Motion/&gt;</c> 嵌入时，
    /// 这里从容器取同一份单例 VM（没装配容器就保持空视图，不把异常抛到界面）。
    /// </remarks>
    public partial class Motion : UserControl
    {
        private MotionViewModel? _viewModel;

        public Motion()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this) || _viewModel is not null)
            {
                return;
            }

            MotionViewModel? viewModel = DataContext as MotionViewModel;

            if (viewModel is null)
            {
                try
                {
                    viewModel = ContainerLocator.Container.Resolve<MotionViewModel>();
                }
                catch (Exception)
                {
                    return;
                }

                DataContext = viewModel;
            }

            _viewModel = viewModel;
        }

        private void AxisMotion_Loaded(object sender, RoutedEventArgs e)
        {

        }
    }
}

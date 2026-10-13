using System.Windows.Controls;

namespace Motion.View
{
    /// <summary>
    /// 单轴页面：只负责布局，数据上下文由宿主（<c>Motion</c> 页签或调用方）给的
    /// <see cref="ViewModel.AxisMotionViewModel"/> 提供。
    /// </summary>
    public partial class AxisMotion : UserControl
    {
        public AxisMotion()
        {
            InitializeComponent();
        }
    }
}

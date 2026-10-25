using Motion.ViewModel;
using System.Windows;
using System.Windows.Controls;

namespace Motion.View
{
    /// <summary>
    /// 按轨迹段的类型选择直线 / 圆弧模板。
    /// </summary>
    public sealed class SegmentTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? LineTemplate { get; set; }

        public DataTemplate? ArcTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is TrajectorySegmentViewModel segment && segment.IsArc)
            {
                return ArcTemplate ?? LineTemplate;
            }

            return LineTemplate ?? ArcTemplate;
        }
    }
}

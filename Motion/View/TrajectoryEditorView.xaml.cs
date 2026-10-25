using Motion.ViewModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Motion.View
{
    /// <summary>
    /// 轨迹编辑器视图：画布点选 + 列表选中同步。
    /// </summary>
    public partial class TrajectoryEditorView : UserControl
    {
        public TrajectoryEditorView()
        {
            InitializeComponent();
        }

        public TrajectoryEditorView(TrajectoryEditorViewModel viewModel)
            : this()
        {
            DataContext = viewModel;
        }

        /// <summary>
        /// 点中某段：把它设为唯一选中项，列表与画布的高亮一起更新。
        /// </summary>
        private void Segment_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: TrajectorySegmentViewModel clicked })
            {
                return;
            }

            if (DataContext is TrajectoryEditorViewModel editor)
            {
                editor.SelectedSegment = clicked;

                // 列表的 SelectedItem 绑定会跟着更新，这里再滚到可视区
                SegmentsList.SelectedItem = clicked;
                SegmentsList.ScrollIntoView(clicked);
            }

            e.Handled = true;
        }
    }
}

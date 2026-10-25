using Motion.ViewModel;
using Prism.Ioc;
using Prism.Modularity;

namespace Motion
{
    /// <summary>
    /// 运动模块（Prism）：登记运动页的 ViewModel，并把页面挂进导航。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>本模块不注册 <c>IZmotionCard</c>：</b>卡地址与连接时机属于组合根（一台机器一张卡、只连一次），
    /// 由宿主在确认 endpoint 后注册 <c>ZmotionCardHost</c>。没注册时运动页照样打开，
    /// 只是状态显示“未连接”、下发命令禁用——不假装连上了卡。
    /// </para>
    /// </remarks>
    public class MotionModule : IModule
    {
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterSingleton<MotionViewModel>();

            // 轨迹编辑 / 加工参数：整页共用同一份配方与参数，重新生成时不会拿到旧快照
            containerRegistry.RegisterSingleton<DispenseProgram>();
            containerRegistry.RegisterSingleton<TrajectoryEditor>();
            containerRegistry.RegisterSingleton<MachiningParametersViewModel>();
            containerRegistry.RegisterSingleton<TrajectoryEditorViewModel>();

            containerRegistry.RegisterForNavigation<View.Motion, MotionViewModel>();
            containerRegistry.RegisterForNavigation<View.TrajectoryEditorView, TrajectoryEditorViewModel>();
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
        }
    }
}

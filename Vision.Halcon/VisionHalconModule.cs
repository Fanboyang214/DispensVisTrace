using Core.Vision;
using Prism.Ioc;
using Prism.Modularity;

namespace Vision.Halcon
{
    public class VisionHalconModule : IModule
    {
        public void OnInitialized(IContainerProvider containerProvider)
        {
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterSingleton<IVisionAlgorithm, HalconVisionAlgorithm>("Halcon");
            containerRegistry.RegisterSingleton<IInspectEngine, HalconInspectEngine>();
        }
    }
}
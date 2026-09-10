using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Core.Vision;
using Prism.Ioc;
using Prism.Modularity;

namespace Vision.Halcon
{
    public class VisionHalconModule : IModule
    {
        public void OnInitialized(IContainerProvider containerProvider)
        {
            throw new NotImplementedException();
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterSingleton<IVisionAlgorithm, HalconVisionAlgorithm>("Halcon");
        }
    }
}

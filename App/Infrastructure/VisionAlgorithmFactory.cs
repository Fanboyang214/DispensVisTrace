using Core.Vision;
using Prism.Ioc;
using System;
using System.Collections.Generic;

using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace App.Infrastructure
{
    public class VisionAlgorithmFactory : IVisionAlgorithmFactory
    {
        private readonly IContainerProvider _container;
        
        

        public VisionAlgorithmFactory(IContainerProvider container)
        {
            _container = container;
           
            
        }

        public IReadOnlyList<string> AvailableEngines => ["Halcon", "OpenCV"];
        public IVisionAlgorithm Create(string engineName)
        {
            try
            {

                return _container.Resolve<IVisionAlgorithm>(engineName);
            }
            catch (Exception ex)
            {
                {
                    throw new ArgumentException($"无法创建引擎 '{engineName}': {ex.Message}", ex);
                }
            }
        }
    }
}

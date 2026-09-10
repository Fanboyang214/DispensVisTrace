using Core.Models;
using Core.Vision;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Vision.Halcon
{
    public class HalconInspectEngine:IInspectEngine
    {   
        private  InspectConfig? _config;
        private bool _disposed;
        public bool Initialize(InspectConfig config)
        {
            if (_disposed) return false;
            if (_config == null) return false;
            _config = config;
            return true;
        }

        public void Inspect()
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            _disposed  = true;
        }

       


       
    }
}

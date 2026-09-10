using Core.Logging;
using Core.Models;
using Core.Vision;
using HalconDotNet;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Vision.Halcon
{
    public class HalconLocateEngine:ILocateEngine
    {
        private  LocateConfig? _config;
        private ILogService _logService;

        private bool _disposed;

        public HalconLocateEngine(IContainerProvider containerProvider)
        {
            _logService = containerProvider.Resolve<ILogService>();
        }
        public bool Initialize(LocateConfig config)
        {
            if (_disposed) return false;
            if (_config == null) return false;

                _config = config;
            return true;   
        }

        public ImageQualityResult CheckImageQuality(InspectionImage image)
        {
            GCHandle handle = GCHandle.Alloc(image.RawData, GCHandleType.Pinned);
            
            IntPtr ptr = handle.AddrOfPinnedObject();
                
            HImage hImage = new HImage("byte",image.ImageWidth,image.ImageHeight,ptr);

            HTuple meanTuple = null, deviationTuple=null;
            HObject roi = hImage.GetDomain();
            try
            {
               
                HOperatorSet.Intensity(roi,hImage, out meanTuple, out deviationTuple);

                double mean = meanTuple[0];
                double dev = deviationTuple[0];

                var score = mean * 0.5 + dev * 0.5;

                object minContrastObj = null;
                object maxContrastObj = null;
                object minBrightnessObj = null;
                object maxBrightnessObj = null;
                object scoreThresholdObj = null;
                bool hasMinContrast = _config?.ImageQualityGate.TryGetValue("minContrast", out minContrastObj) ?? false;
                bool hasMaxContrast = _config?.ImageQualityGate.TryGetValue("maxContrast", out maxContrastObj) ?? false;
                bool hasMinBrightness = _config?.ImageQualityGate.TryGetValue("minBrightness", out minBrightnessObj) ?? false;
                bool hasMaxBirghtness = _config?.ImageQualityGate.TryGetValue("maxBrightness", out maxBrightnessObj) ?? false;
                bool hasScoreThreshold = _config?.ImageQualityGate.TryGetValue("scoreThreshold", out scoreThresholdObj) ?? false;

                double minBrightness = hasMinBrightness ? Convert.ToDouble(minBrightnessObj) : 40.0;
                double maxBrightness = hasMinBrightness ? Convert.ToDouble(maxBrightnessObj) : 220.0;
                double minContrast = hasMinBrightness ? Convert.ToDouble(minContrastObj) : 30.0;
                double maxContrast = hasMinBrightness ? Convert.ToDouble(maxContrastObj) : 200.0;
                double scoreThreshold = hasMinBrightness ? Convert.ToDouble(scoreThresholdObj) : 0.6;


                return new ImageQualityResult
                {
                    IsPass = (minBrightness <= mean && mean <= maxBrightness) && (minContrast <= dev && dev <= maxContrast) ? (score >= scoreThreshold ? true : false) : false,
                    Score = score
                };
            
            }catch(Exception ex)
            {
                _logService.Log(LogLevel.Warn,$"定位图像质量检测异常：{ex.Message}");
            }
            finally
            {
                handle.Free();
                meanTuple?.Dispose();
                deviationTuple?.Dispose();
                roi?.Dispose();

            }
               
        }

        public ProductPose? FindProductPose(InspectionImage frame)
        {

        } 


        public Task LocateAsync(InspectionImage image)
        {

        }

        public void Dispose() => _disposed = true;
    }

   
}
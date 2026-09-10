using Core.Models;
using Core.Vision;
using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Vision.Halcon
{
    /// <summary>
    /// <see cref="IVisionAlgorithm"/> 的 Halcon 实现。
    /// 定位：图像加载 → 光线归一化 → 动态ROI提取 → 边缘增强 → 模板匹配 → 坐标转换 → 旋转补偿。
    /// 缺陷：图像加载 → 频域分析 → 边缘增强 → 缺陷检测 → 坐标转换。
    /// </summary>
    public class HalconVisionAlgorithm : IVisionAlgorithm
    {
        
        public HalconVisionAlgorithm()
        {
            
        }

        public string AlgorithmName => "Halcon";

        public bool IsInitiated { get; private set; }

        private CancellationTokenSource _ctsLocateInit;
        
        private CancellationTokenSource _ctsInspectInit;



        public HalconLocateEngine LocateEngine { get; set; }
        public HalconInspectEngine InspectEngine { get; set; }

        

        private HObject _templateModel;



        private bool _disposed;

        public async Task DetectLocateAsync(InspectionImage image, CancellationToken ct) 
        {
            await Task.Run(async() => await LocateEngine.LocateAsync(image),ct);
        }

        public async Task DetectInspectAsync(InspectionImage image, CancellationToken ct)
        {
            await Task.Run(() =>  LocateEngine.LocateAsync(image), ct);
        }



        public Task InitializeAsync(AlgorithmConfig config, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                
                bool isInitLocate = LocateEngine.Initialize(config.Locate);
                bool isInitInspect = InspectEngine.Initialize(config.Inspect);
                if (isInitLocate && isInitInspect)
                    IsInitiated = true;
                else
                    IsInitiated = false;
                return Task.CompletedTask;
            },ct);
        }
        
        public void Dispose()
        {
            _disposed = true;
        }

       
    }
}

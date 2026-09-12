using Core.Models;
using Core.Vision;
using HalconDotNet;
using Prism.Ioc;
using System;
using System.Threading;
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
        private readonly IContainerProvider _containerProvider;

        public HalconVisionAlgorithm(IContainerProvider containerProvider)
        {
            _containerProvider = containerProvider;
        }

        public string AlgorithmName => "Halcon";

        public bool IsInitiated { get; private set; }

        private CancellationTokenSource _ctsLocateInit;

        private CancellationTokenSource _ctsInspectInit;

        public HalconLocateEngine LocateEngine { get; private set; }

        public HalconInspectEngine InspectEngine { get; private set; }

        private HObject _templateModel;

        private bool _disposed;

        public async Task DetectLocateAsync(InspectionImage image, CancellationToken ct)
        {
            await Task.Run(async () => await LocateEngine.LocateAsync(image), ct);
        }

        public async Task DetectInspectAsync(InspectionImage image, CancellationToken ct)
        {
            await Task.Run(() => InspectEngine.Inspect(image), ct);
        }

        public Task InitializeAsync(AlgorithmConfig config, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                LocateEngine = new HalconLocateEngine(_containerProvider);
                InspectEngine = new HalconInspectEngine(_containerProvider);

                bool isInitLocate = LocateEngine.Initialize(config.Locate);
                bool isInitInspect = InspectEngine.Initialize(config.Inspect);
                if (isInitLocate && isInitInspect)
                    IsInitiated = true;
                else
                    IsInitiated = false;
            }, ct);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            LocateEngine?.Dispose();
            InspectEngine?.Dispose();
            _templateModel?.Dispose();
        }
    }
}
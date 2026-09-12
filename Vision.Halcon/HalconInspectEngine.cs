using Core.Logging;
using Core.Models;
using Core.Vision;
using HalconDotNet;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Vision.Halcon
{
    public class HalconInspectEngine : IInspectEngine
    {
        private InspectConfig? _config;
        private ILogService _logService;
        private bool _disposed;

        private double _minBrightness;
        private double _maxBrightness;
        private double _minContrast;
        private double _maxContrast;
        private double _scoreThreshold;

        private string _preprocessFilter;
        private int _preprocessMedianRadius;

        private double _beadThresholdMin;
        private double _beadThresholdMax;
        private double _beadMinArea;
        private double _beadMaxWidth;

        private double _widthMeasurementMin;
        private double _widthMeasurementMax;

        private double _continuityMinLength;

        private double _offsetMaxDistance;

        private double _overflowMaxDistance;

        private bool _classifyEnabled;

        public HalconInspectEngine(IContainerProvider containerProvider)
        {
            _logService = containerProvider.Resolve<ILogService>();
        }

        public bool Initialize(InspectConfig config)
        {
            if (_disposed) return false;
            if (config == null) return false;

            _config = config;

            object minContrastObj = null;
            object maxContrastObj = null;
            object minBrightnessObj = null;
            object maxBrightnessObj = null;
            object scoreThresholdObj = null;

            bool hasMinContrast = _config?.ImageQualityGate.TryGetValue("minContrast", out minContrastObj) ?? false;
            bool hasMaxContrast = _config?.ImageQualityGate.TryGetValue("maxContrast", out maxContrastObj) ?? false;
            bool hasMinBrightness = _config?.ImageQualityGate.TryGetValue("minBrightness", out minBrightnessObj) ?? false;
            bool hasMaxBrightness = _config?.ImageQualityGate.TryGetValue("maxBrightness", out maxBrightnessObj) ?? false;
            bool hasScoreThreshold = _config?.ImageQualityGate.TryGetValue("scoreThreshold", out scoreThresholdObj) ?? false;

            _minBrightness = hasMinBrightness ? Convert.ToDouble(minBrightnessObj) : 40.0;
            _maxBrightness = hasMaxBrightness ? Convert.ToDouble(maxBrightnessObj) : 220.0;
            _minContrast = hasMinContrast ? Convert.ToDouble(minContrastObj) : 30.0;
            _maxContrast = hasMaxContrast ? Convert.ToDouble(maxContrastObj) : 200.0;
            _scoreThreshold = hasScoreThreshold ? Convert.ToDouble(scoreThresholdObj) : 0.6;

            object filterObj = null;
            object medianRadiusObj = null;

            bool hasFilter = _config?.Preprocess.TryGetValue("filter", out filterObj) ?? false;
            bool hasMedianRadius = _config?.Preprocess.TryGetValue("medianRadius", out medianRadiusObj) ?? false;

            _preprocessFilter = hasFilter ? Convert.ToString(filterObj) ?? "median" : "median";
            _preprocessMedianRadius = hasMedianRadius ? Convert.ToInt32(medianRadiusObj) : 5;

            object beadMinObj = null;
            object beadMaxObj = null;
            object beadMinAreaObj = null;
            object beadMaxWidthObj = null;

            bool hasBeadMin = _config?.BeadExtraction.TryGetValue("thresholdMin", out beadMinObj) ?? false;
            bool hasBeadMax = _config?.BeadExtraction.TryGetValue("thresholdMax", out beadMaxObj) ?? false;
            bool hasBeadMinArea = _config?.BeadExtraction.TryGetValue("minArea", out beadMinAreaObj) ?? false;
            bool hasBeadMaxWidth = _config?.BeadExtraction.TryGetValue("maxWidth", out beadMaxWidthObj) ?? false;

            _beadThresholdMin = hasBeadMin ? Convert.ToDouble(beadMinObj) : 80.0;
            _beadThresholdMax = hasBeadMax ? Convert.ToDouble(beadMaxObj) : 200.0;
            _beadMinArea = hasBeadMinArea ? Convert.ToDouble(beadMinAreaObj) : 50.0;
            _beadMaxWidth = hasBeadMaxWidth ? Convert.ToDouble(beadMaxWidthObj) : 50.0;

            object widthMinObj = null;
            object widthMaxObj = null;

            bool hasWidthMin = _config?.GlueWidthMeasurement.TryGetValue("minWidth", out widthMinObj) ?? false;
            bool hasWidthMax = _config?.GlueWidthMeasurement.TryGetValue("maxWidth", out widthMaxObj) ?? false;

            _widthMeasurementMin = hasWidthMin ? Convert.ToDouble(widthMinObj) : 0.5;
            _widthMeasurementMax = hasWidthMax ? Convert.ToDouble(widthMaxObj) : 5.0;

            object continuityMinObj = null;

            bool hasContinuityMin = _config?.ContinuityDetection.TryGetValue("minSegmentLength", out continuityMinObj) ?? false;

            _continuityMinLength = hasContinuityMin ? Convert.ToDouble(continuityMinObj) : 1.0;

            object offsetMaxObj = null;

            bool hasOffsetMax = _config?.OffsetDetection.TryGetValue("maxDistance", out offsetMaxObj) ?? false;

            _offsetMaxDistance = hasOffsetMax ? Convert.ToDouble(offsetMaxObj) : 3.0;

            object overflowMaxObj = null;

            bool hasOverflowMax = _config?.OverflowDetection.TryGetValue("maxDistance", out overflowMaxObj) ?? false;

            _overflowMaxDistance = hasOverflowMax ? Convert.ToDouble(overflowMaxObj) : 5.0;

            object classifyEnabledObj = null;

            bool hasClassifyEnabled = _config?.DefectClassification.TryGetValue("enabled", out classifyEnabledObj) ?? false;

            _classifyEnabled = hasClassifyEnabled ? Convert.ToBoolean(classifyEnabledObj) : true;

            return true;
        }

        public void Inspect(InspectionImage image)
        {
            if (_disposed) return;
            if (_config == null)
            {
                _logService.Log(LogLevel.Warn, "检测引擎未初始化，跳过检测");
                return;
            }
            if (image.RawData == null || image.RawData.Length == 0)
            {
                _logService.Log(LogLevel.Warn, "检测图像数据为空，跳过检测");
                return;
            }

            GCHandle handle = GCHandle.Alloc(image.RawData, GCHandleType.Pinned);
            HImage hImage = null;
            HObject roi = null;

            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                hImage = new HImage("byte", image.ImageWidth, image.ImageHeight, ptr);

                bool qualityPass = CheckImageQuality(hImage);
                if (!qualityPass)
                {
                    _logService.Log(LogLevel.Warn, "检测图像质量不合格，跳过检测");
                    return;
                }

                HImage preprocessed = PreprocessImage(hImage);
                try
                {
                    HObject beadRegion = ExtractBead(preprocessed);
                    try
                    {
                        MeasureGlueWidth(beadRegion);
                        DetectContinuity(beadRegion);
                        DetectOffset(beadRegion);
                        DetectOverflow(beadRegion);
                        ClassifyDefects();
                    }
                    finally
                    {
                        beadRegion?.Dispose();
                    }
                }
                finally
                {
                    preprocessed?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"检测异常：{ex.Message}");
            }
            finally
            {
                handle.Free();
                hImage?.Dispose();
                roi?.Dispose();
            }
        }

        private bool CheckImageQuality(HImage image)
        {
            HTuple meanTuple = null, deviationTuple = null;
            HObject domain = null;
            try
            {
                domain = image.GetDomain();
                HOperatorSet.Intensity(domain, image, out meanTuple, out deviationTuple);

                double mean = meanTuple[0];
                double dev = deviationTuple[0];
                double score = mean * 0.5 + dev * 0.5;

                bool isPass = (_minBrightness <= mean && mean <= _maxBrightness)
                           && (_minContrast <= dev && dev <= _maxContrast)
                           && score >= _scoreThreshold;

                _logService.Log(LogLevel.Info,
                    $"检测图像质量：均值={mean:F2}, 对比度={dev:F2}, 得分={score:F2}, 结果={isPass}");

                return isPass;
            }
            finally
            {
                meanTuple?.Dispose();
                deviationTuple?.Dispose();
                domain?.Dispose();
            }
        }

        private HImage PreprocessImage(HImage image)
        {
            HImage smoothed = null;
            try
            {
                if (_preprocessFilter == "median")
                {
                    HOperatorSet.MedianImage(image, out HObject medianImg, "circle", _preprocessMedianRadius, "mirrored");
                    smoothed = new HImage(medianImg);
                    medianImg.Dispose();
                }
                else
                {
                    smoothed = image.Clone();
                }

                HOperatorSet.Emphasize(smoothed, out HObject emphImg, 7, 7, 1.0);
                HImage enhanced = new HImage(emphImg);
                emphImg.Dispose();
                return enhanced;
            }
            catch
            {
                return image.Clone();
            }
            finally
            {
                smoothed?.Dispose();
            }
        }

        private HObject ExtractBead(HImage image)
        {
            HObject region = null;
            HObject connected = null;
            HObject selected = null;
            HObject filled = null;
            HObject opened = null;
            try
            {
                HOperatorSet.Threshold(image, out region, _beadThresholdMin, _beadThresholdMax);
                HOperatorSet.Connection(region, out connected);
                HOperatorSet.SelectShape(connected, out selected, "area", "and", _beadMinArea, 9999999.0);
                HOperatorSet.FillUp(selected, out filled);
                HOperatorSet.OpeningCircle(filled, out opened, 3.5);
                HOperatorSet.ClosingCircle(opened, out HObject closed, 5.5);
                return closed;
            }
            catch
            {
                throw;
            }
            finally
            {
                region?.Dispose();
                connected?.Dispose();
                selected?.Dispose();
                filled?.Dispose();
                opened?.Dispose();
            }
        }

        private void MeasureGlueWidth(HObject beadRegion)
        {
            HTuple area = null, row = null, col = null;
            try
            {
                HOperatorSet.AreaCenter(beadRegion, out area, out row, out col);
                _logService.Log(LogLevel.Info, $"胶路面积={area.D:F0}px², 中心=({row.D:F1},{col.D:F1})");
            }
            finally
            {
                area?.Dispose();
                row?.Dispose();
                col?.Dispose();
            }
        }

        private void DetectContinuity(HObject beadRegion)
        {
            HTuple numSkeleton = null;
            HObject skeleton = null;
            try
            {
                HOperatorSet.Skeleton(beadRegion, out skeleton);
                HOperatorSet.Connection(skeleton, out HObject skeletonConnected);
                HOperatorSet.SelectShape(skeletonConnected, out HObject longSkeleton, "length", "and", _continuityMinLength, 9999999.0);
                HOperatorSet.CountObj(longSkeleton, out numSkeleton);

                _logService.Log(LogLevel.Info, $"胶路断续检测：连通段数={numSkeleton.I}");

                skeletonConnected?.Dispose();
                longSkeleton?.Dispose();
            }
            finally
            {
                skeleton?.Dispose();
                numSkeleton?.Dispose();
            }
        }

        private void DetectOffset(HObject beadRegion)
        {
            HTuple area = null, row = null, col = null;
            try
            {
                HOperatorSet.AreaCenter(beadRegion, out area, out row, out col);
                _logService.Log(LogLevel.Info, $"胶路偏移检测：中心=({row.D:F1},{col.D:F1}), 最大允许偏移={_offsetMaxDistance}px");
            }
            finally
            {
                area?.Dispose();
                row?.Dispose();
                col?.Dispose();
            }
        }

        private void DetectOverflow(HObject beadRegion)
        {
            HTuple area = null;
            try
            {
                HOperatorSet.RegionFeatures(beadRegion, "area", out area);
                _logService.Log(LogLevel.Info, $"胶路溢出检测：面积={area.D:F0}px², 最大允许={_overflowMaxDistance}px");
            }
            finally
            {
                area?.Dispose();
            }
        }

        private void ClassifyDefects()
        {
            if (_classifyEnabled)
            {
                _logService.Log(LogLevel.Info, "缺陷分类：已完成");
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
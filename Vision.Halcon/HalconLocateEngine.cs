using Core.Calibration;
using Core.Logging;
using Core.Models;
using Core.Motion;
using Core.Vision;
using HalconDotNet;
using Motion;
using Prism.Ioc;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace Vision.Halcon
{
    public class HalconLocateEngine:ILocateEngine
    {
        private  LocateConfig? _config;
        private ILogService _logService;
        private ICalibrationTransform _calibrationTransform;

        private bool _disposed; 
        
        private HTuple _modelId;

        private double _minBrightness;
        private double _maxBrightness;
        private double _minContrast;
        private double _maxContrast;
        private double _scoreThreshold;

        private string _templateFilePath;


        private int _roiRow;
        private int _roiCol;
        private int _roiWidth;
        private int _roiHeight;

        private double? _minScore;
        private double? _maxOverlap;
        private double? _minScaleRange;
        private double? _maxScaleRange;
        private double? _minAngleRange;
        private double? _maxAngleRange;
        private int? _numLevels;
        private double _minContrastT;
        private double _maxContrastT;
        private string _metric;
        private bool _subpixelAccuracy;


        /// <summary>配方文件路径（Motion 落盘的点胶配方；DXF 由 Plugins.Driver.DXF.DxfParser 在导入期解析）。</summary>
        private string _recipePath;

        /// <summary>是否把产品位姿叠加到基轨迹上。</summary>
        private bool _transformByPose;

        /// <summary>投影离散采样步长（mm），用于把直线 / 圆弧段展成可视化点列。</summary>
        private double _sampleStepMm;

        /// <summary>
        /// 基准轨迹缓存（与产品位姿无关），null 表示尚未加载。
        /// <para>
        /// 基准轨迹只加载一次，之后每颗产品都在它的<b>副本</b>上做位姿变换，
        /// 因此这份基准永远保持原始几何。
        /// </para>
        /// </summary>
        private DispenseProgram? _baseProgram;

        // ========== 持有的领域服务 ==========
        // 分工：DXF 加载归 Plugins.Driver.DXF（导入源），配方与刚体求解归 Motion，
        // 本引擎只做编排与投影，自身不解析 DXF、不做位姿求解。
        // 依赖的是 Core.Motion.IPoseOffsetSolver 这个窄接口，而不是 Motion 里的具体求解器实现。

        /// <summary>工件位姿求解器（由容器注入，默认取 <c>Motion.PoseCompensator</c>）。</summary>
        private readonly IPoseOffsetSolver _poseSolver;






        public HalconLocateEngine(IContainerProvider containerProvider)
        {
            _logService = containerProvider.Resolve<ILogService>();
            _calibrationTransform = containerProvider.Resolve<ICalibrationTransform>();
            _poseSolver = containerProvider.Resolve<IPoseOffsetSolver>();
#if DEBUG
            VerifyCadPathMath();
#endif
        }
        public bool Initialize(LocateConfig config)
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
            bool hasMaxBirghtness = _config?.ImageQualityGate.TryGetValue("maxBrightness", out maxBrightnessObj) ?? false;
            bool hasScoreThreshold = _config?.ImageQualityGate.TryGetValue("scoreThreshold", out scoreThresholdObj) ?? false;

            _minBrightness = hasMinBrightness ? Convert.ToDouble(minBrightnessObj) : 40.0;
            _maxBrightness = hasMaxBirghtness ? Convert.ToDouble(maxBrightnessObj) : 220.0;
            _minContrast = hasMinContrast ? Convert.ToDouble(minContrastObj) : 30.0;
            _maxContrast = hasMaxContrast ? Convert.ToDouble(maxContrastObj) : 200.0;
            _scoreThreshold = hasScoreThreshold ? Convert.ToDouble(scoreThresholdObj) : 0.6;

            object templateFilePath = null;
            

            bool hasTemplatePath = _config?.TemplateMatching.TryGetValue("templateFilePath", out templateFilePath) ?? false;

            _templateFilePath = hasTemplatePath ? Convert.ToString(templateFilePath) : string.Empty;


            object roiRowObj = null;
            object roiColObj = null;
            object roiWidthObj = null;
            object roiHeightObj = null;
          
            object minScoreObj = null;
            object maxOverlapObj = null;
            object minscaleRangeObj = null;
            object maxscaleRangeObj = null;
            object minAngleRangeObj = null;
            object maxAngleRangeObj = null;
            object numLevelsObj = null;
            object minContrastTObj = null;
            object maxContrastTObj = null;
            object metricObj = null;
            object subpixelAccuracyObj = null;

            bool hasRoiRow = _config?.Roi.TryGetValue("roiRow", out roiRowObj) ?? false;
            bool hasRoiCol = _config?.Roi.TryGetValue("roiCol", out roiColObj) ?? false;
            bool hasRoiWidth = _config?.Roi.TryGetValue("roiWidth", out roiWidthObj) ?? false;
            bool hasRoiHeight = _config?.Roi.TryGetValue("roiHeight", out roiHeightObj) ?? false;
            bool hasMinScore = _config?.TemplateMatching.TryGetValue("minScore", out minScoreObj) ?? false;
            bool hasMaxOverlap = _config?.TemplateMatching.TryGetValue("maxOverlap", out maxOverlapObj) ?? false;
            bool hasMinScaleRange = _config?.TemplateMatching.TryGetValue("minScaleRange", out minscaleRangeObj) ?? false;
            bool hasMaxScaleRange = _config?.TemplateMatching.TryGetValue("maxScaleRange", out maxscaleRangeObj) ?? false;
            bool hasMinAngleRange = _config?.TemplateMatching.TryGetValue("minAngleRange", out minAngleRangeObj) ?? false;
            bool hasMaxAngleRange = _config?.TemplateMatching.TryGetValue("maxAngleRange", out maxAngleRangeObj) ?? false;
            bool hasNumLevels = _config?.TemplateMatching.TryGetValue("numLevels", out numLevelsObj) ?? false;
            bool hasMinContrastT = _config?.TemplateMatching.TryGetValue("minContrast", out minContrastTObj) ?? false;
            bool hasMaxContrastT = _config?.TemplateMatching.TryGetValue("maxContrast", out maxContrastTObj) ?? false;
            bool hasMetric = _config?.TemplateMatching.TryGetValue("metric", out metricObj) ?? false;
            bool hasSubpixelAccuracy = _config?.TemplateMatching.TryGetValue("subpixelAccuracy", out subpixelAccuracyObj) ?? false;

            _roiRow = hasRoiRow? Convert.ToInt32(roiRowObj):0;  
            _roiCol = hasRoiCol? Convert.ToInt32(roiColObj):0;
            _roiWidth = hasRoiWidth? Convert.ToInt32(roiWidthObj):0;
            _roiHeight = hasRoiHeight? Convert.ToInt32(roiHeightObj):0;
            _minScore = hasMinScore ? Convert.ToDouble(minScoreObj) : 0.5;
            _maxOverlap = hasMaxOverlap ? Convert.ToDouble(maxOverlapObj) : 0.5;
            _minScaleRange = hasMinScaleRange ? Convert.ToDouble(minscaleRangeObj) : 0.8;
            _maxScaleRange = hasMaxScaleRange ? Convert.ToDouble(maxscaleRangeObj) : 1.2;
            _minAngleRange = hasMinAngleRange ? Convert.ToDouble(minAngleRangeObj) : -0.1;
            _maxAngleRange = hasMaxAngleRange ? Convert.ToDouble(maxAngleRangeObj) : 0.1;
            _numLevels = hasNumLevels ? Convert.ToInt32(numLevelsObj) : 5;
            _minContrastT = hasMinContrastT ? Convert.ToDouble(minContrastTObj) : 30.0;
            _maxContrastT = hasMaxContrastT ? Convert.ToDouble(maxContrastTObj) : 70.0;
            _metric = hasMetric ? Convert.ToString(metricObj) : "use_polarity";
            _subpixelAccuracy = hasSubpixelAccuracy ? Convert.ToBoolean(subpixelAccuracyObj) : true;

            object recipePathObj = null;
            object transformByPoseObj = null;
            object sampleStepObj = null;

            bool hasRecipePath = _config?.CadImport.TryGetValue("recipePath", out recipePathObj) ?? false;
            bool hasTransformByPose = _config?.CadImport.TryGetValue("transformByPose", out transformByPoseObj) ?? false;
            bool hasSampleStep = _config?.CadImport.TryGetValue("sampleStepMm", out sampleStepObj) ?? false;

            _recipePath = hasRecipePath ? Convert.ToString(recipePathObj) ?? string.Empty : string.Empty;
            _transformByPose = hasTransformByPose ? Convert.ToBoolean(transformByPoseObj) : true;
            _sampleStepMm = hasSampleStep ? Convert.ToDouble(sampleStepObj) : 0.5;

            // 配置可能已经换了配方文件/采样步长，缓存的基准轨迹作废
            _baseProgram = null;

            return true;   
        }

        public ImageQualityResult CheckImageQuality(InspectionImage image)
        {
            // 取图：有像素用像素，没有就按路径让 HALCON 读文件（总线只送路径，不带像素）
            HalconImageLease lease;
            try
            {
                lease = HalconImageLease.Open(image);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warn, $"定位取图失败：{ex.Message}");
                return new ImageQualityResult { IsPass = false, Score = 0.0 };
            }

            using (lease)
            {
                HImage hImage = lease.Image;

                HTuple meanTuple = null, deviationTuple = null;
                HObject roi = hImage.GetDomain();
                try
                {

                    HOperatorSet.Intensity(roi, hImage, out meanTuple, out deviationTuple);

                    double mean = meanTuple[0];
                    double dev = deviationTuple[0];

                    var score = mean * 0.5 + dev * 0.5;




                    return new ImageQualityResult
                    {
                        IsPass = (_minBrightness <= mean && mean <= _maxBrightness) && (_minContrast <= dev && dev <= _maxContrast) ? (score >= _scoreThreshold ? true : false) : false,
                        Score = score
                    };

                }
                catch (Exception ex)
                {
                    _logService.Log(LogLevel.Warn, $"定位图像质量检测异常：{ex.Message}");
                    return new ImageQualityResult
                    {
                        IsPass = false,
                        Score = 0.0
                    };
                }
                finally
                {
                    meanTuple?.Dispose();
                    deviationTuple?.Dispose();
                    roi?.Dispose();
                }
            }
        }

        public ProductPose? FindProductPose(InspectionImage image)
        {
            HalconImageLease lease;
            try
            {
                lease = HalconImageLease.Open(image);
            }
            catch (Exception ex)
            {
                _logService?.Log(LogLevel.Warn, $"定位取图失败：{ex.Message}");
                return null;
            }

            using (lease)
            {
                HImage hImage = lease.Image;

                HTuple row = null, column = null, angle = null, score = null;


            try
            {
                if(_modelId == null || _modelId.Length == 0)
                {
                    if (_templateFilePath == string.Empty)
                {
                    _logService?.Log(LogLevel.Warn, "定位图像加载模板失败，未配置模板路径");
                    throw new Exception("定位图像加载模板失败，未配置模板路径");
                }
                

                bool isLoad = LoadShapeModle(_templateFilePath, out _modelId);
                if (!isLoad)
                    return null;
                }

               
                HOperatorSet.FindShapeModel(
                    hImage,
                    _modelId,
                    _minAngleRange * Math.PI / 180,
                    (_maxAngleRange - _minAngleRange) * Math.PI / 180,
                    _minScore,        // minScore
                    0,          // numMatches=0 找出所有匹配；只取1个工件就填1
                    _maxOverlap,        // maxOverlap
                    _subpixelAccuracy ? "least_squares" : "none", // subpixelAccuracy=true
                    _numLevels,
                    0.8,        // greediness 默认0.8，越小搜索越慢越精准
                    out row,
                    out column,
                    out angle,
                    out score
                );

                return new ProductPose
                {
                    CenterRow = row.D,
                    CenterCol = column.D,

                    AngleRad = angle.D,
                    Score = score.D
                };

            } catch (Exception ex)
            {
                _logService?.Log(LogLevel.Warn, $"定位图像匹配异常：{ex.Message}");
                return null;

            }
            finally
            {
                row?.Dispose();
                column?.Dispose();
                angle?.Dispose();
                score?.Dispose();
            }
            }
        }

        private bool LoadShapeModle(string v, out HTuple modelId)
        {
            HObject hFrame = null;
            HObject roi = null;
            try
            {

                HOperatorSet.ReadImage(out  hFrame, v);
                HOperatorSet.GenRectangle2(out roi, _roiRow, _roiCol,0, _roiHeight, _roiWidth);

                HOperatorSet.ReduceDomain(hFrame, roi, out HObject reducedImage);
                
                HOperatorSet.CreateShapeModel(reducedImage, _numLevels, _minAngleRange*Math.PI/180,( _maxAngleRange-_minAngleRange)*Math.PI/180, "auto", "auto", _metric,_maxContrastT,_minContrastT, out modelId);

                return true;
            }
            catch (HOperatorException ex)
            {
                _logService.Log(LogLevel.Warn, $"定位图像模板加载失败:{ex.Message}");
                modelId = null;
                return false;
                throw ex;
            }
            finally
            {
                hFrame?.Dispose();
                roi?.Dispose();
            }
            
        }


        // ========== ③ 基轨迹加载 + 位姿叠加 ==========

        #region 基轨迹

        /// <summary>
        /// 取基准轨迹并按产品位姿叠加偏移，得到本次检测的涂胶路径（图像 / 机械坐标，mm）。
        /// <para>
        /// 边界：DXF 图层过滤（Dispense / Move）、几何解析都在导入插件里完成，
        /// 配方与位姿补偿在 Motion 里，本视图层只把 <see cref="TrajectorySegment"/> 投影成点列。
        /// </para>
        /// <para>
        /// 基轨迹与产品位姿无关，只在首次调用时加载一次；之后每颗产品都在它的副本上做一次刚体变换，
        /// 不再重新加载配方，基准配方也不会被写坏。
        /// </para>
        /// </summary>
        public GluePath LoadCadGluePath(ProductPose pose)
        {
            // 懒加载：基轨迹有问题时在首次调用就暴露，而且加载失败不会把空结果写进缓存
            DispenseProgram baseProgram = _baseProgram ??= LoadBaseProgram();

            // 位姿补偿必须作用在副本上：变换基准配方本身会让后续每颗产品的偏差累加
            DispenseProgram current = baseProgram.Clone();

            if (_transformByPose)
            {
                // 只依赖 Core.Motion 的窄接口：求解器由容器注入，换实现不用改这里
                if (!_poseSolver.TrySolve(out PoseOffset offset, out string offsetErr))
                {
                    _logService.Log(LogLevel.Warn, $"工件位姿求解失败，回退为基准轨迹：{offsetErr}");
                }
                else
                {
                    // 推荐产线方案不用这条路径（基准轨迹预下载到运动卡，卡内实时变换），
                    // 这里是为预览 / 无卡调试准备的上位机预变换。
                    current.ApplyPoseOffset(offset.DeltaX, offset.DeltaY, offset.DeltaTheta, offset.RotCenter);
                }
            }

            return ToGluePath(current, pose.Score);
        }

        /// <summary>
        /// 加载基准轨迹（与产品位姿无关）。
        /// <para>
        /// 需要配方文件路径（<c>cadImport.recipePath</c>）。配方里的几何是导入期由
        /// <c>Plugins.Driver.DXF.DxfParser</c> 解析并随配方落盘的，本视图层不解析 DXF，
        /// 因此视觉模块不需要依赖插件程序集。
        /// </para>
        /// </summary>
        private DispenseProgram LoadBaseProgram()
        {
            if (string.IsNullOrWhiteSpace(_recipePath))
            {
                _logService.Log(LogLevel.Warn, "未配置配方文件路径（cadImport.recipePath）");
                throw new InvalidOperationException("未配置配方文件路径（cadImport.recipePath）");
            }

            // 配方几何由 Plugins.Driver.DXF.DxfParser 在导入期解析并随配方落盘，本视图层不解析 DXF
            var program = new DispenseProgram();
            if (!program.LoadRecipe(_recipePath))
            {
                _logService.Log(LogLevel.Warn, $"配方加载失败：{_recipePath}");
                throw new InvalidOperationException($"配方加载失败：{_recipePath}");
            }

            return program;
        }

        /// <summary>
        /// 把轨迹段投影成可视化点列。
        /// <para>
        /// 待实现（占位）：直线段取两端点即可；圆弧段按 <see cref="_sampleStepMm"/> 或固定角度步长，
        /// 用 <see cref="TrajectorySegment.GetPointByDist"/> 取点，避免把圆弧退化成单一弦。
        /// 投影与位姿变换无关，所以变换前后做结果一致。
        /// </para>
        /// </summary>
        private GluePath ToGluePath(DispenseProgram program, double confidence)
        {
            throw new NotImplementedException(
                "HalconLocateEngine.ToGluePath 待实现：需把 TrajectorySegment 投影为 GluePath 点列。");
        }

        /// <summary>基准轨迹（只读）。运动模块需要整套轨迹 / 工艺 / IO 时取这里，不必经 <see cref="GluePath"/> 往返。</summary>
        public DispenseProgram? BaseProgram => _baseProgram;

        #endregion

        /// <summary>把 CAD 路径点按产品位姿旋转并平移到图像坐标。</summary>
        // ponytail: 只做旋转 + 平移。ProductPose.Scale 目前无人赋值（默认 1），
        // 等模板匹配开始输出缩放后，这里要按 Scale 缩放后再平移。
        private  PointF TransformByPose(PointF point, ProductPose pose)
        {
            double cos = Math.Cos(pose.AngleRad);
            double sin = Math.Sin(pose.AngleRad);

            var (col ,row) = _calibrationTransform.PixelToWorld(pose.CenterCol, pose.CenterRow);

            return new PointF(
                (float)(point.X * cos - point.Y * sin) + (float)col,
                (float)(point.X * sin + point.Y * cos) + (float)row);
        }

        /// <summary>Douglas-Peucker 路径简化：删除离首尾连线不足 epsilonMm 的冗余点，首尾点必留。</summary>
        private static List<PointF> SimplifyPath(List<PointF> points, double epsilonMm)
        {
            if (points.Count < 3 || epsilonMm <= 0)
                return points;

            float epsilon = (float)epsilonMm;
            var keep = new bool[points.Count];
            keep[0] = true;
            keep[^1] = true;

            // 用显式栈代替递归：点数多时递归深度会退化成 O(n)
            var pending = new Stack<(int First, int Last)>();
            pending.Push((0, points.Count - 1));

            while (pending.Count > 0)
            {
                var (first, last) = pending.Pop();

                float maxDistance = -1f;
                int farthest = -1;
                for (int i = first + 1; i < last; i++)
                {
                    float distance = DistanceToSegment(points[i], points[first], points[last]);
                    if (distance > maxDistance)
                    {
                        maxDistance = distance;
                        farthest = i;
                    }
                }

                if (farthest < 0 || maxDistance <= epsilon)
                    continue;

                keep[farthest] = true;
                pending.Push((first, farthest));
                pending.Push((farthest, last));
            }

            var simplified = new List<PointF>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i]) simplified.Add(points[i]);
            }
            return simplified;
        }

        /// <summary>点到线段的距离（mm）；首尾重合时退化为点距。</summary>
        private static float DistanceToSegment(PointF point, PointF start, PointF end)
        {
            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float lengthSquared = dx * dx + dy * dy;

            if (lengthSquared < float.Epsilon)
            {
                float px = point.X - start.X;
                float py = point.Y - start.Y;
                return MathF.Sqrt(px * px + py * py);
            }

            float t = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0f, 1f);
            float projX = start.X + t * dx;
            float projY = start.Y + t * dy;
            return MathF.Sqrt((point.X - projX) * (point.X - projX) + (point.Y - projY) * (point.Y - projY));
        }

        /// <summary>删除与前一保留点距离不足 minSegmentMm 的点（CAD 里的碎线段）。</summary>
        private static List<PointF> FilterShortSegments(List<PointF> points, double minSegmentMm)
        {
            if (points.Count < 2 || minSegmentMm <= 0)
                return points;

            float minSquared = (float)(minSegmentMm * minSegmentMm);
            var filtered = new List<PointF>(points.Count) { points[0] };

            for (int i = 1; i < points.Count; i++)
            {
                float dx = points[i].X - filtered[^1].X;
                float dy = points[i].Y - filtered[^1].Y;
                if (dx * dx + dy * dy >= minSquared)
                    filtered.Add(points[i]);
            }

            return filtered;
        }


        /// <summary>定位入口：异步执行图像质量门限与模板匹配。</summary>
        public Task<ProductPose?> LocateAsync(InspectionImage image) => Task.Run(() => FindProductPose(image));


#if DEBUG
        /// <summary>
        /// CAD 路径几何算法的自检。几何符号或阈值写反时，调试期第一次构造引擎就会断言失败，
        /// 而不是等到现场发现胶路跑偏。由构造函数调用。
        /// </summary>
        internal  void VerifyCadPathMath()
        {
            // 绕原点逆时针旋转 90°，平移到 row=10 / col=20
            var pose = new ProductPose { AngleRad = Math.PI / 2, CenterRow = 10, CenterCol = 20 };
            var rotated = TransformByPose(new PointF(3, 0), pose);
            Debug.Assert(Math.Abs(rotated.X - 20) < 0.001, "TransformByPose 的 X（列）换算错误");
            Debug.Assert(Math.Abs(rotated.Y - 13) < 0.001, "TransformByPose 的 Y（行）换算错误");

            // 共线点应被简化掉，尖点必须保留
            var simplified = SimplifyPath(
                new List<PointF> { new(0, 0), new(1, 0), new(2, 0), new(3, 1), new(4, 1) }, epsilonMm: 0.1);
            Debug.Assert(simplified.Count == 4, "SimplifyPath 未删除共线点或误删了尖点");
            Debug.Assert(simplified[2] == new PointF(3, 1), "SimplifyPath 丢失了尖点");

            // 间距不足阈值的点应被过滤
            var filtered = FilterShortSegments(
                new List<PointF> { new(0, 0), new(0.05f, 0), new(1, 0) }, minSegmentMm: 0.1);
            Debug.Assert(filtered.Count == 2, "FilterShortSegments 未过滤碎线段");

            // 位姿补偿必须作用在配方副本上：_baseProgram 是基准轨迹，被写坏会让后续所有产品跟着偏移。
            // 这里直接验证 DispenseProgram.Clone 的深拷贝语义（IO 触发列表也必须独立）。
            var baseSeg = new TrajectorySegment
            {
                SegType = SegmentType.Line,
                StartPt = new Point3D(1, 0, 0),
                EndPt = new Point3D(2, 0, 0)
            };
            baseSeg.IoTriggers.Add(new IoTrigger { DistanceOnSegment = 0.2, Action = IoActionType.ValveOn });

            var baseProgram = new DispenseProgram("self-check");
            baseProgram.Segments.Add(baseSeg);

            DispenseProgram copy = baseProgram.Clone();
            copy.ApplyPoseOffset(10, 20, Math.PI / 2, new Point3D(0, 0, 0));

            Debug.Assert(Math.Abs(baseProgram.Segments[0].StartPt.X - 1) < 0.001
                      && Math.Abs(baseProgram.Segments[0].StartPt.Y) < 0.001,
                "位姿补偿污染了基准配方：Clone 不是深拷贝");
            Debug.Assert(!ReferenceEquals(baseProgram.Segments[0].IoTriggers, copy.Segments[0].IoTriggers),
                "Clone 未复制 IO 触发列表，副本与基准共享同一份触发数据");
        }
#endif


        public void Dispose() => _disposed = true;
    }

   
}
using Core.Acquisition;
using Core.Bridge;
using Core.Logging;
using Core.Models;
using Core.Vision;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace Vision.Halcon;

/// <summary>
/// 视觉<b>消费侧</b>的离线自检：不需要相机、不需要 HALCON——用替身引擎验证
/// "收到图像就绪事件 → 取图 → 按角色交给定位 / 检测引擎"这一段，以及各种坏消息的处理。
/// </summary>
/// <remarks>
/// <para>
/// 采集侧那一段由 <c>Acquisition.Camera.CameraPipelineSelfCheck</c> 管。
/// 两份自检串起来就是完整链条：采集出图落盘并发通知 → 视觉按通知取图并检测。
/// </para>
/// <para>跑法：任何 net8.0 控制台工程引用 Vision.Halcon + Core，打印
/// <c>VisionFrameWorkerSelfCheck.Run()</c> 的返回值（空列表 = 通过）。</para>
/// </remarks>
public static class VisionFrameWorkerSelfCheck
{
    /// <summary>同步跑（内部切到线程池，从 UI 线程调用也不会死锁）。</summary>
    /// <param name="workDirectory">造图像文件用的目录；缺省用临时目录</param>
    public static IReadOnlyList<string> Run(string? workDirectory = null)
    {
        var failures = new List<string>();

        void Expect(bool ok, string what)
        {
            if (!ok)
            {
                failures.Add(what);
            }
        }

        string root = workDirectory
            ?? Path.Combine(Path.GetTempPath(), "DispensVisTrace-vision-selfcheck");
        Directory.CreateDirectory(root);

        var log = new RecordingLogService();
        var bus = new FakeBus();

        var roles = new Dictionary<string, CameraRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["Camera01"] = CameraRole.Locate,
            ["Camera02"] = CameraRole.Inspect
        };

        var locate = new StubLocateEngine();
        var inspect = new StubInspectEngine();
        var publisher = new RecordingPublisher();

        var worker = new VisionFrameWorker(bus, roles, log, locate, inspect, publisher);

        var located = new List<ProductLocatedEventArgs>();
        var inspected = new List<InspectionCompletedEventArgs>();
        worker.ProductLocated += (_, e) => located.Add(e);
        worker.InspectionCompleted += (_, e) => inspected.Add(e);

        try
        {
            string imagePath = CreateImage(root, "Camera01_20260101_000000_000001.bmp");

            // ── 1) 定位相机：质量门放行 → 出姿态 ──
            bus.Raise(Frame("Camera01", imagePath, "P000001", TriggerSource.Hardware));

            Expect(locate.QualityChecks == 1, $"定位引擎的质量检查应被调 1 次，实际 {locate.QualityChecks}");
            Expect(locate.PoseCalls == 1, $"定位引擎的求姿态应被调 1 次，实际 {locate.PoseCalls}");
            Expect(located.Count == 1, $"应产出 1 个定位结果，实际 {located.Count}");

            // 定位成功 → 用实际置信度覆盖 cam/locate-conf（链接时那个值是配置阈值）
            Expect(
                publisher.Published.Count == 1
                && publisher.Published[0].Device == "Camera01"
                && publisher.Published[0].DataPoint == CameraDataPoints.LocateConfidence
                && (double)publisher.Published[0].Value == 0.97,
                $"定位成功应上报 {CameraDataPoints.LocateConfidence}=0.97（实际 {string.Join("；", publisher.Published)}）");

            if (located.Count == 1)
            {
                Expect(
                    located[0].Pose.CenterRow == 111 && located[0].Pose.CenterCol == 222,
                    "定位姿态应原样透出");
                Expect(
                    located[0].Image.SourcePath == imagePath,
                    "取图路径应来自总线事件（像素不进总线，由引擎按路径读）");
                Expect(
                    located[0].Image.SerialNumber == "P000001",
                    "产品条码应带到视觉输入上（追溯）");
                Expect(
                    located[0].Image.ImageWidth == 640 && located[0].Image.ImageHeight == 480,
                    "图像尺寸应带到视觉输入上");
            }

            // ── 2) 检测相机：交给检测引擎 ──
            string inspectPath = CreateImage(root, "Camera02_20260101_000000_000001.bmp");
            bus.Raise(Frame("Camera02", inspectPath, "P000001", TriggerSource.Software));

            Expect(inspect.Calls == 1, $"检测引擎应被调 1 次，实际 {inspect.Calls}");
            Expect(inspected.Count == 1, $"应产出 1 个检测结果，实际 {inspected.Count}");

            // 检测完成：先发置信度（引擎产出，覆盖链接时的配置阈值），再发"检测完成"
            Expect(
                publisher.Published.Count == 3,
                $"检测一帧应上报 2 条（置信度 + 检测完成），加上前面的定位共 3 条，实际 {publisher.Published.Count}");
            Expect(
                publisher.Published.Count == 3
                && publisher.Published[1].Device == "Camera02"
                && publisher.Published[1].DataPoint == CameraDataPoints.InspectConfidence
                && (double)publisher.Published[1].Value == 0.88,
                $"检测应上报 {CameraDataPoints.InspectConfidence}=0.88（引擎产出的分数，实际 {string.Join("；", publisher.Published)}）");
            Expect(
                publisher.Published.Count == 3
                && publisher.Published[2].Device == "Camera02"
                && publisher.Published[2].DataPoint == CameraDataPoints.InspectComplete
                && publisher.Published[2].Value is true,
                $"检测完成应最后上报 {CameraDataPoints.InspectComplete}=true（实际 {string.Join("；", publisher.Published)}）");
            Expect(
                !publisher.Published.Any(p => p.Device == "Camera01" && p.DataPoint == CameraDataPoints.InspectComplete),
                "定位相机不应产生检测完成点（点位与相机角色要对上）");

            // ── 3) 质量门不过：不调求姿态，只记警告 ──
            locate.QualityPasses = false;
            bus.Raise(Frame("Camera01", imagePath, "P000002", TriggerSource.Hardware));

            Expect(locate.PoseCalls == 1, $"质量不合格时不应调求姿态，实际被调 {locate.PoseCalls} 次");
            Expect(located.Count == 1, "质量不合格时不应产出定位结果");
            Expect(
                log.Messages.Any(m => m.Contains("图像质量不合格")),
                "质量不合格必须留痕（现场要能查到是哪颗产品没定位）");
            locate.QualityPasses = true;

            // ── 4) 通知说有图、盘上没有：必须说清是共享目录的问题 ──
            bus.Raise(Frame("Camera01", Path.Combine(root, "not-there.bmp"), "P000003", TriggerSource.Hardware));

            Expect(locate.PoseCalls == 1, "图像不存在时不应调引擎");
            Expect(
                log.Messages.Any(m => m.Contains("通知指向的图像不存在")),
                "图像缺失必须按'共享目录不通/被清理'报出来，而不是丢给引擎报一个读图失败");

            // ── 5) 拓扑里没有的相机：忽略，且只警告一次 ──
            int before = CountWarnings(log, "不在相机拓扑里");
            bus.Raise(Frame("Camera09", imagePath, "P000004", TriggerSource.Hardware));
            bus.Raise(Frame("Camera09", imagePath, "P000005", TriggerSource.Hardware));

            Expect(
                CountWarnings(log, "不在相机拓扑里") - before == 1,
                "未知相机的警告只应出现一次，不刷屏");

            // ── 6) 引擎抛异常：不能抛回总线（会连累其它订阅者），只记错误 ──
            locate.ThrowOnPose = true;
            bool escaped = false;
            try
            {
                bus.Raise(Frame("Camera01", imagePath, "P000006", TriggerSource.Hardware));
            }
            catch (Exception)
            {
                escaped = true;
            }

            Expect(!escaped, "引擎异常绝不能让总线派发线程看到");
            Expect(
                log.Messages.Any(m => m.Contains("处理第") && m.Contains("帧失败")),
                "引擎失败必须留痕，并提示可能是未 Initialize");

            // ── 7) 没登记的引擎：只取图不处理，且有告警 ──
            var blindLog = new RecordingLogService();
            var blindBus = new FakeBus();
            using (var blindWorker = new VisionFrameWorker(blindBus, roles, blindLog))
            {
                blindBus.Raise(Frame("Camera01", imagePath, "P000007", TriggerSource.Hardware));
                Expect(
                    blindLog.Messages.Any(m => m.Contains("没有对应的视觉引擎")),
                    "没有引擎时要明确说'图像不会有人处理'，而不是静默丢弃");
            }

            // ── 8) 数据点上报失败：检测本身与结果事件都不能受影响 ──
            publisher.Throw = true;
            string secondInspectPath = CreateImage(root, "Camera02_20260101_000000_000002.bmp");
            bus.Raise(Frame("Camera02", secondInspectPath, "P000008", TriggerSource.Software));

            Expect(inspect.Calls == 2, $"上报失败时检测仍应执行，实际 {inspect.Calls} 次");
            Expect(inspected.Count == 2, "上报失败时检测结果事件仍应产出");
            Expect(
                log.Messages.Any(m => m.Contains(CameraDataPoints.InspectConfidence) && m.Contains("失败")),
                "数据点上报失败必须留痕（否则上位机看不到完成却查不出原因）");
            publisher.Throw = false;

            // ── 9) 配置阈值映射：与实际判定用的必须是同一个字段，缺键不许猜默认值 ──
            var modelA = new AlgorithmConfig
            {
                ProductModel = "MODEL-A",
                Enabled = true,
                Description = "自检用配方",
                Locate = new LocateConfig
                {
                    TemplateMatching = new Dictionary<string, object> { ["minScore"] = 0.7 }
                },
                Inspect = new InspectConfig
                {
                    ImageQualityGate = new Dictionary<string, object> { ["scoreThreshold"] = 0.55 }
                }
            };
            var modelB = new AlgorithmConfig
            {
                ProductModel = "MODEL-B",
                Enabled = true,
                Description = "自检用配方 B",
                Locate = new LocateConfig { TemplateMatching = new Dictionary<string, object> { ["minScore"] = 0.65 } },
                Inspect = new InspectConfig { ImageQualityGate = new Dictionary<string, object> { ["scoreThreshold"] = 0.6 } }
            };
            var emptyModel = new AlgorithmConfig
            {
                ProductModel = "MODEL-C",
                Enabled = true,
                Description = "没有阈值键的配方"
            };

            Expect(
                CameraConfidenceConfig.ThresholdOf(modelA, CameraRole.Locate) == 0.7,
                "定位的置信度阈值应取 locate.templateMatching.minScore（引擎 FindShapeModel 用的就是它）");
            Expect(
                CameraConfidenceConfig.ThresholdOf(modelA, CameraRole.Inspect) == 0.55,
                "检测的置信度阈值应取 inspect.imageQualityGate.scoreThreshold（检测引擎产出的分数比的就是它）");
            Expect(
                CameraConfidenceConfig.ThresholdOf(emptyModel, CameraRole.Locate) is null,
                "配方里没有阈值键时必须返回 null，不许塞一个默认值当成生效门限");
            Expect(
                CameraConfidenceConfig.Select(new[] { modelA }, out string? singleNote) is not null && singleNote is null,
                "只有一个启用配方时应直接采用，且不需要提醒");
            Expect(
                CameraConfidenceConfig.Select(new[] { modelA, modelB }, out string? multiNote) is not null && multiNote is not null,
                "多个启用配方且没有型号选择时，必须给出“暂定按哪个型号”的说明（不能默默挑一个）");
            Expect(
                CameraConfidenceConfig.Select(Array.Empty<AlgorithmConfig>(), out string? emptyNote) is null && emptyNote is not null,
                "没有可用配方时应返回 null 并给出说明");
        }
        catch (Exception ex)
        {
            failures.Add($"自检过程抛异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            worker.Dispose();

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录清不掉不影响结论
            }
        }

        return failures;
    }

    private static CameraFrameEventArgs Frame(string camera, string path, string productId, TriggerSource trigger) =>
        new(camera, path, productId, 640, 480, "Mono8", 1, DateTime.Now, trigger);

    private static string CreateImage(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    private static int CountWarnings(RecordingLogService log, string fragment) =>
        log.Messages.Count(m => m.Contains(fragment));

    /// <summary>替身总线：测试里手动 Raise（真实实现是 MQTT / 进程内总线）。</summary>
    private sealed class FakeBus : IImageReadySubscriber
    {
        public event EventHandler<CameraFrameEventArgs>? ImageReady;

        public void Raise(CameraFrameEventArgs frame) => ImageReady?.Invoke(this, frame);

        public void Dispose()
        {
        }
    }

    /// <summary>替身数据点出口：记录上报，并可切换成"上报必失败"以验证检测不受影响。</summary>
    private sealed class RecordingPublisher : IDataPointPublisher
    {
        private readonly List<Sent> _published = new();

        /// <summary>true = 每次上报都抛（模拟 broker 断开 / 连接未建立）。</summary>
        public bool Throw { get; set; }

        public IReadOnlyList<Sent> Published
        {
            get
            {
                lock (_published)
                {
                    return _published.ToList();
                }
            }
        }

        public void Publish(string deviceName, string dataPoint, object value)
        {
            if (Throw)
            {
                throw new InvalidOperationException("自检用的上报失败");
            }

            lock (_published)
            {
                _published.Add(new Sent(deviceName, dataPoint, value));
            }
        }

        public sealed record Sent(string Device, string DataPoint, object Value)
        {
            public override string ToString() => $"{Device}/{DataPoint}={Value}";
        }
    }

    private sealed class StubLocateEngine : ILocateEngine
    {
        public int QualityChecks { get; private set; }

        public int PoseCalls { get; private set; }

        public bool QualityPasses { get; set; } = true;

        public bool ThrowOnPose { get; set; }

        public bool Initialize(LocateConfig config) => true;

        public ImageQualityResult CheckImageQuality(InspectionImage image)
        {
            QualityChecks++;
            return new ImageQualityResult { IsPass = QualityPasses, Score = 0.98 };
        }

        public ProductPose? FindProductPose(InspectionImage image)
        {
            PoseCalls++;

            if (ThrowOnPose)
            {
                throw new InvalidOperationException("引擎未初始化（替身故意抛）");
            }

            return new ProductPose { CenterRow = 111, CenterCol = 222, AngleRad = Math.PI / 2, Score = 0.97 };
        }

        public GluePath LoadCadGluePath(ProductPose pose) => new(new List<PointF>(), pose.Score);

        public void Dispose()
        {
        }
    }

    private sealed class StubInspectEngine : IInspectEngine
    {
        public int Calls { get; private set; }

        /// <summary>替身引擎"产出的分数"。</summary>
        public double Score { get; set; } = 0.88;

        /// <inheritdoc />
        public double? LastConfidence { get; private set; }

        public bool Initialize(InspectConfig config) => true;

        public void Inspect(InspectionImage image)
        {
            Calls++;
            LastConfidence = Score;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>把日志收进内存的日志服务（自检要断言"该留痕的留了痕"）。</summary>
    private sealed class RecordingLogService : ILogService
    {
        private readonly List<string> _messages;

        public RecordingLogService(string name = "VisionFrameWorkerSelfCheck", List<string>? messages = null)
        {
            Name = name;
            _messages = messages ?? new List<string>();
        }

        public string Name { get; }

        public IReadOnlyList<string> Messages => _messages;

        public bool IsEnabled(LogLevel level) => true;

        public ILogService ForContext(string name) => new RecordingLogService(name, _messages);

        public void Log(LogLevel level, string message) => _messages.Add($"[{level}] {message}");

        public void Log(LogLevel level, string messageTemplate, params object?[] args) =>
            _messages.Add($"[{level}] {messageTemplate} | {string.Join(", ", args)}");

        public void Log(LogLevel level, Exception exception, string message) =>
            _messages.Add($"[{level}] {message} | {exception.Message}");

        public void Log(LogLevel level, Exception exception, string messageTemplate, params object?[] args) =>
            _messages.Add($"[{level}] {messageTemplate} | {string.Join(", ", args)} | {exception.Message}");
    }
}

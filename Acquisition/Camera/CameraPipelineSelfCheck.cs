using Core.Acquisition;
using Core.Bridge;
using Core.Configuration;
using Core.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Acquisition.Camera;

/// <summary>
/// 相机<b>采集侧</b>的离线自检：不需要真相机——用 Simulation 驱动跑
/// "配置 → 装配 → 出图 → 落盘 → 发通知"这一段，并钉住每个边界行为。
/// </summary>
/// <remarks>
/// <para>
/// 视觉侧的消费（订阅 → 取图 → 引擎）由 <c>Vision.Halcon.VisionFrameWorkerSelfCheck</c> 管；
/// 两侧合起来的端到端链条，用两份自检 + 一个把两侧接起来的宿主即可验证（见各文件注释）。
/// </para>
/// <para>跑法：任何 net8.0 控制台工程引用 Acquisition + Core，打印
/// <c>CameraPipelineSelfCheck.Run()</c> 的返回值（空列表 = 通过）。</para>
/// </remarks>
public static class CameraPipelineSelfCheck
{
    /// <summary>同步跑（内部切到线程池，从 UI 线程调用也不会死锁）。</summary>
    /// <param name="workDirectory">图像落盘目录；缺省用临时目录</param>
    public static IReadOnlyList<string> Run(string? workDirectory = null) =>
        Task.Run(() => RunAsync(workDirectory)).GetAwaiter().GetResult();

    /// <summary>异步跑。</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(
        string? workDirectory = null, CancellationToken ct = default)
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
            ?? Path.Combine(Path.GetTempPath(), "DispensVisTrace-camera-selfcheck");
        Directory.CreateDirectory(root);

        var log = new RecordingLogService();

        // ── 1) 配置加载器：没配 ≠ 配错 ──
        Expect(
            CameraTopologyLoader.TryLoadFromFile(Path.Combine(root, "not-there.json")) is null,
            "cameras.json 不存在时应返回 null（本机没配相机），而不是空拓扑");

        Expect(
            Throws(() => CameraTopologyLoader.FromJson("""{ "cameras": [] }""")),
            "缺 imageRootDirectory 的配置必须被拒绝");

        // 重复 cameraName（忽略大小写）：同名相机会在 MQTT 主题 / 日志来源 / 落盘文件名上互相覆盖
        const string duplicateCameraJson =
            """
            {
              "imageRootDirectory": "x",
              "cameras": [
                { "cameraName": "Camera01", "sdkType": "Simulation" },
                { "cameraName": "camera01", "sdkType": "Simulation" }
              ]
            }
            """;

        Expect(
            Throws(() => CameraTopologyLoader.FromJson(duplicateCameraJson)),
            "cameraName 重复（忽略大小写）必须被拒绝：同名相机会在主题/日志/文件名上互相覆盖");

        Expect(
            Throws(() => CameraDriverFactory.Create(
                new CameraConfig("Camera09", "Basler", "SN", TriggerSource.Hardware, 0, 0, 0, 0, null), log)),
            "未登记的 sdkType 必须抛 NotSupportedException，而不是悄悄返回一个假驱动");

        // ── 2) 装配两台仿真相机（拓扑直接构造，避免把临时路径塞进 JSON）──
        var drivers = new Dictionary<string, SimulationCameraDriver>(StringComparer.OrdinalIgnoreCase);
        ICameraDriver Factory(CameraConfig config, ILogService _)
        {
            var driver = new SimulationCameraDriver(config.CameraName);
            drivers[config.CameraName] = driver;
            return driver;
        }

        var topology = new CameraTopology(
            new ImageStorageOptions(root),
            new[]
            {
                new CameraSetup(
                    new CameraConfig("Camera01", CameraDriverFactory.SdkSimulation, "SIM-01",
                        TriggerSource.Hardware, 3000, 0, 640, 480, null),
                    CameraRole.Locate),
                new CameraSetup(
                    new CameraConfig("Camera02", CameraDriverFactory.SdkSimulation, "SIM-02",
                        TriggerSource.Software, 3000, 0, 640, 480, null),
                    CameraRole.Inspect)
            },
            AutoStart: true);

        var productCodes = new ProductCodeGenerator();
        var runtime = new CameraRuntime(topology, log, productCodes, Factory);
        var bus = new RecordingPublisher();
        var publisher = new ImageReadyPublisher(runtime, bus, log);

        try
        {
            Expect(
                publisher.Cameras.Count == 2,
                $"两台相机都应接入发布者，实际 {publisher.Cameras.Count}");

            await runtime.StartAsync(ct).ConfigureAwait(false);

            Expect(
                runtime.Cameras.All(c => c.State == CameraState.Armed),
                "启动后两台相机都应处于 Armed：" +
                string.Join("、", runtime.Cameras.Select(c => $"{c.CameraName}={c.State}")));

            // ── 3) Camera01（硬触发 = 产品到位）：出图 → 落盘 → 发通知 ──
            drivers["Camera01"].TriggerHardwareFrame();

            CameraFrameEventArgs? first = await bus.WaitForFrameAsync(TimeSpan.FromSeconds(10), ct)
                                                    .ConfigureAwait(false);

            Expect(first is not null, "硬触发后应收到一条图像就绪通知");
            if (first is not null)
            {
                Expect(first.CameraName == "Camera01", $"通知里的相机名应为 Camera01，实际 {first.CameraName}");
                Expect(
                    Path.GetFileName(first.ImagePath).StartsWith("Camera01_", StringComparison.Ordinal),
                    $"落盘文件名应带相机名：{Path.GetFileName(first.ImagePath)}");
                Expect(File.Exists(first.ImagePath), $"通知指向的图像应真的存在：{first.ImagePath}");
                Expect(
                    first.TriggerSource == TriggerSource.Hardware,
                    "通知应带上真实触发来源（硬触发）");
                Expect(
                    !string.IsNullOrEmpty(first.ProductId),
                    "通知应带产品条码（追溯链的起点）");
                Expect(
                    first.SequenceNumber > 0 && first.Width == 640 && first.Height == 480,
                    "通知应带序号与图像尺寸");
            }

            // ── 4) Camera02（软触发）：出图 → 落盘 → 发通知 ──
            await runtime.TriggerAsync("Camera02", ct).ConfigureAwait(false);

            CameraFrameEventArgs? second = await bus.WaitForFrameAsync(TimeSpan.FromSeconds(10), ct)
                                                     .ConfigureAwait(false);

            Expect(second is not null, "软触发后应收到一条图像就绪通知");
            if (second is not null)
            {
                Expect(second.CameraName == "Camera02", $"通知里的相机名应为 Camera02，实际 {second.CameraName}");
                Expect(File.Exists(second.ImagePath), $"通知指向的图像应真的存在：{second.ImagePath}");
                Expect(
                    !string.IsNullOrEmpty(second.ProductId),
                    "软触发帧也应带产品条码（与定位相机共用同一个条码发生器）");
            }

            // ── 5) 总线满时必须留痕，不许静默丢 ──
            bus.Accept = false;
            drivers["Camera01"].TriggerHardwareFrame();
            await Task.Delay(200, ct).ConfigureAwait(false);

            Expect(bus.DroppedCount == 1, $"被拒的通知应计入丢弃数，实际 {bus.DroppedCount}");
            Expect(
                log.Messages.Any(m => m.Contains("图像就绪通知发布失败")),
                "通知发不出去时必须写一条警告（静默丢弃在现场表现成'某几颗没检测'，最难查）");

            // ── 6) 停采集 ──
            await runtime.StopAsync(ct).ConfigureAwait(false);
            Expect(
                runtime.Cameras.All(c => c.State == CameraState.Disconnected),
                "停止后两台相机都应断开");
        }
        catch (Exception ex)
        {
            failures.Add($"自检过程抛异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            publisher.Dispose();
            await runtime.DisposeAsync().ConfigureAwait(false);

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

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>替身总线：记录通知，可切换成"拒收"以验证丢通知留痕。</summary>
    private sealed class RecordingPublisher : IImageReadyPublisher
    {
        private readonly Queue<CameraFrameEventArgs> _pending = new();
        private readonly SemaphoreSlim _signal = new(0);
        private long _dropped;

        public bool Accept { get; set; } = true;

        public long DroppedCount => Interlocked.Read(ref _dropped);

        public bool Publish(CameraFrameEventArgs frame)
        {
            if (!Accept)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            lock (_pending)
            {
                _pending.Enqueue(frame);
            }

            _signal.Release();
            return true;
        }

        /// <summary>取下一帧（按到达顺序，取走即出队）。</summary>
        public async Task<CameraFrameEventArgs?> WaitForFrameAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (!await _signal.WaitAsync(timeout, ct).ConfigureAwait(false))
            {
                return null;
            }

            lock (_pending)
            {
                return _pending.Count == 0 ? null : _pending.Dequeue();
            }
        }
    }

    /// <summary>把日志收进内存的日志服务（自检要断言"该留痕的留了痕"）。</summary>
    private sealed class RecordingLogService : ILogService
    {
        private readonly List<string> _messages;

        public RecordingLogService(string name = "CameraPipelineSelfCheck", List<string>? messages = null)
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

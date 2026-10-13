using Core.Acquisition;
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
/// 相机<b>设备侧 MQTT 通道</b>的离线自检：不需要 broker，也不需要真相机——
/// 用仿真相机 + 替身驱动，把"采集事件 → <c>cam/*</c> 数据点"这一段的每个边界钉住。
/// </summary>
/// <remarks>
/// <para>
/// 线上契约（主题 = 数据点地址、不加相机名前缀）由 <c>Plugins.Drivers.Mqtt.MqttDeviceDriverSelfCheck</c>
/// 用真 broker 与宿主侧驱动对线；这里管的是<b>桥接侧</b>：发了哪些点、什么顺序、触发怎么进来、
/// 没配 mqtt 的相机怎么被跳过、同一个 broker 的两台相机有没有被警告。
/// </para>
/// <para>跑法：任何 net8.0 控制台工程引用 Acquisition + Core，打印
/// <c>MqttCameraChannelSelfCheck.Run()</c> 的返回值（空列表 = 通过）。</para>
/// </remarks>
public static class MqttCameraChannelSelfCheck
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
            ?? Path.Combine(Path.GetTempPath(), "DispensVisTrace-mqtt-channel-selfcheck");
        Directory.CreateDirectory(root);

        var log = new RecordingLogService();

        // ── 0) cameras.json 的 mqtt 段：能读到、填默认值、写错当场拒绝 ──
        const string mqttJson =
            """
            {
              "imageRootDirectory": "x",
              "cameras": [
                {
                  "cameraName": "Camera01",
                  "sdkType": "Simulation",
                  "mqtt": { "endpoint": "mqtt://192.168.1.40:1883" }
                },
                {
                  "cameraName": "Camera02",
                  "sdkType": "Simulation",
                  "role": "Inspect",
                  "mqtt": {
                    "endpoint": "mqtts://192.168.1.41",
                    "clientId": "cam02",
                    "username": "operator",
                    "passwordEnvironmentVariable": "MQTT_PASSWORD"
                  }
                },
                { "cameraName": "Camera03", "sdkType": "Simulation" }
              ]
            }
            """;

        CameraTopology loaded = CameraTopologyLoader.FromJson(mqttJson);

        Expect(loaded.Cameras[0].Mqtt?.Endpoint == "mqtt://192.168.1.40:1883",
            "mqtt.endpoint 应被读进拓扑（broker 地址是设备侧自己的配置，不是上位机的）");
        Expect(loaded.Cameras[0].Mqtt?.ClientId == "Camera01",
            "mqtt.clientId 留空时应填成相机名（同 broker 上标识重复会互相顶掉连接）");
        Expect(loaded.Cameras[1].Mqtt?.ClientId == "cam02",
            "显式配了 mqtt.clientId 时应原样使用");
        Expect(loaded.Cameras[1].Mqtt?.ParseEndpoint() is { Port: MqttEndpoint.DefaultTlsPort, UseTls: true },
            "mqtts:// 未写端口时应落到 8883 并启用 TLS");
        Expect(loaded.Cameras[2].Mqtt is null,
            "没写 mqtt 段的相机应保持 null（纯本机采集，不上报）");

        Expect(
            Throws(() => CameraTopologyLoader.FromJson(
                """
                { "imageRootDirectory": "x", "cameras": [ { "cameraName": "Camera01", "sdkType": "Simulation",
                  "mqtt": { "endpoint": "192.168.1.40:notaport" } } ] }
                """)),
            "mqtt.endpoint 写错必须在启动阶段被拒绝（否则现场只表现为'上位机读不到数据'）");

        Expect(
            Throws(() => CameraTopologyLoader.FromJson(
                """
                { "imageRootDirectory": "x", "cameras": [ { "cameraName": "Camera01", "sdkType": "Simulation",
                  "mqtt": { "endpoint": "mqtt://192.168.1.40:1883", "username": "operator" } } ] }
                """)),
            "配了 mqtt.username 却没配密码环境变量名必须被拒绝（配置只存环境变量名，不存明文）");

        var mqttDrivers = new Dictionary<string, FakeDeviceDriver>(StringComparer.OrdinalIgnoreCase);
        IDeviceDriver MqttFactory(CameraSetup setup, ILogService _)
        {
            var driver = new FakeDeviceDriver();
            mqttDrivers[setup.Config.CameraName] = driver;
            return driver;
        }

        var cameraDrivers = new Dictionary<string, SimulationCameraDriver>(StringComparer.OrdinalIgnoreCase);
        ICameraDriver CameraFactory(CameraConfig config, ILogService _)
        {
            var driver = new SimulationCameraDriver(config.CameraName);
            cameraDrivers[config.CameraName] = driver;
            return driver;
        }

        // Camera01：定位（硬触发），配了 mqtt；Camera02：检测（软触发），配了 mqtt；
        // Camera03：没配 mqtt —— 必须被整条跳过（纯本机采集）
        var topology = new CameraTopology(
            new ImageStorageOptions(root),
            new[]
            {
                new CameraSetup(
                    new CameraConfig("Camera01", CameraDriverFactory.SdkSimulation, "SIM-01",
                        TriggerSource.Hardware, 3000, 0, 640, 480, null),
                    CameraRole.Locate,
                    new MqttDeviceLink("mqtt://192.168.1.40:1883", ClientId: "Camera01")),
                new CameraSetup(
                    new CameraConfig("Camera02", CameraDriverFactory.SdkSimulation, "SIM-02",
                        TriggerSource.Software, 3000, 0, 640, 480, null),
                    CameraRole.Inspect,
                    new MqttDeviceLink("mqtt://192.168.1.41:1883", ClientId: "Camera02")),
                new CameraSetup(
                    new CameraConfig("Camera03", CameraDriverFactory.SdkSimulation, "SIM-03",
                        TriggerSource.Software, 3000, 0, 640, 480, null),
                    CameraRole.Inspect)
            },
            AutoStart: true);

        var runtime = new CameraRuntime(topology, log, new ProductCodeGenerator(), CameraFactory);
        var channel = new MqttCameraChannel(topology, runtime, MqttFactory, log);

        try
        {
            // ── 1) 装配：只有配了 mqtt 的相机才有通道 ──
            Expect(channel.Count == 2, $"只有两台相机配了 mqtt，通道数应为 2，实际 {channel.Count}");
            Expect(
                !channel.Cameras.Contains("Camera03", StringComparer.OrdinalIgnoreCase),
                "没配 mqtt 段的相机不应出现在 MQTT 通道里");
            Expect(channel.BridgeOf("Camera03") is null, "没配 mqtt 的相机不应有桥");
            Expect(channel.DriverOf("Camera01") is FakeDeviceDriver d01 && ReferenceEquals(d01, mqttDrivers["Camera01"]),
                "每台相机应各自有一个（自己的）设备侧驱动实例");

            Expect(
                channel.BridgeOf("Camera01")?.ImageUrlDataPoint == CameraDataPoints.LocateImageUrl,
                "定位相机的图像 URL 点应是 cam/locate-image-url");
            Expect(
                channel.BridgeOf("Camera02")?.ImageUrlDataPoint == CameraDataPoints.InspectImageUrl,
                "检测相机的图像 URL 点应是 cam/inspect-image-url");

            // ── 2) 启动：按配置里的 endpoint 连接、只订触发点、主题不带相机名前缀 ──
            await channel.StartAsync(ct).ConfigureAwait(false);

            Expect(
                mqttDrivers["Camera01"].ConnectEndpoints.SequenceEqual(new[] { "mqtt://192.168.1.40:1883" }),
                "驱动必须用 cameras.json 里这台相机自己的 endpoint 连接（两台相机的 broker 不同）");
            Expect(
                mqttDrivers["Camera02"].ConnectEndpoints.SequenceEqual(new[] { "mqtt://192.168.1.41:1883" }),
                "Camera02 应连自己的 broker，而不是复用 Camera01 的连接");

            foreach (string name in new[] { "Camera01", "Camera02" })
            {
                List<string> subscribed = mqttDrivers[name].Subscribed;
                Expect(
                    subscribed.SequenceEqual(new[] { CameraDataPoints.Trigger }),
                    $"{name} 应只订 {CameraDataPoints.Trigger}，实际 [{string.Join(", ", subscribed)}]");
                Expect(
                    subscribed.All(t => !t.Contains(name, StringComparison.OrdinalIgnoreCase)),
                    $"{name} 的主题里不能带相机名前缀（宿主是按资源树地址逐字订阅的）：[{string.Join(", ", subscribed)}]");
            }

            // 幂等：重复 StartAsync 不应重复连接
            await channel.StartAsync(ct).ConfigureAwait(false);
            Expect(mqttDrivers["Camera01"].ConnectEndpoints.Count == 1, "StartAsync 应幂等，不能重复连接 broker");

            // ── 3) 相机 Armed：复位触发状态（frame-ready=false、trigger-error 空串）──
            await runtime.StartAsync(ct).ConfigureAwait(false);

            Expect(
                await WaitUntilAsync(
                    () => mqttDrivers["Camera01"].Published.Any(p => p.Address == CameraDataPoints.FrameReady && p.Value is false),
                    TimeSpan.FromSeconds(5), ct).ConfigureAwait(false),
                "相机进入 Armed 时应把 cam/frame-ready 复位为 false");
            Expect(
                mqttDrivers["Camera01"].Published.Any(
                    p => p.Address == CameraDataPoints.TriggerError && (string)p.Value == string.Empty),
                "相机进入 Armed 时应把 cam/trigger-error 清成空串（表示无错误）");

            mqttDrivers["Camera01"].Published.Clear();
            mqttDrivers["Camera02"].Published.Clear();

            // ── 4) 硬触发一帧：按序上报采集侧的全部数据点，frame-ready 最后 ──
            cameraDrivers["Camera01"].TriggerHardwareFrame();

            Expect(
                await WaitUntilAsync(
                    () => mqttDrivers["Camera01"].Published.Any(p => p.Address == CameraDataPoints.FrameReady && p.Value is true),
                    TimeSpan.FromSeconds(10), ct).ConfigureAwait(false),
                "硬触发后应上报 cam/frame-ready=true");

            List<FakeDeviceDriver.Sent> first = mqttDrivers["Camera01"].Published.ToList();

            Expect(first.All(p => !p.Address.StartsWith("Camera01/", StringComparison.OrdinalIgnoreCase)),
                "主题不能带相机名前缀（宿主按 TagPoint.Address 订阅，前缀会让线静默断掉）");
            Expect(first.Any(p => p.Address == CameraDataPoints.TriggerSource && p.Value is TriggerSource.Hardware),
                "cam/trigger-source 应是真实触发来源（硬触发）");
            Expect(first.Any(p => p.Address == CameraDataPoints.TriggerCount && p.Value is long),
                "cam/trigger-count 应是帧序号");
            Expect(
                first.Any(p => p.Address == CameraDataPoints.LocateImageUrl
                               && p.Value is string s
                               && s.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                               && Path.IsPathRooted(s)),
                "cam/locate-image-url 应是落盘后的图像路径");
            Expect(first.Count > 0 && first[^1].Address == CameraDataPoints.FrameReady,
                $"frame-ready 必须最后发（上位机看到它时路径已就绪），实际最后一条是 {first.LastOrDefault()?.Address}");
            Expect(
                !first.Any(p => p.Address == "cam/product-id"),
                "cam/product-id 已从契约里移除（条码随图像与检测结果走，不再单独占一个点）");

            // ── 5) 软触发方向：上位机写 cam/trigger → 采集服务软触发（检测相机是软触发）──
            var second = mqttDrivers["Camera02"];
            second.Raise(CameraDataPoints.Trigger, "true");

            Expect(
                await WaitUntilAsync(
                    () => second.Published.Any(p => p.Address == CameraDataPoints.InspectImageUrl),
                    TimeSpan.FromSeconds(10), ct).ConfigureAwait(false),
                "上位机写 cam/trigger=true 后检测相机应出图并上报 cam/inspect-image-url");

            Expect(
                second.Published.Any(p => p.Address == CameraDataPoints.TriggerSource && p.Value is TriggerSource.Software),
                "软触发那帧的来源应是 Software");

            // ── 6) 无法识别的触发载荷必须留痕（否则表现为"触发没反应"却查不到原因）──
            second.Published.Clear();
            second.Raise(CameraDataPoints.Trigger, "OPEN");

            Expect(
                log.Messages.Any(m => m.Contains("无法识别的触发载荷")),
                "无法识别的触发载荷必须写警告");
            await Task.Delay(200, ct).ConfigureAwait(false);
            Expect(
                second.Published.Count == 0,
                $"无法识别的载荷不应触发采集，实际产生了 {second.Published.Count} 条上报");

            // ── 7) 数据点统一出口：视觉侧按相机名写点，落到那台相机的连接上 ──
            var publisher = new DeviceDataPointPublisher(channel, log);
            second.Published.Clear();

            publisher.Publish("Camera02", CameraDataPoints.InspectComplete, true);

            Expect(
                await WaitUntilAsync(
                    () => second.Published.Any(p => p.Address == CameraDataPoints.InspectComplete && p.Value is true),
                    TimeSpan.FromSeconds(5), ct).ConfigureAwait(false),
                $"视觉侧的 {CameraDataPoints.InspectComplete} 应经统一出口写到 Camera02 的连接上");
            Expect(
                mqttDrivers["Camera01"].Published.All(p => p.Address != CameraDataPoints.InspectComplete),
                "写给 Camera02 的点不能落到 Camera01 的连接上（按相机名找连接）");

            publisher.Publish("Camera03", CameraDataPoints.InspectComplete, true);
            Expect(
                log.Messages.Any(m => m.Contains("Camera03") && m.Contains("没有 MQTT 通道")),
                "往没配 mqtt 的相机写点必须留痕（否则数据点凭空消失，没人知道为什么）");

            // ── 8) 断开：桥退订触发点，驱动断开 ──
            await channel.StopAsync(ct).ConfigureAwait(false);

            Expect(
                mqttDrivers["Camera01"].Unsubscribed.SequenceEqual(new[] { CameraDataPoints.Trigger }),
                "断开时应退订 cam/trigger（重启通道时才不会重复订阅）");
            Expect(
                mqttDrivers.Values.All(d => d.DisconnectCount == 1),
                "断开时每台相机的驱动都应断开一次");

            // ── 9) 同一个 broker 的两台相机必须被警告（主题不带前缀时会互相覆盖）──
            var sameBrokerTopology = new CameraTopology(
                new ImageStorageOptions(root),
                new[]
                {
                    new CameraSetup(
                        new CameraConfig("Camera01", CameraDriverFactory.SdkSimulation, "SIM-01",
                            TriggerSource.Hardware, 3000, 0, 640, 480, null),
                        CameraRole.Locate,
                        new MqttDeviceLink("mqtt://192.168.1.40:1883", ClientId: "Camera01")),
                    new CameraSetup(
                        new CameraConfig("Camera02", CameraDriverFactory.SdkSimulation, "SIM-02",
                            TriggerSource.Software, 3000, 0, 640, 480, null),
                        CameraRole.Inspect,
                        new MqttDeviceLink("mqtt://192.168.1.40:1883", ClientId: "Camera02"))
                },
                AutoStart: true);

            var sameBrokerLog = new RecordingLogService();
            var sameBrokerRuntime = new CameraRuntime(sameBrokerTopology, sameBrokerLog, new ProductCodeGenerator(), CameraFactory);
            await using (var sameBrokerChannel = new MqttCameraChannel(
                sameBrokerTopology, sameBrokerRuntime, MqttFactory, sameBrokerLog))
            {
                Expect(sameBrokerChannel.Count == 2, "同一个 broker 的配置仍应建出两条通道（只是要警告）");
                Expect(
                    sameBrokerLog.Messages.Any(m => m.Contains("同一个 MQTT broker")),
                    "两台相机共用一个 broker 时必须警告：主题不带前缀，cam/trigger 会互相覆盖");
            }

            await sameBrokerRuntime.DisposeAsync().ConfigureAwait(false);

            // ── 10) 驱动建不出来时：跳过这台相机并留痕，不影响其它相机 ──
            var brokenLog = new RecordingLogService();
            var brokenRuntime = new CameraRuntime(topology, brokenLog, new ProductCodeGenerator(), CameraFactory);
            await using (var brokenChannel = new MqttCameraChannel(
                topology,
                brokenRuntime,
                (_, _) => throw new InvalidOperationException("自检用的建驱动失败"),
                brokenLog))
            {
                Expect(brokenChannel.Count == 0, "驱动建不出来时通道应为空，而不是留半个状态");
                Expect(
                    brokenLog.Messages.Any(m => m.Contains("驱动创建失败")),
                    "驱动创建失败必须留痕（否则资源树数据点凭空消失，没人知道为什么）");
            }

            await brokenRuntime.DisposeAsync().ConfigureAwait(false);

            // ── 11) 释放 ──
            await channel.DisposeAsync().ConfigureAwait(false);
            Expect(
                mqttDrivers.Values.All(d => d.DisposeCount == 1),
                "释放通道时每台相机的驱动都应释放一次");
        }
        catch (Exception ex)
        {
            failures.Add($"自检过程抛异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 释放失败不影响结论
            }

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

    /// <summary>轮询等待条件成立（自检里不用信号量，避免替身驱动写得比被检查的代码还复杂）。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        return condition();
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

    /// <summary>替身设备侧驱动：记录收发，可手动注入"上位机写进来的值"。</summary>
    private sealed class FakeDeviceDriver : IDeviceDriver
    {
        private readonly object _sync = new();

        public string DriverType => "MQTT-FAKE";

        public bool IsConnected { get; private set; }

        public string EndPoint { get; private set; } = string.Empty;

        public List<string> ConnectEndpoints { get; } = new();

        public List<string> Subscribed { get; } = new();

        public List<string> Unsubscribed { get; } = new();

        public List<Sent> Published { get; } = new();

        public int DisconnectCount { get; private set; }

        public int DisposeCount { get; private set; }

        public event EventHandler<DataPointReceivedEventArgs>? DataPointReceived;

        public event EventHandler<DriverErrorEventArgs>? ErrorOccurred;

        public Task ConnectAsync(string endpoint, CancellationToken ct)
        {
            lock (_sync)
            {
                ConnectEndpoints.Add(endpoint);
                EndPoint = endpoint;
                IsConnected = true;
            }

            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            lock (_sync)
            {
                DisconnectCount++;
                IsConnected = false;
            }

            return Task.CompletedTask;
        }

        public Task SubscribeAsync(string address, CancellationToken ct)
        {
            lock (_sync)
            {
                Subscribed.Add(address);
            }

            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(string address, CancellationToken ct)
        {
            lock (_sync)
            {
                Unsubscribed.Add(address);
            }

            return Task.CompletedTask;
        }

        public Task PublishAsync(string address, object value, CancellationToken ct)
        {
            lock (_sync)
            {
                Published.Add(new Sent(address, value));
            }

            return Task.CompletedTask;
        }

        /// <summary>模拟"上位机写了一个值进来"。</summary>
        public void Raise(string address, string payload) =>
            DataPointReceived?.Invoke(this, new DataPointReceivedEventArgs(address, payload));

        /// <summary>模拟驱动报错（当前自检未使用，保留以便扩展）。</summary>
        public void RaiseError(string message, bool isFatal = false) =>
            ErrorOccurred?.Invoke(this, new DriverErrorEventArgs(DriverType, message, isFatal));

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                DisposeCount++;
                IsConnected = false;
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>清空本轮记录（自检按阶段断言用）。</summary>
        public void Clear()
        {
            lock (_sync)
            {
                Published.Clear();
            }
        }

        public sealed record Sent(string Address, object Value);
    }

    /// <summary>把日志收进内存的日志服务（自检要断言"该留痕的留了痕"）。</summary>
    private sealed class RecordingLogService : ILogService
    {
        private readonly List<string> _messages;

        public RecordingLogService(string name = "MqttCameraChannelSelfCheck", List<string>? messages = null)
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

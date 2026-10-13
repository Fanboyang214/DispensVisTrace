using Core.Acquisition;
using Core.Logging;
using Core.Models;
using MQTTnet;
using MQTTnet.Server;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Plugins.Drivers.Mqtt
{
    /// <summary>
    /// 设备侧 MQTT 驱动的离线自检：起一个<b>进程内 MQTT broker</b>，让设备侧
    /// （<see cref="MqttDeviceDriver"/>）与宿主侧（<see cref="MqttDriver"/>）真的对一次线。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么必须两端一起跑：两侧最容易出的错不是"发不出去"，而是<b>主题或载荷对不上</b>——
    /// 设备侧加了前缀、bool 写成 <c>True</c>、数字带了区域格式，都会让线"看起来通的"但数据是空的，
    /// 而且两端都不报错。只有真连一次 broker 才能钉住这件事。
    /// </para>
    /// <para>跑法：任何 net8.0 控制台工程引用 Plugins + Core，打印 <c>MqttDeviceDriverSelfCheck.Run()</c>
    /// 的返回值（空列表 = 通过）。</para>
    /// </remarks>
    public static class MqttDeviceDriverSelfCheck
    {
        /// <summary>同步跑（内部切线程池）。</summary>
        public static IReadOnlyList<string> Run() =>
            Task.Run(() => RunAsync()).GetAwaiter().GetResult();

        /// <summary>异步跑。</summary>
        public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
        {
            var failures = new List<string>();

            void Expect(bool ok, string what)
            {
                if (!ok)
                {
                    failures.Add(what);
                }
            }

            // ── 1) 端点解析：默认端口与 TLS 不能靠猜 ──
            Expect(MqttEndpoint.TryParse("mqtt://192.168.1.40:1883", out MqttEndpoint? e1, out _)
                   && e1!.Host == "192.168.1.40" && e1.Port == 1883 && !e1.UseTls,
                "mqtt://主机:端口 应解析为主机 + 1883 + 非 TLS");

            Expect(MqttEndpoint.TryParse("mqtts://cam.local", out MqttEndpoint? e2, out _)
                   && e2!.Port == MqttEndpoint.DefaultTlsPort && e2.UseTls,
                "mqtts:// 未写端口时应落到 8883 且启用 TLS");

            Expect(MqttEndpoint.TryParse("192.168.1.40", out MqttEndpoint? e3, out _)
                   && e3!.Port == MqttEndpoint.DefaultPort,
                "不写 scheme 与端口时应落到 mqtt 1883（现场常见写法）");

            Expect(!MqttEndpoint.TryParse("http://192.168.1.40", out _, out _),
                "非 MQTT 协议必须被拒绝，而不是悄悄当成 MQTT");

            Expect(!MqttEndpoint.TryParse("   ", out _, out _),
                "空端点必须被拒绝");

            // ── 2) 载荷编码：宿主侧按 TagPoint.DataType 解析的就是这些文本 ──
            Expect(MqttDeviceDriver.Encode(null) == string.Empty, "null 应编码为空串");
            Expect(MqttDeviceDriver.Encode(true) == "true", "bool 应编码为小写 true");
            Expect(MqttDeviceDriver.Encode(false) == "false", "bool 应编码为小写 false");
            Expect(MqttDeviceDriver.Encode(3L) == "3", "整数应编码为 InvariantCulture 文本");
            Expect(MqttDeviceDriver.Encode(1.5) == "1.5", "小数应编码为 InvariantCulture 文本（不受区域设置影响）");
            Expect(MqttDeviceDriver.Encode(TriggerSource.Software) == "software",
                "枚举应编码为小写名（资源树 enum 值即 hardware/software 形式）");
            Expect(MqttDeviceDriver.Encode("/share/Camera01_1.bmp") == "/share/Camera01_1.bmp",
                "字符串应原样编码（图像 URL 里有下划线与反斜杠）");

            // ── 3) 起进程内 broker ──
            int port = FindFreePort();
            await using var broker = new InProcessBroker(port);

            try
            {
                await broker.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add($"进程内 MQTT broker 启动失败（端口 {port}）：{ex.GetType().Name}: {ex.Message}");
                return failures;
            }

            var log = new RecordingLogService();
            string endpoint = $"mqtt://127.0.0.1:{port}";

            var device = new MqttDeviceDriver(
                new MqttDeviceLink(endpoint, ClientId: "Camera01"), log);

            // 设备侧收到的消息（设备侧只订阅 cam/trigger 一类的点）
            var received = new List<DataPointReceivedEventArgs>();
            device.DataPointReceived += (_, e) =>
            {
                lock (received)
                {
                    received.Add(e);
                }
            };

            List<DataPointReceivedEventArgs> Snapshot()
            {
                lock (received)
                {
                    return received.ToList();
                }
            }

            try
            {
                // ── 4) 未连接时的语义：发布必须抛（不静默丢数据），订阅只登记 ──
                Expect(
                    await ThrowsAsync(() => device.PublishAsync("cam/frame-ready", true, ct)).ConfigureAwait(false),
                    "未连接时发布必须抛 InvalidOperationException，不能静默丢弃");

                await device.SubscribeAsync("cam/trigger-timeout", ct).ConfigureAwait(false);
                Expect(device.Topics.Contains("cam/trigger-timeout"),
                    "未连接时订阅应被登记下来（连接或重连后自动补订）");
                Expect(
                    log.Messages.Any(m => m.Contains("cam/trigger-timeout") && m.Contains("已登记")),
                    "未连接时的订阅必须留痕（否则表现为'订了但收不到'）");

                // ── 5) 连接：登记过的主题要自动补订 ──
                await device.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                Expect(device.IsConnected, "ConnectAsync 之后应处于已连接状态");

                await device.SubscribeAsync("cam/trigger", ct).ConfigureAwait(false);
                Expect(device.Topics.Count == 2, $"应登记两个主题，实际 {device.Topics.Count}");

                // ── 6) 宿主侧（采集宿主用 IProtocolDriver）按资源树 Tag 订阅 ──
                var triggerTag = new TagPoint
                {
                    Name = "Trigger",
                    Address = "cam/trigger",
                    DataType = TagDataType.Bool,
                    Access = TagAccess.Write        // 命令点：宿主只写不读
                };
                var frameReadyTag = new TagPoint
                {
                    Name = "FrameReady",
                    Address = "cam/frame-ready",
                    DataType = TagDataType.Bool
                };
                var countTag = new TagPoint
                {
                    Name = "TriggerCount",
                    Address = "cam/trigger-count",
                    DataType = TagDataType.Int32
                };
                var imageUrlTag = new TagPoint
                {
                    Name = "LocateImageURL",
                    Address = "cam/locate-image-url",
                    DataType = TagDataType.String
                };

                var hostConfig = new DeviceConfig
                {
                    Id = "Camera01",
                    Name = "LocateCamera",
                    DriverType = "Drivers.Mqtt",
                    IpAddress = "127.0.0.1",
                    Port = port,
                    MqttClientId = "SelfCheckHost",
                    Tags = new List<TagPoint> { triggerTag, frameReadyTag, countTag, imageUrlTag }
                };

                await using var host = new MqttDriver(hostConfig);
                await host.ConnectAsync(ct).ConfigureAwait(false);
                Expect(host.IsConnected, "宿主侧驱动应连上同一个 broker");

                // 先订一遍再发：MQTT 没有 retain 时"发在订阅之前"就是收不到，
                // 现场上位机也是先起订阅、等设备出帧，所以这个顺序才是真实顺序
                _ = await host.ReadTagsAsync(new[] { triggerTag, frameReadyTag, countTag, imageUrlTag }, ct)
                            .ConfigureAwait(false);

                // 设备侧发布（主题 = 数据点地址，不带任何前缀）
                const string imagePath = @"\\share\images\Camera01_20260101_120000_000_000001.bmp";
                await device.PublishAsync("cam/frame-ready", true, ct).ConfigureAwait(false);
                await device.PublishAsync("cam/trigger-count", 3L, ct).ConfigureAwait(false);
                await device.PublishAsync("cam/locate-image-url", imagePath, ct).ConfigureAwait(false);

                // 宿主侧读（轮询：订阅后第一条消息到达有时间差）
                IReadOnlyList<TagValue>? values = null;
                var readDeadline = Stopwatch.StartNew();
                while (readDeadline.Elapsed < TimeSpan.FromSeconds(15))
                {
                    values = await host.ReadTagsAsync(new[] { triggerTag, frameReadyTag, countTag, imageUrlTag }, ct)
                                       .ConfigureAwait(false);

                    TagValue? ready = values.FirstOrDefault(v => v.TagId == frameReadyTag.Id);
                    if (ready?.Value is true)
                    {
                        break;
                    }

                    await Task.Delay(100, ct).ConfigureAwait(false);
                }

                Expect(values is not null, "宿主侧应能读到 Tag 快照");
                if (values is not null)
                {
                    TagValue ready = values.First(v => v.TagId == frameReadyTag.Id);
                    TagValue count = values.First(v => v.TagId == countTag.Id);
                    TagValue url = values.First(v => v.TagId == imageUrlTag.Id);

                    Expect(ready.Value is true && ready.Quality == Quality.Good,
                        $"cam/frame-ready 应被宿主解析为 bool true（实际 {ready.Value} / {ready.Quality}）");
                    Expect(count.Value is 3 && count.Quality == Quality.Good,
                        $"cam/trigger-count 应被宿主解析为 int 3（实际 {count.Value} / {count.Quality}）");
                    Expect(url.Value as string == imagePath,
                        $"cam/locate-image-url 应逐字传过去（实际 {url.Value}）");
                }

                // ── 7) 反方向：宿主写 cam/trigger → 设备侧收到（这是上位机触发相机的唯一通路）──
                await host.WriteTagAsync(triggerTag, true, ct).ConfigureAwait(false);

                DataPointReceivedEventArgs? receivedTrigger = null;
                var triggerWait = Stopwatch.StartNew();
                while (triggerWait.Elapsed < TimeSpan.FromSeconds(10) && receivedTrigger is null)
                {
                    receivedTrigger = Snapshot().FirstOrDefault(r => r.Address == "cam/trigger");
                    if (receivedTrigger is null)
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                }

                Expect(receivedTrigger is not null,
                    "宿主写 cam/trigger 后设备侧必须收到（主题不一致时这里会静默失败）");
                Expect(receivedTrigger is not null
                       && receivedTrigger.Payload.Trim().Trim('"')
                                          .Equals("true", StringComparison.OrdinalIgnoreCase),
                    $"设备侧收到的触发载荷应等价于 true（实际 {receivedTrigger?.Payload}）");

                // ── 8) 断线自愈 + 重连后补订 ──
                await broker.StopAsync().ConfigureAwait(false);

                var offline = Stopwatch.StartNew();
                while (offline.Elapsed < TimeSpan.FromSeconds(10) && device.IsConnected)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }

                Expect(!device.IsConnected, "broker 停掉后设备侧应变为未连接");

                lock (received)
                {
                    received.Clear();
                }

                await broker.RestartAsync(ct).ConfigureAwait(false);

                var reconnect = Stopwatch.StartNew();
                while (reconnect.Elapsed < TimeSpan.FromSeconds(30) && !device.IsConnected)
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }

                Expect(device.IsConnected, "broker 回来后设备侧应自动重连（不能靠人工重启程序）");

                if (device.IsConnected)
                {
                    // 见证者：另一个设备侧连接，往 cam/trigger 写一条 ——
                    // 只有设备侧真的补订了，它才收得到（clean session 下订阅不会自己回来）
                    var witness = new MqttDeviceDriver(
                        new MqttDeviceLink(endpoint, ClientId: "SelfCheckWitness"), log);

                    await witness.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                    await witness.PublishAsync("cam/trigger", "true", ct).ConfigureAwait(false);

                    var resub = Stopwatch.StartNew();
                    while (resub.Elapsed < TimeSpan.FromSeconds(10) && Snapshot().Count == 0)
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }

                    Expect(Snapshot().Count > 0,
                        "重连后必须自动补订原主题：否则连接正常但一条消息都收不到");
                    Expect(
                        log.Messages.Any(m => m.Contains("已重连成功")),
                        "重连成功必须留痕（现场靠它判断是网络问题还是程序问题）");

                    await witness.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"自检过程抛异常：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // ── 9) 释放后的语义 ──
                try
                {
                    await device.DisposeAsync().ConfigureAwait(false);

                    Expect(
                        await ThrowsAsync(() => device.PublishAsync("cam/frame-ready", true, CancellationToken.None))
                            .ConfigureAwait(false),
                        "释放之后发布应抛 ObjectDisposedException");
                }
                catch (Exception ex)
                {
                    failures.Add($"释放设备侧驱动时抛异常：{ex.GetType().Name}: {ex.Message}");
                }
            }

            return failures;
        }

        private static async Task<bool> ThrowsAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>让 OS 分配一个空闲端口（拿完即释放，给进程内 broker 用）。</summary>
        private static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>进程内 MQTT broker：让两端真的走一遍 TCP 上的 MQTT，而不是假装。</summary>
        private sealed class InProcessBroker : IAsyncDisposable
        {
            private readonly int _port;
            private readonly MqttServerOptions _options;
            private MqttServer? _server;

            public InProcessBroker(int port)
            {
                _port = port;
                _options = new MqttServerOptionsBuilder()
                    .WithDefaultEndpoint()
                    .WithDefaultEndpointPort(port)
                    .Build();
            }

            public Task StartAsync(CancellationToken ct) => StartCoreAsync();

            public Task RestartAsync(CancellationToken ct) => StartCoreAsync();

            private async Task StartCoreAsync()
            {
                // 端口刚从 TIME_WAIT 里放出来时可能短暂占用，重试几次即可
                Exception? last = null;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        _server = new MqttFactory().CreateMqttServer(_options);
                        await _server.StartAsync().ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        await Task.Delay(200).ConfigureAwait(false);
                    }
                }

                throw new InvalidOperationException($"进程内 MQTT broker 无法在端口 {_port} 启动", last);
            }

            public async Task StopAsync()
            {
                if (_server is null)
                {
                    return;
                }

                await _server.StopAsync().ConfigureAwait(false);
                _server.Dispose();
                _server = null;
            }

            public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
        }

        /// <summary>把日志收进内存的日志服务（自检要断言"该留痕的留了痕"）。</summary>
        private sealed class RecordingLogService : ILogService
        {
            private readonly List<string> _messages;

            public RecordingLogService(string name = "MqttDeviceDriverSelfCheck", List<string>? messages = null)
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
}

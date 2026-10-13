using Core.Acquisition;
using Core.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Plugins.Drivers.Mqtt
{
    /// <summary>
    /// 设备侧的 MQTT 驱动：把本机（相机）当成资源树里的一台 MQTT 设备，
    /// <b>按数据点地址收发</b>——地址就是 MQTT 主题，不加任何前缀。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>与宿主侧驱动的分工：</b>宿主（采集宿主 / 上位机）用 <see cref="Core.Interfaces.IProtocolDriver"/>
    /// 按资源树里的 Tag 批量读写；本类跑在设备侧，只认"地址 → 值"。两侧共用一条 MQTT 线，
    /// 因此<b>主题必须逐字相同</b>：宿主是按 <c>TagPoint.Address</c> 直接订阅的
    /// （见 <see cref="MqttDriver.ReadTagsAsync"/>），这里若自作主张加 <c>相机名/</c> 前缀，
    /// 宿主就永远收不到帧、也永远触发不了相机，而两端都不会报错。
    /// 同 broker 上要区分设备，靠的是<b>每台相机连自己的 broker</b>（见 cameras.json 的 mqtt.endpoint）。
    /// </para>
    /// <para>
    /// <b>载荷是纯文本</b>（见 <see cref="Encode"/>），与宿主侧 <c>MqttDriver.Parse</c> 的
    /// <c>TagPoint.DataType</c> 解析一一对应：bool 写小写 <c>true</c>/<c>false</c>，
    /// 数字用 InvariantCulture，枚举写小写名（资源树的 enum 值就是 <c>hardware</c>/<c>software</c> 这种）。
    /// 千万不要改成 JSON 而不改宿主——两边的解析是对着的。
    /// </para>
    /// <para>
    /// <b>断开自愈：</b>非人为断开（broker 重启、网线松动）会拉起后台重连循环，指数退避到 30 秒封顶；
    /// 重连成功后会<b>把登记过的主题全部重新订阅</b>——连接用的是 clean session，
    /// 不补订的话表现是"连接正常但一条消息都收不到"，最容易误判成上位机没发。
    /// </para>
    /// <para>
    /// <b>未连接时的语义：</b><see cref="PublishAsync"/> 直接抛（调用方留痕，不静默丢数据）；
    /// <see cref="SubscribeAsync"/> 只登记并告警，重连后自动补订（订阅比发布更该"等一等"）。
    /// </para>
    /// </remarks>
    public sealed class MqttDeviceDriver : IDeviceDriver
    {
        /// <summary>驱动类型键（与宿主侧 MQTT 驱动同名，日志里一致）。</summary>
        public const string Type = "MQTT";

        /// <summary>首次重连等待；之后每次翻倍。</summary>
        private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);

        /// <summary>重连等待上限。</summary>
        private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

        private readonly MqttDeviceLink _link;
        private readonly ILogService _log;
        private readonly IMqttClient _client;
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();

        /// <summary>已登记的主题：重连后按它补订。</summary>
        private readonly ConcurrentDictionary<string, byte> _topics = new(StringComparer.Ordinal);

        private readonly object _loopSync = new();
        private Task? _reconnectLoop;
        private string _endpoint;
        private int _reconnectAttempts;
        private volatile bool _stopping;
        private bool _disposed;

        /// <param name="link">设备侧接入配置（端点、标识、认证）</param>
        /// <param name="log">日志服务</param>
        /// <param name="client">MQTT 客户端；留空自建（自检时可注入）</param>
        public MqttDeviceDriver(MqttDeviceLink link, ILogService log, IMqttClient? client = null)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
            _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<MqttDeviceDriver>();

            if (string.IsNullOrWhiteSpace(_link.ClientId))
            {
                // 同一 broker 上标识相同的两个客户端会互相顶掉连接，且不会有明确报错——宁可拒绝启动
                throw new InvalidOperationException("设备侧 MQTT 必须有客户端标识（ClientId），不能为空");
            }

            _client = client ?? new MqttFactory().CreateMqttClient();
            _endpoint = _link.Endpoint;

            _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
            _client.DisconnectedAsync += OnDisconnectedAsync;
        }

        /// <inheritdoc />
        public string DriverType => Type;

        /// <inheritdoc />
        public bool IsConnected => _client.IsConnected;

        /// <inheritdoc />
        public string EndPoint => _endpoint;

        /// <summary>客户端标识（诊断用；同 broker 上必须唯一）。</summary>
        public string ClientId => _link.ClientId!;

        /// <summary>已登记订阅的主题（诊断 / 自检用）。</summary>
        public IReadOnlyCollection<string> Topics => _topics.Keys.ToArray();

        /// <summary>累计重连尝试次数（诊断用；归零表示当前连接是干净的）。</summary>
        public int ReconnectAttempts => _reconnectAttempts;

        /// <inheritdoc />
        public event EventHandler<DataPointReceivedEventArgs>? DataPointReceived;

        /// <inheritdoc />
        public event EventHandler<DriverErrorEventArgs>? ErrorOccurred;

        /// <inheritdoc />
        public async Task ConnectAsync(string endpoint, CancellationToken ct)
        {
            ThrowIfDisposed();

            // 端点优先用调用方给的（组合根按相机配置传），留空回落到构造时的配置
            string effective = string.IsNullOrWhiteSpace(endpoint) ? _link.Endpoint : endpoint;
            MqttEndpoint broker = ResolveBroker(effective);       // 端点/协议错 → 致命配置错
            MqttClientOptions options = BuildOptions(broker);     // 认证配置缺 → 致命配置错
            _endpoint = effective;

            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_client.IsConnected)
                {
                    return;
                }

                await _client.ConnectAsync(options, ct).ConfigureAwait(false);
                _reconnectAttempts = 0;

                _log.Info("设备侧 MQTT 已连接 {Host}:{Port}（TLS={Tls}，客户端标识 {ClientId}）",
                    broker.Host, broker.Port, broker.UseTls, ClientId);

                await ResubscribeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 连接失败不是终点：现场网络抖动、broker 重启都属正常，交给后台循环自愈；
                // 但必须往上抛一次，让调用方在启动日志里看得见"这台现在没连上"
                RaiseError($"连接 MQTT broker 失败（{_endpoint}，客户端标识 {ClientId}）", ex, isFatal: false);
                StartReconnectLoop();
                throw;
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task DisconnectAsync(CancellationToken ct)
        {
            _stopping = true;

            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync(new MqttClientDisconnectOptions(), ct).ConfigureAwait(false);
                    _log.Info("设备侧 MQTT 已断开（客户端标识 {ClientId}）", ClientId);
                }
            }
            catch (Exception ex)
            {
                // 断开失败不影响结论：连接已经没用了，记一条即可
                _log.Warn(ex, "断开 MQTT 时出错（客户端标识 {ClientId}）", ClientId);
            }
            finally
            {
                _stopping = false;
            }
        }

        /// <inheritdoc />
        public async Task SubscribeAsync(string address, CancellationToken ct)
        {
            ThrowIfDisposed();
            ArgumentException.ThrowIfNullOrWhiteSpace(address);

            bool isNew = _topics.TryAdd(address, 0);

            if (!_client.IsConnected)
            {
                if (isNew)
                {
                    _log.Warn("MQTT 未连接，主题 {Topic} 已登记，连接后会自动补订", address);
                }

                return;
            }

            if (!isNew)
            {
                return;
            }

            await SubscribeTopicAsync(address, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task UnsubscribeAsync(string address, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(address);
            _topics.TryRemove(address, out _);

            if (!_client.IsConnected)
            {
                return;
            }

            try
            {
                await _client.UnsubscribeAsync(
                    new MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(address).Build(), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "退订主题 {Topic} 失败（忽略，重连后按登记表为准）", address);
            }
        }

        /// <inheritdoc />
        public async Task PublishAsync(string address, object value, CancellationToken ct)
        {
            ThrowIfDisposed();
            ArgumentException.ThrowIfNullOrWhiteSpace(address);

            if (!_client.IsConnected)
            {
                // 不静默丢弃：设备上报的数据点（帧就绪、图像 URL、触发计数）丢了必须让人看见
                throw new InvalidOperationException(
                    $"MQTT 未连接，无法发布主题 {address}（端点 {_endpoint}，客户端标识 {ClientId}）");
            }

            string payload = Encode(value);

            await _client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(address)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(),
                ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping = true;
            _lifetime.Cancel();

            Task? loop;
            lock (_loopSync)
            {
                loop = _reconnectLoop;
            }

            if (loop is not null)
            {
                // 重连循环可能正在等退避或正在连接，给它一点收尾时间，别让它碰到已释放的客户端
                try
                {
                    await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 超时/取消都无所谓：下面直接释放客户端
                }
            }

            _client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
            _client.DisconnectedAsync -= OnDisconnectedAsync;

            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None)
                               .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "释放 MQTT 驱动时断开失败（忽略）");
            }

            _client.Dispose();
            _connectGate.Dispose();
            _lifetime.Dispose();
        }

        /// <summary>
        /// 把值编成 MQTT 载荷（纯文本，与宿主侧的 TagPoint.DataType 解析对应）。
        /// </summary>
        /// <remarks>
        /// 规则：null → 空串；bool → 小写 <c>true</c>/<c>false</c>；
        /// 枚举 → 小写名（资源树 enum 值即此形式）；数字/日期 → InvariantCulture；
        /// 其余 → <see cref="Convert.ToString(object?, IFormatProvider?)"/>。
        /// </remarks>
        public static string Encode(object? value) => value switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            string s => s,
            Enum e => e.ToString().ToLowerInvariant(),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

        /// <summary>解析端点并按配置强制 TLS。</summary>
        private MqttEndpoint ResolveBroker(string endpoint)
        {
            MqttEndpoint broker = MqttEndpoint.Parse(endpoint);
            return _link.UseTls && !broker.UseTls ? broker with { UseTls = true } : broker;
        }

        /// <summary>构造客户端选项；认证配置自相矛盾时抛（属致命配置错，重试也不会好）。</summary>
        private MqttClientOptions BuildOptions(MqttEndpoint broker)
        {
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(broker.Host, broker.Port)
                .WithClientId(ClientId)
                .WithCleanSession()
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .WithTimeout(TimeSpan.FromSeconds(10));

            bool hasUser = !string.IsNullOrWhiteSpace(_link.Username);
            bool hasPasswordVariable = !string.IsNullOrWhiteSpace(_link.PasswordEnvironmentVariable);

            if (hasUser && !hasPasswordVariable)
            {
                throw new InvalidOperationException(
                    $"设备侧 MQTT 配了用户名 {_link.Username} 却没配密码环境变量名：配置文件只保存环境变量名，不保存密码明文");
            }

            if (!hasUser && hasPasswordVariable)
            {
                throw new InvalidOperationException(
                    $"设备侧 MQTT 配了密码环境变量 {_link.PasswordEnvironmentVariable} 却没配用户名：密码无处使用，配置自相矛盾");
            }

            if (hasUser)
            {
                string? password = Environment.GetEnvironmentVariable(_link.PasswordEnvironmentVariable!);
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException(
                        $"未设置 MQTT 密码环境变量“{_link.PasswordEnvironmentVariable}”（当前进程环境里为空）");
                }

                builder.WithCredentials(_link.Username, password);
            }

            if (broker.UseTls)
            {
                builder.WithTlsOptions(options => options.UseTls());
            }

            return builder.Build();
        }

        /// <summary>按登记表补订全部主题（重连后必做：clean session 会丢订阅）。</summary>
        private async Task ResubscribeAsync(CancellationToken ct)
        {
            if (_topics.IsEmpty)
            {
                return;
            }

            foreach (string topic in _topics.Keys)
            {
                await SubscribeTopicAsync(topic, ct).ConfigureAwait(false);
            }
        }

        private async Task SubscribeTopicAsync(string topic, CancellationToken ct)
        {
            await _client.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(filter => filter.WithTopic(topic))
                    .Build(),
                ct).ConfigureAwait(false);

            _log.Info("已订阅主题 {Topic}", topic);
        }

        /// <summary>非人为断开 → 拉起后台重连。</summary>
        private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
        {
            if (_disposed || _stopping)
            {
                return Task.CompletedTask;
            }

            _log.Warn("MQTT 连接断开（客户端标识 {ClientId}，断开前是否在线={WasConnected}）：{Reason}；后台自动重连",
                ClientId, args.ClientWasConnected, args.Exception?.Message ?? "无异常信息");

            StartReconnectLoop();
            return Task.CompletedTask;
        }

        private void StartReconnectLoop()
        {
            if (_disposed || _stopping)
            {
                return;
            }

            lock (_loopSync)
            {
                if (_reconnectLoop is { IsCompleted: false })
                {
                    return;      // 已经有一个在跑，别叠罗汉
                }

                _reconnectLoop = Task.Run(ReconnectLoopAsync);
            }
        }

        /// <summary>指数退避重连；成功即补订全部主题并退出循环。</summary>
        private async Task ReconnectLoopAsync()
        {
            TimeSpan delay = InitialReconnectDelay;

            while (!_disposed && !_lifetime.IsCancellationRequested && !_client.IsConnected)
            {
                try
                {
                    await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    MqttEndpoint broker = ResolveBroker(_endpoint);
                    MqttClientOptions options = BuildOptions(broker);

                    await _connectGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        if (_client.IsConnected)
                        {
                            return;
                        }

                        await _client.ConnectAsync(options, _lifetime.Token).ConfigureAwait(false);
                        _reconnectAttempts = 0;
                        _log.Info("MQTT 已重连成功 {Host}:{Port}（客户端标识 {ClientId}）",
                            broker.Host, broker.Port, ClientId);

                        await ResubscribeAsync(_lifetime.Token).ConfigureAwait(false);
                        return;
                    }
                    finally
                    {
                        _connectGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _reconnectAttempts++;
                    RaiseError($"MQTT 第 {_reconnectAttempts} 次重连失败（{_endpoint}）", ex, isFatal: false);

                    // 网络恢复可能很久，退避到上限就固定住，避免每 30 秒刷屏之外的额外噪声
                    delay = delay >= MaxReconnectDelay
                        ? MaxReconnectDelay
                        : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
                }
            }
        }

        private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                string topic = args.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);

                DataPointReceived?.Invoke(this, new DataPointReceivedEventArgs(topic, payload));
            }
            catch (Exception ex)
            {
                // 订阅者抛异常不能逃回 MQTTnet：那会打断客户端内部循环，表现为"收几条之后就不收了"
                _log.Error(ex, "处理 MQTT 消息时订阅者抛异常，已忽略该条消息");
            }

            return Task.CompletedTask;
        }

        private void RaiseError(string message, Exception? cause, bool isFatal)
        {
            if (cause is null)
            {
                _log.Error("{Message}（致命={Fatal}）", message, isFatal);
            }
            else
            {
                _log.Error(cause, "{Message}（致命={Fatal}）", message, isFatal);
            }

            try
            {
                ErrorOccurred?.Invoke(this, new DriverErrorEventArgs(DriverType, message, isFatal));
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "ErrorOccurred 订阅者抛异常，已忽略");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>按相机配置创建设备侧 MQTT 驱动（组合根注册成 <c>Func&lt;CameraSetup, ILogService, IDeviceDriver&gt;</c>）。</summary>
    public static class MqttDeviceDriverFactory
    {
        /// <summary>建一台相机的设备侧驱动。</summary>
        /// <exception cref="InvalidOperationException">该相机没配 mqtt 段</exception>
        public static IDeviceDriver Create(CameraSetup setup, ILogService log)
        {
            ArgumentNullException.ThrowIfNull(setup);

            if (setup.Mqtt is null)
            {
                throw new InvalidOperationException(
                    $"相机 {setup.Config.CameraName} 没有 mqtt 段，无法作为 MQTT 设备接入（见 config/cameras.json）");
            }

            return new MqttDeviceDriver(setup.Mqtt, log);
        }
    }
}

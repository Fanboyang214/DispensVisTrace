namespace Core.Acquisition;

/// <summary>
/// MQTT broker 端点：把配置里的 <c>mqtt://host:port</c> 解析成连得上东西的三元组。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要单独一个类型：</b>设备定义里写的是一个 URI 字符串，而 MQTTnet 要的是 host + port +
/// 是否 TLS 三个值。解析规则散在驱动里就会出现"一处默认 1883、一处默认 8883、TLS 靠字符串 contains 判断"
/// 这类只在现场才暴露的问题，所以规则集中在这里，且可离线断言。
/// </para>
/// <para>接受的写法：<c>mqtt://host:1883</c>、<c>mqtts://host</c>、<c>host:1883</c>、<c>host</c>。</para>
/// </remarks>
public sealed record MqttEndpoint(string Host, int Port, bool UseTls)
{
    /// <summary>明文 MQTT 默认端口。</summary>
    public const int DefaultPort = 1883;

    /// <summary>TLS MQTT 默认端口。</summary>
    public const int DefaultTlsPort = 8883;

    /// <summary>解析端点；不合法时抛出带具体原因的异常（配置错必须当场可见）。</summary>
    /// <exception cref="InvalidOperationException">端点为空或格式不合法</exception>
    public static MqttEndpoint Parse(string? endpoint)
    {
        if (!TryParse(endpoint, out MqttEndpoint? parsed, out string? error))
        {
            throw new InvalidOperationException(error);
        }

        return parsed!;
    }

    /// <summary>
    /// 尝试解析端点。
    /// </summary>
    /// <param name="endpoint">配置里的端点字符串</param>
    /// <param name="result">解析结果</param>
    /// <param name="error">失败原因（成功时为 null）</param>
    public static bool TryParse(string? endpoint, out MqttEndpoint? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            error = "MQTT 端点为空：必须写成 mqtt://主机:端口（如 mqtt://192.168.1.40:1883）";
            return false;
        }

        string text = endpoint.Trim();

        // 不写 scheme 时按明文 mqtt 处理：现场常见 "192.168.1.40:1883"，
        // 而 Uri 会把 "192.168.1.40" 当成 scheme，必须先补上
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "mqtt://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
        {
            error = $"MQTT 端点格式不合法：{endpoint}（应形如 mqtt://主机:端口）";
            return false;
        }

        bool tls = uri.Scheme.ToLowerInvariant() switch
        {
            "mqtt" or "tcp" => false,
            "mqtts" or "ssl" => true,
            _ => false
        };

        if (uri.Scheme.ToLowerInvariant() is not ("mqtt" or "tcp" or "mqtts" or "ssl"))
        {
            error = $"MQTT 端点协议不支持：{uri.Scheme}（只支持 mqtt:// 或 mqtts://）";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = $"MQTT 端点没有主机名：{endpoint}";
            return false;
        }

        // uri.Port 在未写端口时会回落到 scheme 默认口（mqtt=1883），这里显式表达意图
        int port = uri.IsDefaultPort ? (tls ? DefaultTlsPort : DefaultPort) : uri.Port;

        result = new MqttEndpoint(uri.Host, port, tls);
        return true;
    }
}

/// <summary>
/// 一台设备（这里是相机）作为 MQTT <b>设备侧</b>接入 broker 的配置。
/// </summary>
/// <remarks>
/// <para>
/// 角色分工：设备侧用 <see cref="Core.Acquisition.IDeviceDriver"/> 发布自己的数据点、并订阅别人写进来的点
/// （如 <c>cam/trigger</c>）；宿主侧用 <c>Core.Interfaces.IProtocolDriver</c> 按资源树的 Tag 订阅这些点。
/// 两侧共用一条 MQTT 线，但抽象不同：设备侧按"地址"收发，宿主侧按 Tag 批量读写。
/// </para>
/// <para>
/// 密码只存<b>环境变量名</b>，不存明文（与宿主侧 <c>DeviceConfig</c> 同一规则）。
/// </para>
/// </remarks>
/// <param name="Endpoint">broker 端点，如 <c>mqtt://192.168.1.40:1883</c></param>
/// <param name="ClientId">
/// 客户端标识。加载器在留空时填成相机名：同一 broker 上两台相机用同一个标识会被 broker 互相顶掉连接，
/// 表现是"随机一台掉线"，所以驱动层要求它非空，不给默认值。
/// </param>
/// <param name="Username">用户名；留空为匿名</param>
/// <param name="PasswordEnvironmentVariable">保存密码的环境变量名；配了用户名就必须配它</param>
/// <param name="UseTls">强制 TLS；端点写成 mqtts:// 时自动为 true</param>
public sealed record MqttDeviceLink(
    string Endpoint,
    string? ClientId = null,
    string? Username = null,
    string? PasswordEnvironmentVariable = null,
    bool UseTls = false)
{
    /// <summary>解析端点；<see cref="UseTls"/> 为 true 时强制走 TLS。</summary>
    public MqttEndpoint ParseEndpoint()
    {
        MqttEndpoint endpoint = MqttEndpoint.Parse(Endpoint);
        return UseTls && !endpoint.UseTls ? endpoint with { UseTls = true } : endpoint;
    }
}

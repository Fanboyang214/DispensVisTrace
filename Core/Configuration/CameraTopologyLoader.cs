using Core.Acquisition;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core.Configuration
{
    /// <summary>
    /// cameras.json 加载器：把"本机装了几台相机、图落到哪、各拍什么"读成 <see cref="CameraTopology"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>文件不存在 → 返回 null</b>（本机没配相机，相机通道不启动）。
    /// 刻意不返回"空拓扑"：调用方必须显式处理"没配"，而不是把"没配"和"配了但为空"混成一个状态。
    /// </para>
    /// <para>
    /// <b>文件存在但内容不合法 → 抛异常</b>。配置错必须当场失败：
    /// 静静少连一台相机会在现场表现成"某台相机不出图"，而那时没人会想到是相机名或目录写错了。
    /// </para>
    /// <para>JSON 用 camelCase，枚举写成名字（<c>"Locate"</c> / <c>"Hardware"</c>），与仓库其它配置一致。</para>
    /// </remarks>
    public static class CameraTopologyLoader
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        /// <summary>约定路径：exe 同目录 <c>config/cameras.json</c>。</summary>
        public static string DefaultPath(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                throw new ArgumentException("基准目录不能为空", nameof(baseDirectory));
            }

            return Path.Combine(baseDirectory, "config", "cameras.json");
        }

        /// <summary>
        /// 从文件加载。
        /// </summary>
        /// <param name="filePath">cameras.json 路径</param>
        /// <returns>拓扑；文件不存在时为 null</returns>
        /// <exception cref="InvalidOperationException">文件存在但内容不合法</exception>
        public static CameraTopology? TryLoadFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("配置文件路径不能为空", nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                return null;
            }

            string json = File.ReadAllText(filePath);

            try
            {
                return FromJson(json, Path.GetFileName(filePath));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{Path.GetFileName(filePath)} 解析失败：{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从 JSON 文本加载（自检 / 单测用，不碰磁盘）。
        /// </summary>
        /// <param name="json">cameras.json 的内容</param>
        /// <param name="sourceName">出错信息里显示的文件名</param>
        public static CameraTopology FromJson(string json, string sourceName = "cameras.json")
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ArgumentException("JSON 内容不能为空", nameof(json));
            }

            CameraTopologyDto dto = JsonSerializer.Deserialize<CameraTopologyDto>(json, Options)
                ?? throw new InvalidOperationException($"{sourceName} 内容为空");

            if (string.IsNullOrWhiteSpace(dto.ImageRootDirectory))
            {
                throw new InvalidOperationException(
                    $"{sourceName} 未配置 imageRootDirectory：图像共享目录是必填项（双相机必须落到同一个目录，视觉与上位机都按它取图）");
            }

            var setups = new List<CameraSetup>(dto.Cameras.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (CameraDto camera in dto.Cameras)
            {
                if (string.IsNullOrWhiteSpace(camera.CameraName))
                {
                    throw new InvalidOperationException($"{sourceName} 里有一台相机没写 cameraName");
                }

                if (string.IsNullOrWhiteSpace(camera.SdkType))
                {
                    throw new InvalidOperationException(
                        $"{sourceName} 里 {camera.CameraName} 没写 sdkType（如 Simulation / HikRobot），无法决定用哪个驱动");
                }

                // 同名相机会在 MQTT 主题、日志来源、落盘文件名上互相覆盖，且不会有任何报错——必须当场拦住
                if (!seen.Add(camera.CameraName))
                {
                    throw new InvalidOperationException($"{sourceName} 里 cameraName 重复：{camera.CameraName}");
                }

                setups.Add(new CameraSetup(
                    new CameraConfig(
                        CameraName: camera.CameraName,
                        SdkType: camera.SdkType,
                        SerialNumber: camera.SerialNumber,
                        TriggerSource: camera.TriggerSource,
                        ExposureTimeUs: camera.ExposureTimeUs,
                        GainDb: camera.GainDb,
                        Width: camera.Width,
                        Height: camera.Height,
                        FrameRate: camera.FrameRate),
                    camera.Role,
                    BuildMqttLink(camera, sourceName)));
            }

            return new CameraTopology(new ImageStorageOptions(dto.ImageRootDirectory), setups, dto.AutoStart);
        }

        /// <summary>把相机的 mqtt 段变成设备侧接入配置；没写就返回 null（这台相机不上报）。</summary>
        private static MqttDeviceLink? BuildMqttLink(CameraDto camera, string sourceName)
        {
            MqttDto? mqtt = camera.Mqtt;
            if (mqtt is null)
            {
                return null;
            }

            // 端点写错会让这台相机永远连不上 broker，且现场只表现为"上位机读不到数据"——必须启动阶段拦住
            if (!MqttEndpoint.TryParse(mqtt.Endpoint, out MqttEndpoint? _, out string? error))
            {
                throw new InvalidOperationException(
                    $"{sourceName} 里 {camera.CameraName} 的 mqtt.endpoint 不合法：{error}");
            }

            // 与宿主侧 DeviceConfig 同一规则：配了用户名就必须知道密码存在哪个环境变量里，
            // 否则会在运行时变成"连接被拒"这种查不到根因的故障
            if (!string.IsNullOrWhiteSpace(mqtt.Username)
                && string.IsNullOrWhiteSpace(mqtt.PasswordEnvironmentVariable))
            {
                throw new InvalidOperationException(
                    $"{sourceName} 里 {camera.CameraName} 配了 mqtt.username 却没配 mqtt.passwordEnvironmentVariable：" +
                    "配置文件只保存密码的环境变量名，不保存密码明文");
            }

            return new MqttDeviceLink(
                Endpoint: mqtt.Endpoint.Trim(),
                // 留空就填相机名：客户端标识为空会被 broker 拒绝或与别的进程撞车，不留空是硬要求
                ClientId: string.IsNullOrWhiteSpace(mqtt.ClientId) ? camera.CameraName : mqtt.ClientId,
                Username: string.IsNullOrWhiteSpace(mqtt.Username) ? null : mqtt.Username,
                PasswordEnvironmentVariable: string.IsNullOrWhiteSpace(mqtt.PasswordEnvironmentVariable)
                    ? null
                    : mqtt.PasswordEnvironmentVariable,
                UseTls: mqtt.UseTls);
        }

        /// <summary>JSON 根对象。</summary>
        private sealed class CameraTopologyDto
        {
            /// <summary>图像共享目录（本地路径或 UNC）。双相机写同一处。</summary>
            public string ImageRootDirectory { get; set; } = string.Empty;

            /// <summary>是否随应用启动自动连接并 Armed。</summary>
            public bool AutoStart { get; set; } = true;

            public List<CameraDto> Cameras { get; set; } = new();
        }

        /// <summary>JSON 里的单台相机。</summary>
        private sealed class CameraDto
        {
            public string CameraName { get; set; } = string.Empty;

            /// <summary>产线角色：Locate（定位）/ Inspect（检测）。</summary>
            public CameraRole Role { get; set; } = CameraRole.Locate;

            /// <summary>驱动类型：Simulation / HikRobot（MVS）。</summary>
            public string SdkType { get; set; } = string.Empty;

            public string SerialNumber { get; set; } = string.Empty;

            public TriggerSource TriggerSource { get; set; } = TriggerSource.Hardware;

            public double ExposureTimeUs { get; set; }

            public double GainDb { get; set; }

            public int Width { get; set; }

            public int Height { get; set; }

            /// <summary>自由运行模式下的帧率；不用时为 null。</summary>
            public double? FrameRate { get; set; }

            /// <summary>设备侧 MQTT 接入配置；不写 = 这台相机不往资源树上报。</summary>
            public MqttDto? Mqtt { get; set; }
        }

        /// <summary>JSON 里的 mqtt 段。</summary>
        private sealed class MqttDto
        {
            /// <summary>broker 端点，如 mqtt://192.168.1.40:1883。</summary>
            public string Endpoint { get; set; } = string.Empty;

            /// <summary>客户端标识；留空用相机名。</summary>
            public string? ClientId { get; set; }

            public string? Username { get; set; }

            /// <summary>保存密码的环境变量名（不存明文）。</summary>
            public string? PasswordEnvironmentVariable { get; set; }

            public bool UseTls { get; set; }
        }
    }
}

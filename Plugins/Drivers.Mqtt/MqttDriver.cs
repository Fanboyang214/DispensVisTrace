using Core.Interfaces;
using Core.Models;
using MQTTnet;
using MQTTnet.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Plugins.Drivers.Mqtt
{
    public sealed class MqttDriver:IProtocolDriver
    {
        private readonly DeviceConfig _config;
        private readonly IMqttClient _client;
        private readonly ConcurrentDictionary<string, string> _lastestValues = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _subscriptions = new(StringComparer.Ordinal);

        public MqttDriver(DeviceConfig config)
        {
            _config = config;
            _client = new MqttFactory().CreateMqttClient();
            _client.ApplicationMessageReceivedAsync += args =>
            {
                _lastestValues[args.ApplicationMessage.Topic] = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
                return Task.CompletedTask;
            };
        }

        public string DriverType => "MQTT";
        public bool IsConnected => _client.IsConnected;

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_client.IsConnected) return;

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.IpAddress, _config.Port > 0 ? _config.Port : 1883)
                .WithClientId(string.IsNullOrWhiteSpace(_config.MqttClientId) ? $"DispensVisTrace-{_config.Id}" : _config.MqttClientId)
                .WithCleanSession();
            if (!string.IsNullOrWhiteSpace(_config.MqttUsername))
            {
                if (string.IsNullOrWhiteSpace(_config.MqttPasswordEnvironmentVariable))
                    throw new InvalidOperationException("MQTT用户认证必须配置密码环境变量名称。");
                var password = Environment.GetEnvironmentVariable(_config.MqttPasswordEnvironmentVariable);
                if (string.IsNullOrEmpty(password))
                    throw new IndexOutOfRangeException($"未设置MQTT密码环境变量‘{_config.MqttPasswordEnvironmentVariable}’。");
                builder.WithCredentials(_config.MqttUsername, password);
            

            
            }
            if (_config.MqttUseTls)
                builder.WithTlsOptions(options => options.UseTls());
            await _client.ConnectAsync(builder.Build(), ct);


        }

        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            if (_client.IsConnected) await _client.DisconnectAsync(new MqttClientDisconnectOptions(),ct);
        }



        public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IEnumerable<TagPoint> tags, CancellationToken ct = default)
        {
            var tagList = tags.ToList();
            var newTopics = tagList.Where(tag => tag.Access != TagAccess.Write && _subscriptions.TryAdd(tag.Address, 0))
                                    .Select(tag => tag.Address).Where(topic => !string.IsNullOrWhiteSpace(topic)).ToArray();
            foreach(var topic in newTopics)
            {
                var options = new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter => filter.WithTopic(topic)).Build();
                await _client.SubscribeAsync(options, ct);
            }

            var now = DateTimeOffset.UtcNow;
            return tagList.Where(tag => tag.Access != TagAccess.Write).Select(tag =>
            {
                var hasValue = _lastestValues.TryGetValue(tag.Address, out var payload);
                return new TagValue { TagId = tag.Id, TagName = tag.Name, Value = hasValue ? Parse(payload!, tag.DataType) : null, Quality = hasValue ? Quality.Good : Quality.Uncertain, Timestamp = now, DataType = MapType(tag.DataType) };
            }).ToArray();


        }

       

        public async Task WriteTagAsync(TagPoint tag, object value, CancellationToken ct = default)
        {

            if (!_client.IsConnected) throw new InvalidOperationException("MQTT客户端未连接。");
            var payload = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            await _client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(tag.Address).WithPayload(payload).Build(), ct);
        }
        
        
        public ValueTask DisposeAsync()
        {
            return new(DisconnectAsync());
        }

        private static object? Parse(string payload, TagDataType dataType) => dataType switch
        {
            TagDataType.Bool => bool.TryParse(payload, out var value) ? value : payload is "1" or "on" or "ON",
            TagDataType.Int16 => short.Parse(payload, CultureInfo.InvariantCulture),
            TagDataType.Int32 => int.Parse(payload, CultureInfo.InvariantCulture),
            TagDataType.Int64 => long.Parse(payload, CultureInfo.InvariantCulture),
            TagDataType.UInt16 => ushort.Parse(payload, CultureInfo.InvariantCulture),
            TagDataType.UInt32 => uint.Parse(payload, CultureInfo.InvariantCulture),
            TagDataType.Float32 => float.Parse(payload, CultureInfo.InvariantCulture),
            TagDataType.Float64 => double.Parse(payload, CultureInfo.InvariantCulture),
            _ => payload
        };

        private static Type MapType(TagDataType dataType) => dataType switch
        {
            TagDataType.Bool => typeof(bool),
            TagDataType.Int16 => typeof(short),
            TagDataType.Int32 => typeof(int),
            TagDataType.Int64 => typeof(long),
            TagDataType.UInt16 => typeof(ushort),
            TagDataType.UInt32 => typeof(uint),
            TagDataType.Float32 => typeof(float),
            TagDataType.Float64 => typeof(double),
            _ => typeof(string)
        };

    }
}

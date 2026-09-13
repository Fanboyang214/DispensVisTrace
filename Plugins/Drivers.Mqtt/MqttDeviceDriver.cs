using System.Text;
using Core.Acquisition;
using MQTTnet;
using MQTTnet.Client;

namespace Plugins.Drivers.Mqtt;

public sealed class MqttDeviceDriver : IDeviceDriver
{
    private readonly IMqttClient _client;
    private MqttClientOptions? _options;
    private string _endpoint = string.Empty;

    public string DriverType => "Drivers.Mqtt";
    public string Endpoint => _endpoint;
    public bool IsConnected => _client.IsConnected;

    public event EventHandler<DataPointReceivedEventArgs>? DataPointReceived;
    public event EventHandler<DriverErrorEventArgs>? ErrorOccurred;

    public MqttDeviceDriver()
    {
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();
    }

    public async Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        _endpoint = endpoint;
        var uri = new Uri(endpoint);

        _options = new MqttClientOptionsBuilder()
            .WithTcpServer(uri.Host, uri.Port > 0 ? uri.Port : 1883)
            .WithClientId($"drivers-mqtt-{Guid.NewGuid():N}")
            .WithCleanSession()
            .Build();

        _client.ApplicationMessageReceivedAsync += OnMessageReceived;
        _client.DisconnectedAsync += OnDisconnected;

        await _client.ConnectAsync(_options, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        _client.ApplicationMessageReceivedAsync -= OnMessageReceived;
        
        if (_client.IsConnected)
            await _client.DisconnectAsync(cancellationToken: ct);
        _client.DisconnectedAsync -= OnDisconnected;

    }

    public Task SubscribeAsync(string address, CancellationToken ct)
    {
        return _client.SubscribeAsync(
            new MqttTopicFilterBuilder()
                .WithTopic(address)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            cancellationToken: ct);
    }

    public Task UnsubscribeAsync(string address, CancellationToken ct)
    {
        return _client.UnsubscribeAsync(address, cancellationToken: ct);
    }

    public async Task PublishAsync(string address, object value, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(value));

        await _client.PublishAsync(new MqttApplicationMessage
        {
            Topic = address,
            PayloadSegment = payload,
            QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
        }, cancellationToken: ct);
    }

    private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs args)
    {
        var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
        DataPointReceived?.Invoke(this, new DataPointReceivedEventArgs(
            args.ApplicationMessage.Topic, payload));
        return Task.CompletedTask;
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs args)
    {
        if (args.Reason != MqttClientDisconnectReason.NormalDisconnection)
        {
            ErrorOccurred?.Invoke(this, new DriverErrorEventArgs(
                DriverType, $"Disconnected: {args.Reason}", true));
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None);
        _client.Dispose();
    }
}
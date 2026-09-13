namespace Core.Acquisition;

public interface IDeviceDriver : IAsyncDisposable
{
    public string DriverType { get; }

    public bool IsConnected { get; }

    public string EndPoint { get; }

    Task ConnectAsync(string endpoint, CancellationToken ct);

    Task DisconnectAsync(CancellationToken ct);

    Task SubscribeAsync(string address, CancellationToken ct);

    Task UnsubscribeAsync(string address, CancellationToken ct);

    Task PublishAsync(string address, object value, CancellationToken ct);


    event EventHandler<DataPointReceivedEventArgs> DataPointReceived;

    event EventHandler<DriverErrorEventArgs> ErrorOccurred;
}


public record DataPointReceivedEventArgs(
    string Address,
    string Payload);

public record DriverErrorEventArgs(
    string DriverType,
    string Message,
    bool IsFatal);
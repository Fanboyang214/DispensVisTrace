namespace Core.Bridge;

public interface IDataPointPublisher
{
    void Publish(string deviceName, string dataPoint, object value);
}
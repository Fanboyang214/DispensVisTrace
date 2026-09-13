using Core.Acquisition;
using Prism.Ioc;
using Prism.Modularity;

namespace Plugins.Drivers.Mqtt;

public class MqttDriverModule : IModule
{
    public void OnInitialized(IContainerProvider containerProvider)
    {
    }

    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.Register<IDeviceDriver, MqttDeviceDriver>("Drivers.Mqtt");
    }
}
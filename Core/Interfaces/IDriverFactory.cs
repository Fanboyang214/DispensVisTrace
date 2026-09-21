using Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    /// <summary>
    /// Creates <see cref="IProtocolDriver"/> instances for a given <see cref="DeviceConfig"/>.
    /// Implementations may be extended at runtime (e.g. via plugin scanning).
    /// </summary>
    public interface IDriverFactory
    {
        /// <summary>All driver type keys currently registered in the factory.</summary>
        IReadOnlyCollection<string> RegisteredDriverTypes { get; }

        /// <summary>
        /// Create and configure a driver for the specified device.
        /// The caller owns the returned instance and must dispose it.
        /// </summary>
        Task<IProtocolDriver> CreateDriverAsync(DeviceConfig device, CancellationToken ct = default);

        /// <summary>
        /// Register a new driver type at runtime.
        /// </summary>
        /// <param name="driverType">Key (e.g. "Modbus", "OpcUA"). Must be unique.</param>
        /// <param name="factory">
        /// Factory delegate that receives a <see cref="DeviceConfig"/> and returns a new driver instance.
        /// </param>
        void RegisterDriver(string driverType, Func<DeviceConfig, CancellationToken, Task<IProtocolDriver>> factory);

        /// <summary>
        /// Remove a previously registered driver type.
        /// </summary>
        bool RemoveDriver(string driverType);
    }
}

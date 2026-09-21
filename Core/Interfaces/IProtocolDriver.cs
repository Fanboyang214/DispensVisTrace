using Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    /// <summary>
    /// Contract every protocol driver must implement.
    /// All I/O methods are async and accept a <see cref="CancellationToken"/>.
    /// </summary>
    public interface IProtocolDriver : IAsyncDisposable
    {
        /// <summary>Driver key used for factory resolution (e.g. "Modbus", "OpcUA").</summary>
        string DriverType { get; }

        /// <summary><c>true</c> when the driver has an active connection to the device.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Establish connection to the target device.
        /// Implementation must be idempotent: calling it when already connected is a no-op.
        /// </summary>
        Task ConnectAsync(CancellationToken ct = default);

        /// <summary>
        /// Gracefully close the connection.
        /// Implementation must be idempotent.
        /// </summary>
        Task DisconnectAsync(CancellationToken ct = default);

        /// <summary>
        /// Read a batch of tag values from the device.
        /// </summary>
        /// <param name="tags">Tags to read. The driver groups them into optimal bulk-reads internally.</param>
        /// <param name="ct">Cancellation token that aborts the entire read batch.</param>
        /// <returns>One <see cref="TagValue"/> per requested tag, in the same order.</returns>
        Task<IReadOnlyList<TagValue>> ReadTagsAsync(IEnumerable<TagPoint> tags, CancellationToken ct = default);

        /// <summary>
        /// Write a single value to the device.
        /// </summary>
        /// <param name="tag">Target tag point.</param>
        /// <param name="value">Value to write (type must match <paramref name="tag"/>.<see cref="TagPoint.DataType"/>).</param>
        /// <param name="ct">Cancellation token.</param>
        Task WriteTagAsync(TagPoint tag, object value, CancellationToken ct = default);
    }

}

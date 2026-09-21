using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// Real-time value snapshot for a single tag point.
    /// </summary>
    public sealed class TagValue
    {
        /// <summary>Unique tag identifier (matches <see cref="TagPoint.Id"/>).</summary>
        public string TagId { get; init; } = string.Empty;

        /// <summary>Display name of the tag point.</summary>
        public string TagName { get; init; } = string.Empty;

        /// <summary>Current value (boxed). Use <see cref="DataType"/> to unbox.</summary>
        public object? Value { get; init; }

        /// <summary>OPC-compatible quality code.</summary>
        public Quality Quality { get; init; } = Quality.Bad;

        /// <summary>UTC timestamp when the value was acquired from the device.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Expected .NET type of <see cref="Value"/> (e.g. typeof(float), typeof(bool)).</summary>
        public Type DataType { get; init; } = typeof(object);

        public override string ToString() =>
            $"[{TagName}] = {Value} ({Quality}, {Timestamp:yyyy-MM-dd HH:mm:ss.fff})";
    }
}

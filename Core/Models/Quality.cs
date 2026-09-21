using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// OPC-compatible quality codes for tag values.
    /// </summary>
    public enum Quality : byte
    {
        /// <summary>Good (0xC0) — value is reliable.</summary>
        Good = 0xC0,

        /// <summary>Uncertain (0x40) — value may be stale or estimated.</summary>
        Uncertain = 0x40,

        /// <summary>Bad (0x00) — value is invalid or unavailable.</summary>
        Bad = 0x00,

        /// <summary>Substitute (0x80) — manually overridden value.</summary>
        Substitute = 0x80
    }

}

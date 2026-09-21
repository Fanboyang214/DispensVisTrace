using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{

    /// <summary>
    /// 报警严重程度枚举。
    /// </summary>
    public enum AlarmSeverity : byte
    {
        /// <summary>信息 — 仅通知，无需操作。</summary>
        Info = 0,

        /// <summary>警告 — 需关注，但不会立即停机。</summary>
        Warning = 1,

        /// <summary>严重 — 需立即处理，可能导致停机。</summary>
        Critical = 2
    }

    /// <summary>
    /// 报警状态枚举。
    /// </summary>
    public enum AlarmStatus : byte
    {
        /// <summary>活跃 — 报警条件仍满足。</summary>
        Active = 0,

        /// <summary>已确认 — 操作员已确认但条件未消除。</summary>
        Acknowledged = 1,

        /// <summary>已清除 — 报警条件已消失。</summary>
        Cleared = 2
    }

}

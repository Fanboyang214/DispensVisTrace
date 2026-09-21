using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 报警类型枚举 — 定义报警触发条件的类型。
    /// </summary>
    public enum AlarmType : byte
    {
        /// <summary>高限报警 — 值超过上限阈值触发。</summary>
        High = 0,

        /// <summary>低限报警 — 值低于下限阈值触发。</summary>
        Low = 1,

        /// <summary>高高限报警 — 值超过高高限阈值触发，通常表示危险状态。</summary>
        HighHigh = 2,

        /// <summary>低低限报警 — 值低于低低限阈值触发，通常表示危险状态。</summary>
        LowLow = 3,

        /// <summary>布尔报警 — 值为 true 时触发（用于开关量）。</summary>
        Bool = 4
    }
}

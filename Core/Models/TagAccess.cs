using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 测点访问权限 — 区分只读、只写和读写。
    /// </summary>
    public enum TagAccess : byte
    {
        /// <summary>只读 — 仅采集，不可写入。</summary>
        Read = 0,

        /// <summary>只写 — 仅上位机写入，不从设备采集。</summary>
        Write = 1,

        /// <summary>读写 — 既可采集也可写入。</summary>
        ReadWrite = 2
    }
}

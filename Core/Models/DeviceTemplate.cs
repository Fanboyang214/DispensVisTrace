using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 设备模板 — 定义一类设备的完整配置。
    /// 包含设备类型、协议驱动以及该设备上所有数据点模板。
    /// </summary>
    public sealed class DeviceTemplate
    {
        /// <summary>模板唯一标识。</summary>
        public string TemplateId { get; init; } = string.Empty;

        /// <summary>模板名称。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>协议驱动类型（"Modbus", "OpcUA", "S7"）。</summary>
        public string DriverType { get; init; } = string.Empty;

        /// <summary>该设备类型上的数据点模板集合。</summary>
        public List<DataPointTemplate> DataPoints { get; init; } = [];
    }

}

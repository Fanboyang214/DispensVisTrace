using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{

    /// <summary>
    /// 数据点模板 — 定义一类数据点的完整配置。
    /// 包含报警模板和趋势模板的引用，Tag 直接引用此模板即可获得报警和趋势能力。
    /// </summary>
    public sealed class DataPointTemplate
    {
        /// <summary>模板唯一标识。</summary>
        public string TemplateId { get; init; } = string.Empty;

        /// <summary>模板名称。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>数据类型。</summary>
        public TagDataType DataType { get; init; } = TagDataType.Float32;

        /// <summary>工程单位。</summary>
        public string Unit { get; init; } = string.Empty;

        /// <summary>关联的报警模板（可选）。</summary>
        public AlarmTemplate? AlarmTemplate { get; init; }

        /// <summary>关联的趋势模板（可选）。</summary>
        public TrendTemplate? TrendTemplate { get; init; }
    }
}

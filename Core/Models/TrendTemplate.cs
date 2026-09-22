using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 趋势模板 — 预定义的趋势显示配置。
    /// 定义曲线颜色、Y轴范围、时间窗口、缓冲区大小等参数。
    /// </summary>
    public sealed class TrendTemplate
    {
        /// <summary>模板唯一标识。</summary>
        public string TemplateId { get; init; } = string.Empty;

        /// <summary>模板名称。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>工程单位。</summary>
        public string Unit { get; init; } = string.Empty;

        /// <summary>Y 轴最小值（NaN 表示自动）。</summary>
        public double YMin { get; init; } = double.NaN;

        /// <summary>Y 轴最大值（NaN 表示自动）。</summary>
        public double YMax { get; init; } = double.NaN;

        /// <summary>环形缓冲区容量（数据点数）。</summary>
        public int BufferCapacity { get; init; } = 3600;

        /// <summary>时间窗口（秒）。</summary>
        public int WindowSeconds { get; init; } = 300;

        /// <summary>默认曲线颜色（十六进制）。</summary>
        public string LineColor { get; init; } = "#3B82F6";

        /// <summary>是否显示报警线。</summary>
        public bool ShowAlarmLines { get; init; } = true;

        /// <summary>曲线线宽。</summary>
        public double StrokeThickness { get; init; } = 2;

        /// <summary>是否显示数据点。</summary>
        public bool ShowGeometry { get; init; } = false;
    }
}

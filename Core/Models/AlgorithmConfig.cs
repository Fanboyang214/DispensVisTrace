using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 引擎初始化时传入的算法配置
    /// </summary>
    public class AlgorithmConfig
    {
        /// <summary>产品型号标识符，用于区分不同产品的参数集。</summary>
        public required string ProductModel { get; init; }
        
        /// <summary>
        /// 算法配置的描述信息，便于追溯与日志。
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; init;  }
        /// <summary>
        /// 算法是否启用，若为 false，则引擎初始化时不加载模型和配置。
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }
        /// <summary>
        /// 定位算法配置
        /// </summary>
        [JsonPropertyName("locate")]
        public LocateConfig Locate { get; init; }
        /// <summary>
        /// 缺陷检测算法配置
        /// </summary>
        [JsonPropertyName("inspect")]
        public InspectConfig Inspect { get; init; }
        /// <summary>
        /// 标定配置
        /// </summary>
        [JsonPropertyName("calibration")]
        public CalibrationConfig? Calibration { get; init; }
        /// <summary>
        /// 可视化配置
        /// </summary>
        [JsonPropertyName("visualization")]
        public VisualizationConfig? Visualization { get; init; }
    }


}

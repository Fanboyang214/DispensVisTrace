using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Core.Models
{
    public class InspectConfig
    {
        /// <summary>
        /// 图像质量门限配置
        /// </summary>
        [JsonPropertyName("imageQualityGate")]
        public Dictionary<string, object> ImageQualityGate { get; init; } = new();

        /// <summary>
        /// 预处理配置
        /// </summary>
        [JsonPropertyName("preprocess")]
        public Dictionary<string, object> Preprocess { get; init; } = new();
        /// <summary>
        /// 检测计划配置
        /// </summary>
        [JsonPropertyName("inspectionPlan")]
        public Dictionary<string, object> InspectionPlan { get; set; } = new();
        /// <summary>
        /// 胶路提取配置
        /// </summary>
        [JsonPropertyName("beadExtraction")]
        public Dictionary<string, object> BeadExtraction { get; set; } = new();
        /// <summary>
        /// 胶路宽度测量配置
        /// </summary>
        [JsonPropertyName("glueWidthMeasurement")]
        public Dictionary<string, object> GlueWidthMeasurement { get; set; } = new();
        /// <summary>
        /// 胶线断续检测配置
        /// </summary>
        [JsonPropertyName("continuityDetection")]
        public Dictionary<string, object> ContinuityDetection { get; set; } = new();
        /// <summary>
        /// 胶线偏移检测配置
        /// </summary>
        [JsonPropertyName("offsetDetection")]
        public Dictionary<string, object> OffsetDetection { get; set; } = new();
        /// <summary>
        /// 胶线溢出检测配置
        /// </summary>
        [JsonPropertyName("overflowDetection")]
        public Dictionary<string, object> OverflowDetection { get; set; } = new();
        /// <summary>
        /// 缺陷分类配置
        /// </summary>
        [JsonPropertyName("defectClassification")]
        public Dictionary<string, object> DefectClassification { get; set; } = new();
        /// <summary>
        /// 整体判定配置
        /// </summary>
        [JsonPropertyName("overallJudgment")]
        public Dictionary<string, object> OverallJudgment { get; set; } = new();
    }
}

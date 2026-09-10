using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Core.Models
{
    public class LocateConfig
    {
        /// <summary>
        /// 图像质量门限配置
        /// </summary>
        [JsonPropertyName("imageQualityGate")]
        public Dictionary<string, object> ImageQualityGate { get; init; } = new ();
        
        /// <summary>
        /// 预处理配置
        /// </summary>
        [JsonPropertyName("preprocess")]
        public Dictionary<string, object> Preprocess { get; init; } = new();
        /// <summary>
        /// 模板匹配配置
        /// </summary>
        [JsonPropertyName("templateMatching")]
        public Dictionary<string,object> TemplateMatching { get; set; } = new();
        /// <summary>
        /// CAD胶路导入配置
        /// </summary>
        [JsonPropertyName("cadImport")]
        public Dictionary<string,object> CadImport { get; set; } = new();
        /// <summary>
        /// 坐标转换配置
        /// </summary>
        [JsonPropertyName("output")]
        public Dictionary<string, object> Output { get; set; }  = new();
    }
}

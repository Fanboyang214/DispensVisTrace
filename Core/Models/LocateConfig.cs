using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    public class LocateConfig
    {
        /// <summary>
        /// 图像质量门限配置
        /// </summary>
        public Dictionary<string, object> ImageQualityGate { get; init; } = new ();
        
        /// <summary>
        /// 预处理配置
        /// </summary>
        public Dictionary<string, object> Preprocess { get; init; } = new();
        /// <summary>
        /// 模板匹配配置
        /// </summary>
        public Dictionary<string,object> TemplateMatching { get; set; } = new();
        /// <summary>
        /// CAD胶路导入配置
        /// </summary>
        public Dictionary<string,object> CadImport { get; set; } = new();
        /// <summary>
        /// 坐标转换配置
        /// </summary>
        public Dictionary<string, object> Output { get; set; }  = new();
    }
}

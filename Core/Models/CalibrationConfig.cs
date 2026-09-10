using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Core.Models
{
    public class CalibrationConfig
    {
        /// <summary>
        /// 定位相机标定
        /// </summary>
        [JsonPropertyName("locateCamera")]
        public Dictionary<string,object> LocateCamera { get; init; } = new Dictionary<string, object>();

        /// <summary>
        /// 检测相机标定
        /// </summary>
        [JsonPropertyName("inspectCamera")]
        public Dictionary<string,object> InspectCamera { get; init; } = new Dictionary<string, object>();
    }
}

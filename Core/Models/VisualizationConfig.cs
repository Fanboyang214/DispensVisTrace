using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Core.Models
{
    public class VisualizationConfig
    {
        [JsonPropertyName("locateOverlay")]
        public Dictionary<string,object> LocateOverlay { get;init; } = new Dictionary<string,object>();

        [JsonPropertyName("inspectOverlay")]
        public Dictionary<string,object> InspectOverlay { get;init; } = new Dictionary<string,object>();
    }
}

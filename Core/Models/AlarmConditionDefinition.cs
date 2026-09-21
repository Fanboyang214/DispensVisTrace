using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{

    /// <summary>
    /// One configured expression fragment of an alarm definition.
    /// Multiple fragments allow industrial alarms such as:
    /// Value &gt; 80 AND LineRunning == true AND MaintenanceMode == false.
    /// </summary>
    public sealed record AlarmConditionDefinition
    {
        public string Name { get; init; } = string.Empty;

        public string Expression { get; init; } = string.Empty;

        public int SortOrder { get; init; }

        public bool IsEnabled { get; init; } = true;
    }

}

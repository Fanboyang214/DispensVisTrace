using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    public class ImageQualityResult
    {
        public bool IsPass { get; init; } = false;

        public double Score { get; init; } = 0.0;
    }
}

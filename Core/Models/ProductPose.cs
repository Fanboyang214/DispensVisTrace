using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    public class ProductPose
    {

        public double CenterRow { get; init; }

        public double CenterCol { get; init; }

        public double AngleDeg => AngleRad * 180.0 / Math.PI;

        public double AngleRad { get; init; }
       

        public double Score { get; init; }

        public override string? ToString()
        {
            return $"Pose: Row:{CenterRow:F3},Col:{CenterCol:F3},Angle:{AngleDeg:F2}°,Score:{Score:F3}";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Calibration
{
    public interface ICalibrationTransform
    {
        public bool IsCalibration { get; }

        public (double worldX, double worldY) WorldToPixel(double pixelX, double pixelY);

        public (double pixelX, double pixelY) PixelToWorld(double worldX, double worldY);

        public double AnglePixelToWorld(double pixelAngleRad);

        public void ClearCalibration();

        public List<PointF> PixelToWorldBatch(IEnumerable<(double X,double Y)> pixelPoints);

    }
}

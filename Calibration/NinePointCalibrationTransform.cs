using Core.Calibration;
using MathNet.Numerics.LinearAlgebra;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace Calibration
{
    public class NinePointCalibrationTransform:ICalibrationTransform
    {
        public bool IsCalibration { get => _isCalibrationValid; }
        private bool _isCalibrationValid;
        
        private Matrix3x2 _pixelToWorldMatrix;
        private Matrix3x2 _worldToPixelMatrix;

        public struct CalibPoint
        {
            public double pixelX;
            public double pixelY;
            public double worldX;
            public double world;
        }

        public void SolveMatrix(IReadOnlyList<CalibPoint> points)
        {
            if (points.Count < 6)
                throw new ArgumentException("标定点至少需要6个");

            int n = points.Count;
            var A = Matrix<double>.Build.Dense(n * 2, 6);
            var b = MathNet.Numerics.LinearAlgebra.Vector<double>.Build.Dense(n * 2);

            for (int i = 0; i < n; i++)
            {
                var p = points[i];
                int row0 = i * 2;
                int row1 = i * 2 + 1;

                // Wx = m00*Px + m01*Py + m02
                A[row0, 0] = p.pixelX;
                A[row0, 1] = p.pixelY;
                A[row0, 2] = 1;
                A[row0, 3] = 0;
                A[row0, 4] = 0;
                A[row0, 5] = 0;
                b[row0] = p.worldX;

                // Wy = m10*Px + m11*Py + m12
                A[row1, 0] = 0;
                A[row1, 1] = 0;
                A[row1, 2] = 0;
                A[row1, 3] = p.pixelX;
                A[row1, 4] = p.pixelY;
                A[row1, 5] = 1;
                b[row1] = p.pixelY;
            }

            var x = A.Solve(b);
            double m00 = x[0];
            double m01 = x[1];
            double m02 = x[2];
            double m10 = x[3];
            double m11 = x[4];
            double m12 = x[5];

            _pixelToWorldMatrix = new Matrix3x2
            (
                (float)m00, (float)m01, (float)m02,
                (float)m10, (float)m11, (float)m12
            );

            if(!Matrix3x2.Invert(_pixelToWorldMatrix,out _worldToPixelMatrix))
            {
                _isCalibrationValid = false;

                throw new Exception("仿射矩阵不可逆，标定失败");
            }
            _isCalibrationValid = true;

        }

        public (double worldX, double worldY) WorldToPixel(double pixelX, double pixelY)
        {
            if (!IsCalibration)
            {
                throw new Exception("标定无效");
            }
            var x = Vector2.Transform(new Vector2((float)pixelX,(float)pixelY), _worldToPixelMatrix);
            return (x.X, x.Y);
        }

        public (double pixelX, double pixelY) PixelToWorld(double worldX, double worldY)
        {
            if (!IsCalibration)
            {
                throw new Exception("标定无效");
            }
            var x = Vector2.Transform(new Vector2((float)worldX, (float)worldY), _pixelToWorldMatrix);
            return (x.X, x.Y);
        }

        public double AnglePixelToWorld(double pixelAngleRad)
        {
            // 提取仿射矩阵的旋转分量
            var m = _pixelToWorldMatrix;
            double scaleX = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
            double rotRad = Math.Atan2(m.M12, m.M11);
            return pixelAngleRad + rotRad;
        }

        public void ClearCalibration()
        {
            _pixelToWorldMatrix = Matrix3x2.Identity;
            _worldToPixelMatrix = Matrix3x2.Identity;
            _isCalibrationValid = false;

        }

        public List<PointF> PixelToWorldBatch(IEnumerable<(double X, double Y)> pixelPoints)
        {
            var reslist= new List<PointF>();
            foreach( var point in pixelPoints)
            {
                var v = PixelToWorld(point.X, point.Y);
                reslist.Add(new PointF((float)v.pixelX, (float)v.pixelY));
            }
            return reslist;
        }

       
    }
}

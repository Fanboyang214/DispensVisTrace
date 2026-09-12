using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 涂胶路径
    /// </summary>
    public class GluePath
    {
        /// <summary>
        /// 路径点集合（单位：mm，经过CAD坐标转换+产品位姿变换后的点）
        /// </summary>
        public List<PointF> Points { get; }

        /// <summary>
        /// 模板匹配置信度（来自ProductPose.Score）
        /// </summary>
        public double Confidence { get; }

        /// <summary>
        /// 路径总长度 mm
        /// </summary>
        public double TotalLengthMm { get; }

        /// <summary>
        /// 包围盒
        /// </summary>
        public RectangleF BoundingBox { get; }

        public GluePath(List<PointF> points, double confidence)
        {
            Points = points ?? new List<PointF>();
            Confidence = confidence;

            TotalLengthMm = CalcPathLength(Points);
            BoundingBox = CalcBoundingBox(Points);
        }

        /// <summary>
        /// 计算路径总长度
        /// </summary>
        private static double CalcPathLength(List<PointF> pts)
        {
            double len = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                float dx = pts[i].X - pts[i - 1].X;
                float dy = pts[i].Y - pts[i - 1].Y;
                len += Math.Sqrt(dx * dx + dy * dy);
            }
            return len;
        }

        /// <summary>
        /// 计算包围盒
        /// </summary>
        private static RectangleF CalcBoundingBox(List<PointF> pts)
        {
            if (pts.Count == 0)
                return RectangleF.Empty;

            var xs = pts.Select(p => p.X);
            var ys = pts.Select(p => p.Y);
            float minX = xs.Min();
            float maxX = xs.Max();
            float minY = ys.Min();
            float maxY = ys.Max();
            return new RectangleF(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// 判断路径是否有效（点数量大于等于2，可走胶）
        /// </summary>
        public bool IsValid()
        {
            return Points.Count >= 2;
        }

        /// <summary>
        /// 克隆路径
        /// </summary>
        public GluePath Clone()
        {
            return new GluePath(new List<PointF>(Points), Confidence);
        }
    }
}

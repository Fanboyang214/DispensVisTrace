using Core.Motion;
using System;
using System.Collections.Generic;
using System.Windows;

namespace Motion.ViewModel
{
    /// <summary>
    /// 把轨迹的模型坐标（mm，Y 向上）映射到预览画布坐标（px，Y 向下）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只做<b>显示变换</b>，绝不修改 <see cref="TrajectorySegment"/> 的几何；
    /// 旧实现把模型坐标和圆弧半径直接按画布尺寸改写，导致导出/下发前的轨迹被破坏。
    /// </para>
    /// <para>
    /// Y 轴翻转：模型 Y 向上、画布 Y 向下。圆弧在模型里始终逆时针，
    /// 翻转后在画布上变成顺时针，所以 <see cref="TrajectorySegmentViewModel"/>
    /// 画 <see cref="System.Windows.Media.ArcSegment"/> 时用 <c>SweepDirection.Clockwise</c>。
    /// </para>
    /// </remarks>
    public sealed class TrajectoryViewTransform
    {
        private double _offsetX;
        private double _offsetYUp;
        private double _height = 1;

        /// <summary>模型 mm → 画布 px 的比例。</summary>
        public double Scale { get; private set; } = 1;

        /// <summary>
        /// 按当前段列表计算等比缩放与居中偏移。
        /// </summary>
        /// <param name="segments">轨迹段（只读，不修改）</param>
        /// <param name="width">画布宽（px）</param>
        /// <param name="height">画布高（px）</param>
        /// <param name="margin">四周边距（px）</param>
        public void Update(
            IReadOnlyList<TrajectorySegment> segments,
            double width,
            double height,
            double margin)
        {
            _height = Math.Max(1, height);
            double usableWidth = Math.Max(1, width - 2 * margin);
            double usableHeight = Math.Max(1, height - 2 * margin);

            if (segments.Count == 0)
            {
                Scale = 1;
                _offsetX = margin;
                _offsetYUp = margin;
                return;
            }

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            foreach (TrajectorySegment segment in segments)
            {
                if (segment is null)
                {
                    continue;
                }

                IncludePoint(segment.StartPt, ref minX, ref minY, ref maxX, ref maxY);
                IncludePoint(segment.EndPt, ref minX, ref minY, ref maxX, ref maxY);

                if (segment.SegType == SegmentType.Arc && segment.Radius > 0)
                {
                    // 整圆包络，保证弧段不会被画布裁掉
                    double radius = Math.Abs(segment.Radius);
                    IncludePoint(
                        new Point3D(segment.ArcCenter.X - radius, segment.ArcCenter.Y - radius, 0),
                        ref minX, ref minY, ref maxX, ref maxY);
                    IncludePoint(
                        new Point3D(segment.ArcCenter.X + radius, segment.ArcCenter.Y + radius, 0),
                        ref minX, ref minY, ref maxX, ref maxY);
                }
            }

            if (minX > maxX || minY > maxY)
            {
                Scale = 1;
                _offsetX = margin;
                _offsetYUp = margin;
                return;
            }

            double extentX = Math.Max(maxX - minX, 1e-9);
            double extentY = Math.Max(maxY - minY, 1e-9);

            double scaleX = usableWidth / extentX;
            double scaleY = usableHeight / extentY;
            Scale = Math.Min(scaleX, scaleY);

            if (double.IsNaN(Scale) || double.IsInfinity(Scale) || Scale <= 0)
            {
                Scale = 1;
            }

            _offsetX = margin + (usableWidth - extentX * Scale) / 2 - minX * Scale;
            _offsetYUp = margin + (usableHeight - extentY * Scale) / 2 - minY * Scale;
        }

        /// <summary>模型点 → 画布点（忽略 Z）。</summary>
        public Point Map(Point3D point) => Map(point.X, point.Y);

        /// <summary>模型 XY → 画布点。</summary>
        public Point Map(double x, double y) =>
            new(
                x * Scale + _offsetX,
                _height - (y * Scale + _offsetYUp));

        private static void IncludePoint(
            Point3D point,
            ref double minX,
            ref double minY,
            ref double maxX,
            ref double maxY)
        {
            if (point.X < minX)
            {
                minX = point.X;
            }

            if (point.Y < minY)
            {
                minY = point.Y;
            }

            if (point.X > maxX)
            {
                maxX = point.X;
            }

            if (point.Y > maxY)
            {
                maxY = point.Y;
            }
        }
    }
}

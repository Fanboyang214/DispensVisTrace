using Core.Motion;
using netDxf;
using netDxf.Entities;
using System;
using System.Collections.Generic;

namespace Motion
{
    /// <summary>
    /// 单个轨迹段：直线 / 圆弧，绑定几何 + 本段工艺 + IO 触发列表
    /// </summary>
    /// <remarks>
    /// 圆弧方向约定：<see cref="StartAngle"/> 到 <see cref="EndAngle"/> <b>始终沿逆时针（角度递增）</b>扫掠，
    /// 顺时针圆弧由 DXF 解析时交换起点/终点保证。少画一整圈时 EndAngle == StartAngle，
    /// 按整圆（2π）处理。
    /// </remarks>
    public class TrajectorySegment
    {
        #region 几何信息

        /// <summary>段几何类型：直线 / 圆弧</summary>
        public SegmentType SegType;

        /// <summary>段起点（Dispense 段取 WorkZ，Move 段取 LiftZ）</summary>
        public Point3D StartPt;

        /// <summary>段终点（Dispense 段取 WorkZ，Move 段取 LiftZ）</summary>
        public Point3D EndPt;

        // ---------- 圆弧专用字段，Line 段忽略 ----------

        /// <summary>圆弧圆心（仅 Arc 有效）</summary>
        public Point3D ArcCenter;

        /// <summary>圆弧半径 mm（仅 Arc 有效）</summary>
        public double Radius;

        /// <summary>圆弧起始角，弧度（仅 Arc 有效）</summary>
        public double StartAngle;

        /// <summary>圆弧终止角，弧度（仅 Arc 有效）</summary>
        public double EndAngle;

        #endregion

        #region 工艺信息

        /// <summary>运动模式：点胶 / 空移，决定用 WorkZ 还是 LiftZ</summary>
        public MotionMode MotionMode;

        /// <summary>本段运动速度 mm/s</summary>
        public double Speed = 500;

        /// <summary>点胶工作高度（Dispense 生效）</summary>
        public double WorkZ = 5;

        /// <summary>抬刀高度（Move 生效）</summary>
        public double LiftZ = 30;

        /// <summary>本段总长度 mm，由 <see cref="CalcSegmentLength"/> 计算</summary>
        public double SegmentLength;

        #endregion

        #region IO触发列表

        /// <summary>本段 IO 触发事件列表，Move 段禁止存在任何触发</summary>
        public List<IoTrigger> IoTriggers = new List<IoTrigger>();

        #endregion

        #region 方法

        /// <summary>
        /// 起止角相等判定阈值（弧度）。小于该值认为两点同角，
        /// 而不是一段"几乎整圈"的圆弧——浮点减法的残留只有 1e-16 量级，1e-9 足够区分。
        /// </summary>
        private const double ArcAngleEpsilon = 1e-9;

        /// <summary>本段逆时针扫掠角，落在 (0, 2π]</summary>
        public double CircleSweep => NormalizeSweep(EndAngle - StartAngle);

        /// <summary>
        /// 把一个整圆拆成两段半圆弧。
        /// </summary>
        /// <param name="center">圆心（Z 取自此处）</param>
        /// <param name="radius">半径 mm，取绝对值</param>
        /// <param name="startAngle">起始角（弧度），圆由此角开始沿逆时针走</param>
        /// <param name="motionMode">运动模式，默认点胶</param>
        /// <returns>两段按行进顺序排列的半圆弧</returns>
        /// <remarks>
        /// <para>
        /// 之所以要拆：多数运动控制卡的圆弧指令由"终点 + 圆心/半径"定义，整圈时终点与起点重合，
        /// 卡无法判断走整圈还是不走，所以必须给一个中间点把它拆成两段 π 圆弧。
        /// </para>
        /// <para>
        /// 每段始终落在 (0, π] 开区间内：两段都用 <b>未归一化</b> 的
        /// <c>StartAngle + π</c> 作终点角，避免绕回 2π 引入浮点残差，
        /// 也就永远不会退化成"起止角相等"而被误判成整圆。
        /// </para>
        /// </remarks>
        public static List<TrajectorySegment> FromCircle(
            Point3D center, double radius, double startAngle, MotionMode motionMode = MotionMode.Dispense)
        {
            double r = Math.Abs(radius);
            double midAngle = startAngle + Math.PI;
            double endAngle = startAngle + 2 * Math.PI;

            return new List<TrajectorySegment>
            {
                new TrajectorySegment
                {
                    SegType = SegmentType.Arc,
                    MotionMode = motionMode,
                    ArcCenter = center,
                    Radius = r,
                    StartAngle = startAngle,
                    EndAngle = midAngle,
                    StartPt = OnCircle(center, r, startAngle),
                    EndPt = OnCircle(center, r, midAngle)
                },
                new TrajectorySegment
                {
                    SegType = SegmentType.Arc,
                    MotionMode = motionMode,
                    ArcCenter = center,
                    Radius = r,
                    StartAngle = NormalizeAngle(midAngle),
                    EndAngle = NormalizeAngle(endAngle),
                    StartPt = OnCircle(center, r, NormalizeAngle(midAngle)),
                    EndPt = OnCircle(center, r, NormalizeAngle(endAngle))
                }
            };
        }

        /// <summary>圆心 + 半径 + 角度 → 圆上一点（Z 取圆心 Z）</summary>
        private static Point3D OnCircle(Point3D center, double radius, double angle) =>
            new Point3D(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle),
                center.Z);

        /// <summary>把角度归一化到 [0, 2π)</summary>
        private static double NormalizeAngle(double angle)
        {
            const double fullTurn = 2 * Math.PI;

            angle %= fullTurn;
            if (angle < 0)
            {
                angle += fullTurn;
            }
            return angle;
        }

        /// <summary>
        /// 对本段几何执行旋转平移变换（视觉补偿），<see cref="IoTriggers"/> 无需变动
        /// </summary>
        /// <param name="dx">X 方向平移量 mm</param>
        /// <param name="dy">Y 方向平移量 mm</param>
        /// <param name="theta">绕 Z 轴旋转角，弧度</param>
        /// <param name="rotCenter">旋转中心</param>
        /// <remarks>
        /// Z 不参与变换。圆弧为刚体旋转：圆心随旋转中心变换，半径不变，
        /// 起止角整体偏移 theta，因此段内任意点与圆心的极角关系保持不变。
        /// </remarks>
        public void Transform(double dx, double dy, double theta, Point3D rotCenter)
        {
            StartPt = RotateThenTranslate(StartPt, dx, dy, theta, rotCenter);
            EndPt = RotateThenTranslate(EndPt, dx, dy, theta, rotCenter);

            if (SegType == SegmentType.Arc)
            {
                // 圆心到起止点的半径向量同步旋转 theta，所以半径不变、起止角同增 theta
                ArcCenter = RotateThenTranslate(ArcCenter, dx, dy, theta, rotCenter);
                StartAngle += theta;
                EndAngle += theta;
            }
        }

        /// <summary>
        /// 深拷贝本段（几何、工艺、IO 触发列表全部复制）
        /// </summary>
        public TrajectorySegment Clone()
        {
            var copy = new TrajectorySegment
            {
                SegType = SegType,
                StartPt = StartPt,
                EndPt = EndPt,
                ArcCenter = ArcCenter,
                Radius = Radius,
                StartAngle = StartAngle,
                EndAngle = EndAngle,
                MotionMode = MotionMode,
                Speed = Speed,
                WorkZ = WorkZ,
                LiftZ = LiftZ,
                SegmentLength = SegmentLength,
                IoTriggers = new List<IoTrigger>(IoTriggers.Count)
            };

            foreach (IoTrigger trigger in IoTriggers)
            {
                copy.IoTriggers.Add(new IoTrigger
                {
                    DistanceOnSegment = trigger.DistanceOnSegment,
                    Action = trigger.Action,
                    DelayMs = trigger.DelayMs
                });
            }

            return copy;
        }

        /// <summary>
        /// 解析 DXF 后调用，自动计算本段长度存入 <see cref="SegmentLength"/>
        /// </summary>
        public void CalcSegmentLength()
        {
            if (SegType == SegmentType.Line)
            {
                double dx = EndPt.X - StartPt.X;
                double dy = EndPt.Y - StartPt.Y;
                double dz = EndPt.Z - StartPt.Z;
                SegmentLength = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                return;
            }

            // 圆弧：按 XY 平面扫掠角 × 半径
            double sweep = NormalizeSweep(EndAngle - StartAngle);
            SegmentLength = Math.Abs(Radius) * sweep;
        }

        /// <summary>
        /// 沿段内距离插值，获取对应坐标，用于预览 IO 触发位置
        /// </summary>
        /// <param name="distOnSeg">自本段起点起的行进距离 mm，超出 [0, SegmentLength] 时按端点截断</param>
        /// <returns>该距离对应的机械坐标点</returns>
        public Point3D GetPointByDist(double distOnSeg)
        {
            if (SegmentLength <= 0)
            {
                return EndPt;
            }

            double dist = Math.Clamp(distOnSeg, 0, SegmentLength);
            double ratio = dist / SegmentLength;

            if (SegType == SegmentType.Line)
            {
                return new Point3D(
                    StartPt.X + (EndPt.X - StartPt.X) * ratio,
                    StartPt.Y + (EndPt.Y - StartPt.Y) * ratio,
                    StartPt.Z + (EndPt.Z - StartPt.Z) * ratio);
            }

            double startAngle = StartAngle;
            double sweep = NormalizeSweep(EndAngle - StartAngle);
            // 逆时针扫掠：dist 为 0 取起点角，为 SegmentLength 取终点角
            // 半径取绝对值：DXF 解析若给出负半径表示反向，几何上仍是同一段圆弧
            double angle = startAngle + sweep * ratio;
            double radius = Math.Abs(Radius);

            return new Point3D(
                ArcCenter.X + radius * Math.Cos(angle),
                ArcCenter.Y + radius * Math.Sin(angle),
                ArcCenter.Z);
        }

        /// <summary>
        /// 求逆时针扫掠角，落在 (0, 2π]。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 约定：<b>起止角相等表示整圆（2π），不表示零长度</b>。零长度圆弧由调用方拒绝，
        /// 所以这里把 0 映射为 2π。
        /// </para>
        /// <para>
        /// 减法的浮点残差必须处理掉：两段半圆弧拆圆时，若半圆终点角被绕回 2π，
        /// <c>EndAngle - StartAngle</c> 可能算出 1e-16 或 -1e-16 这样的残留。
        /// 直接用 <c>sweep %= 2π</c> 再判 <c>&lt;= 0</c> 会把它当成整圈，
        /// 半圆弧会突然变成整圆，长度和插值点全错。
        /// </para>
        /// </remarks>
        private static double NormalizeSweep(double sweep)
        {
            const double fullTurn = 2 * Math.PI;

            // 先夹到 (-2π, 2π)，再吃掉"等同起止角"的浮点残差
            sweep %= fullTurn;
            if (Math.Abs(sweep) < ArcAngleEpsilon)
            {
                return fullTurn;
            }

            if (sweep < 0)
            {
                sweep += fullTurn;
            }

            return sweep;
        }

        /// <summary>
        /// 先绕 <paramref name="rotCenter"/> 旋转 theta，再平移 (dx, dy)，Z 保持不变
        /// </summary>
        private static Point3D RotateThenTranslate(Point3D p, double dx, double dy, double theta, Point3D rotCenter)
        {
            double cos = Math.Cos(theta);
            double sin = Math.Sin(theta);
            double x = p.X - rotCenter.X;
            double y = p.Y - rotCenter.Y;

            return new Point3D(
                rotCenter.X + x * cos - y * sin + dx,
                rotCenter.Y + x * sin + y * cos + dy,
                p.Z);
        }


        public static TrajectorySegment FromLine(Line line,MotionMode mode)
        {
            
            var trajectory =  new TrajectorySegment
            {
                SegType = SegmentType.Line,
                StartPt = new Point3D(line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z),
                EndPt = new Point3D(line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z),
                MotionMode = mode, 
                
            };
            
            return trajectory;

        }

        public static TrajectorySegment FromArc(Arc arc, MotionMode mode)
        {

            var trajectory = new TrajectorySegment
            {
                SegType = SegmentType.Arc,
                ArcCenter = new Point3D(arc.Center.X, arc.Center.Y, arc.Center.Z),
                Radius = arc.Radius,
                MotionMode = mode,
                
               
                StartAngle = arc.StartAngle * Math.PI / 180.0,
                EndAngle = arc.EndAngle * Math.PI / 180.0,
                StartPt = OnCircle(new Point3D(arc.Center.X, arc.Center.Y, arc.Center.Z), arc.Radius, arc.StartAngle * Math.PI / 180.0),
                EndPt = OnCircle(new Point3D(arc.Center.X, arc.Center.Y, arc.Center.Z), arc.Radius, arc.EndAngle * Math.PI / 180.0),

            };
           
            return trajectory;

        }
        public static List<TrajectorySegment> FromCircle(Circle circle, MotionMode mode)
        {

           return FromCircle(new Point3D(circle.Center.X, circle.Center.Y, circle.Center.Z), circle.Radius, 0, mode);

        }

        /// <summary>
        /// 把 3D 多段线按顶点顺序拆成直线段。
        /// </summary>
        /// <param name="pline">netDxf 的 Polyline3D</param>
        /// <param name="mode">运动模式，由所在图层决定</param>
        /// <remarks>
        /// <para>
        /// DXF 的 POLYLINE（3D 多段线）顶点是 <c>Vector3</c>，<b>不带 bulge 凸度</b>，
        /// 所以每两个相邻顶点之间只能是直线。带圆弧的二维多段线请用
        /// <see cref="FromPolyline2D(Polyline2D, MotionMode)"/>。
        /// </para>
        /// <para>
        /// 闭合多段线要补上"最后一个顶点 → 第一个顶点"这一段，否则轮廓会缺一边。
        /// </para>
        /// </remarks>
        public static List<TrajectorySegment> FromPolyline3D(Polyline3D pline, MotionMode mode)
        {
            var segments = new List<TrajectorySegment>();
            var vertexes = pline.Vertexes;

            if (vertexes == null || vertexes.Count < 2)
            {
                return segments;
            }

            int spanCount = pline.IsClosed ? vertexes.Count : vertexes.Count - 1;
            for (int i = 0; i < spanCount; i++)
            {
                Vector3 a = vertexes[i];
                Vector3 b = vertexes[(i + 1) % vertexes.Count];
                segments.Add(CreateLine(
                    new Point3D(a.X, a.Y, a.Z),
                    new Point3D(b.X, b.Y, b.Z),
                    mode));
            }

            return segments;
        }

        /// <summary>
        /// 把 2D 多段线按 bulge 凸度拆成直线段 / 圆弧段。
        /// </summary>
        /// <param name="pline">netDxf 的 Polyline2D（对应 DXF 的 LWPOLYLINE / POLYLINE 2D）</param>
        /// <param name="mode">运动模式，由所在图层决定</param>
        /// <param name="elevation">整条多段线所在的 Z 平面，Bulge 顶点只带 XY</param>
        /// <remarks>
        /// bulge 为 0 的两顶点之间是直线；非 0 时
        /// <c>bulge = tan(Δθ/4)</c>，即扫掠角 <c>Δθ = 4·atan(bulge)</c>，符号决定凸向。
        /// </remarks>
        public static List<TrajectorySegment> FromPolyline2D(
            Polyline2D pline, MotionMode mode, double elevation = 5)
        {
            var segments = new List<TrajectorySegment>();
            var vertexes = pline.Vertexes;

            if (vertexes == null || vertexes.Count < 2)
            {
                return segments;
            }

            int spanCount = pline.IsClosed ? vertexes.Count : vertexes.Count - 1;
            for (int i = 0; i < spanCount; i++)
            {
                Polyline2DVertex va = vertexes[i];
                Polyline2DVertex vb = vertexes[(i + 1) % vertexes.Count];

                var a = new Point3D(va.Position.X, va.Position.Y, elevation);
                var b = new Point3D(vb.Position.X, vb.Position.Y, elevation);

                if (Math.Abs(va.Bulge) < ArcAngleEpsilon)
                {
                    segments.Add(CreateLine(a, b, mode));
                    continue;
                }

                List<TrajectorySegment>? arcs = CreateArcFromBulge(a, b, va.Bulge, mode);
                if (arcs != null)
                {
                    segments.AddRange(arcs);
                }
            }

            return segments;
        }

        /// <summary>两顶点之间的直线段</summary>
        private static TrajectorySegment CreateLine(Point3D start, Point3D end, MotionMode mode)
        {
            var line = new TrajectorySegment
            {
                SegType = SegmentType.Line,
                MotionMode = mode,
                StartPt = start,
                EndPt = end
            };
            
            return line;
        }

        /// <summary>
        /// 由 bulge 凸度反算圆弧段，最多两段。
        /// </summary>
        /// <remarks>
        /// <para>
        /// bulge = tan(Δθ/4)，Δθ 是本段圆弧的扫掠角。Δθ 为负表示沿顺时针走，
        /// 而 <see cref="TrajectorySegment"/> 统一按逆时针表示，所以顺时针段要把
        /// 起终点对调、扫掠角取正，插补方向才和设备实际走的方向一致。
        /// </para>
        /// <para>
        /// |Δθ| 接近 2π 时是一个闭合整圆（首尾顶点重合、半径无法由弦长反算）。
        /// 这种弧必须交给 <see cref="FromCircle(Point3D, double, double, MotionMode)"/>
        /// 拆成两段半圆弧，因为运动卡无法用"终点==起点"表达整圈。这里按退化情况返回 null，
        /// 由调用方处理：配方导入侧遇到顶点重合的闭合多段线，应当取该段为整圆再来拆分。
        /// </para>
        /// </remarks>
        private static List<TrajectorySegment>? CreateArcFromBulge(
            Point3D a, Point3D b, double bulge, MotionMode mode)
        {
            //获取扫掠角，bulge = tan(Δθ/4) → Δθ = 4·atan(bulge)
            double sweep = 4 * Math.Atan(bulge);

            // 顺时针 → 对调起终点，统一成逆时针
            if (sweep < 0)
            {
                (a, b) = (b, a);
                sweep = -sweep;
            }

            // 顶点重合（整圆）时弦长为 0，半径与圆心都无从反算
            if (sweep >= 2 * Math.PI - ArcAngleEpsilon)
            {
                return null;
            }

            //整圆保护：弦长为 0 时无法反算圆心，budle不能表示整圆，直接为null
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < ArcAngleEpsilon)
            {
                return null;
            }

            // 弦中点沿左法线 (dy, -dx)/chord 偏移 signedDistance 即得圆心：
            // 逆时针扫掠时圆弧凸向弦的右侧，圆心必在左侧；
            // 半弦长 h 与半径 r 满足 h = r·sin(u)，偏移量 = h·cot(u)，u = sweep/2
            double half = chord / 2;
            double u = sweep / 2;
            double signedDistance = half / Math.Tan(u);

            double midX = (a.X + b.X) / 2;
            double midY = (a.Y + b.Y) / 2;
            double centerX = midX + (dy / chord) * signedDistance;
            double centerY = midY - (dx / chord) * signedDistance;

            // 半径用圆心到起点的实际距离，比公式反算更稳
            double radius = Math.Sqrt((a.X - centerX) * (a.X - centerX) + (a.Y - centerY) * (a.Y - centerY));
            double startAngle = Math.Atan2(a.Y - centerY, a.X - centerX);
            double endAngle = Math.Atan2(b.Y - centerY, b.X - centerX);

            // 校核：atan2 的返回区间是 (-π, π]，startAngle 为负、endAngle 为正时，
            // NormalizeSweep 会把 "应该走 90°" 读成 "走 270°"（方向正好走反）。
            // bulge 本身已经确定了扫掠量（sweep = 4·atan(bulge)），
            // 所以这里用实际存入的起止角反算一次，对不上就对调起终点。
            if (!IsSameSweep(SignedSweep(startAngle, endAngle), sweep))
            {
                (a, b) = (b, a);
                (startAngle, endAngle) = (endAngle, startAngle);
            }

            var arc = new TrajectorySegment
            {
                SegType = SegmentType.Arc,
                MotionMode = mode,
                ArcCenter = new Point3D(centerX, centerY, a.Z),
                Radius = radius,
                StartAngle = startAngle,
                EndAngle = endAngle,
                StartPt = a,
                EndPt = b
            };
           

            return new List<TrajectorySegment> { arc };
        }

        /// <summary>
        /// 由两个方位角求"从 <paramref name="from"/> 逆时针到 <paramref name="to"/>"的扫掠角，
        /// 落在 [0, 2π)。首尾同角返回 0（整圆由调用方按 2π 处理）。
        /// </summary>
        private static double SignedSweep(double from, double to)
        {
            const double fullTurn = 2 * Math.PI;

            double delta = (to - from) % fullTurn;
            if (delta < 0)
            {
                delta += fullTurn;
            }
            return delta;
        }

        /// <summary>两个扫掠量是否表示同一段圆弧（角度模 2π 相等）</summary>
        private static bool IsSameSweep(double a, double b)
        {
            double diff = Math.Abs(SignedSweep(0, a - b));
            return diff < ArcAngleEpsilon || Math.Abs(diff - 2 * Math.PI) < ArcAngleEpsilon;
        }

        #endregion
    }
}

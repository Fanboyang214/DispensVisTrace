
using netDxf;
using netDxf.Entities;
using System;
using System.Collections.Generic;

namespace Motion
{
    /// <summary>
    /// DXF 解析器：把 DXF 几何读成运动域的 <see cref="TrajectorySegment"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么在插件里：DXF 只是<b>导入源</b>，解析依赖 netDxf 这类格式专用库。
    /// 放在运动模块或视觉模块都会把「读文件格式」这件事焊进核心领域，
    /// 还会逼着领域模块反向依赖插件。
    /// </para>
    /// <para>
    /// 依赖方向：Plugins → Motion → Core。运动域只提供
    /// <see cref="DispenseProgram.LoadSegments"/> 接收结果，不认识 DXF。
    /// </para>
    /// </remarks>
    public static class DxfParser
    {
        /// <summary>点胶图层名（模式 1）。</summary>
        public const string DispenseLayer = "Dispense";

        /// <summary>空移图层名（模式 1）。</summary>
        public const string MoveLayer = "Move";

        /// <summary>
        /// 解析 DXF 文件，过滤图层。
        /// <para>
        /// 图层 <see cref="DispenseLayer"/> → <see cref="MotionMode.Dispense"/>；
        /// 图层 <see cref="MoveLayer"/> → <see cref="MotionMode.Move"/>；其余图层直接跳过。
        /// 支持 LINE / ARC / CIRCLE / POLYLINE（解析 bulge 拆分为 Line / Arc）。
        /// </para>
        /// </summary>
        /// <param name="dxfPath">DXF 文件路径</param>
        
        /// <returns>按 DXF 实体存储顺序生成的轨迹段列表</returns>
        /// <remarks>
        /// 待实现：需在本项目引入 netDxf 包。
        /// 圆弧必须保留原生几何（圆心 / 半径 / 起止角），不要离散为微小直线段，
        /// 否则运动卡拿不到圆弧参数、无法做原生圆弧插补。
        /// </remarks>
        public static List<TrajectorySegment> Parse(string dxfPath)
        {
            var dxf = DxfDocument.Load(dxfPath);

            var segments = new List<TrajectorySegment>();

            foreach (var entity in dxf.Entities.All)
            {
                MotionMode mode;
                switch (entity.Layer.Name)
                {
                    case DispenseLayer:
                        mode = MotionMode.Dispense;
                        break;
                    case MoveLayer:
                        mode = MotionMode.Move;
                        break;
                    default:
                        continue; // 跳过非目标图层
                }
                List<TrajectorySegment> segs;
                switch (entity.Type)
                {
                    case EntityType.Line:
                        segs = new List<TrajectorySegment> { TrajectorySegment.FromLine((Line)entity, mode) };
                        break;
                    case EntityType.Arc:
                        segs = new List<TrajectorySegment> { TrajectorySegment.FromArc((Arc)entity, mode) };
                        break;
                    case EntityType.Circle:
                        // 整圆无法用"终点==起点"表达，拆成两段半圆弧
                        segs = TrajectorySegment.FromCircle((Circle)entity, mode);
                        break;
                    case EntityType.Polyline3D:
                        // 3D 多段线顶点不带 bulge，逐段直线
                        segs = TrajectorySegment.FromPolyline3D((Polyline3D)entity, mode);
                        break;
                    case EntityType.Polyline2D:
                        // DXF 的 LWPOLYLINE / 2D POLYLINE 落在这里，bulge 凸度要拆成直线 + 圆弧
                        var polyline2D = (Polyline2D)entity;
                        segs = TrajectorySegment.FromPolyline2D(polyline2D, mode, polyline2D.Elevation);
                        break;
                    default:
                        // 标注、辅助线这类图层外实体直接跳过，不要因为一个不支持的实体废掉整份文件
                        continue;
                }
                segments.AddRange(segs);
            }

            return segments;
        }
    }
}

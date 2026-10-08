
using Core.Motion;
using System.Collections.Generic;

namespace Motion
{
    /// <summary>
    /// 视觉位姿计算：对比基准 Mark 和当前工件 Mark，求解工件偏移 ΔX ΔY Δθ 和旋转中心
    /// </summary>
    /// <remarks>
    /// 实现 <see cref="IPoseOffsetSolver"/>，视觉层只依赖该接口与 <see cref="PoseOffset"/>，
    /// 不依赖本类，因此求解算法可以替换或搬走而不影响视觉层。
    /// </remarks>
    public class PoseCompensator : IPoseOffsetSolver
    {
        /// <summary>基准 Mark 点，来自配方 <see cref="DispenseProgram.BaseMarks"/></summary>
        public List<Point3D> BaseMarks = new List<Point3D>();

        /// <summary>当前工件 Mark 点，已完成手眼转换到机械坐标系</summary>
        public List<Point3D> CurrentMarks = new List<Point3D>();

        /// <summary>工件 X 方向偏移量 mm</summary>
        public double DeltaX { get; private set; }

        /// <summary>工件 Y 方向偏移量 mm</summary>
        public double DeltaY { get; private set; }

        /// <summary>工件绕 Z 轴旋转角，弧度</summary>
        public double DeltaTheta { get; private set; }

        /// <summary>旋转中心</summary>
        public Point3D RotCenter { get; private set; }

        /// <summary>
        /// 计算工件位姿偏差，前提：图像坐标已经通过手眼矩阵转为机械坐标系
        /// </summary>
        /// <param name="errMsg">失败原因</param>
        /// <returns>是否求解成功</returns>
        /// <remarks>
        /// 待实现：基准/当前 Mark 配对与刚体变换求解（至少 2 对点定 Δθ）。
        /// </remarks>
        public bool CalcPoseOffset(out string errMsg)
        {
            throw new System.NotImplementedException(
                "PoseCompensator.CalcPoseOffset 待实现：需基准/当前 Mark 配对策略与刚体变换求解。");
        }

        /// <summary>
        /// <see cref="IPoseOffsetSolver"/> 实现：求解并把结果打包成与视觉层约定的值对象
        /// </summary>
        public bool TrySolve(out PoseOffset offset, out string errMsg)
        {
            offset = default;

            if (!CalcPoseOffset(out errMsg))
            {
                return false;
            }

            offset = new PoseOffset(DeltaX, DeltaY, DeltaTheta, RotCenter);
            return true;
        }
    }
}

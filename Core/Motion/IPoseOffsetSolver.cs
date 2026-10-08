namespace Core.Motion
{

    /// <summary>
    /// 三维机械坐标点（单位：mm）
    /// </summary>
    public struct Point3D
    {
        /// <summary>X 坐标 mm</summary>
        public double X;

        /// <summary>Y 坐标 mm</summary>
        public double Y;

        /// <summary>Z 坐标 mm</summary>
        public double Z;

        public Point3D(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>
        /// 复制一个 Z 值不同的点，圆弧变换只涉及 XY，Z 需原样保留
        /// </summary>
        public Point3D WithZ(double z) => new Point3D(X, Y, z);

        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
    }

    /// <summary>
    /// 工件位姿偏移结果：一套刚体变换参数
    /// </summary>
    /// <param name="DeltaX">X 方向平移量 mm</param>
    /// <param name="DeltaY">Y 方向平移量 mm</param>
    /// <param name="DeltaTheta">绕 Z 轴旋转角，弧度</param>
    /// <param name="RotCenter">旋转中心（机械坐标 mm）</param>
    public readonly record struct PoseOffset(double DeltaX, double DeltaY, double DeltaTheta, Point3D RotCenter);

    /// <summary>
    /// 工件位姿求解器的抽象。
    /// </summary>
    /// <remarks>
    /// 存在的意义是解耦：视觉层只依赖这个窄接口和 <see cref="PoseOffset"/>，
    /// 不依赖具体求解实现（如 Motion 里的 Mark 最小二乘解法），
    /// 因此换求解算法、或把求解搬到别处，都不需要改动视觉层。
    /// </remarks>
    public interface IPoseOffsetSolver
    {
        /// <summary>
        /// 求解工件相对基准轨迹的刚体偏移
        /// </summary>
        /// <param name="offset">求解结果，失败时为空</param>
        /// <param name="errMsg">失败原因</param>
        /// <returns>是否求解成功</returns>
        bool TrySolve(out PoseOffset offset, out string errMsg);
    }
}

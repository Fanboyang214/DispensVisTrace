using Core.Motion;

namespace Motion
{
    /// <summary>
    /// 上位机与运动控制卡之间的<b>工艺通道</b>：整套轨迹的装载、位姿、启停。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与数据点通道的分工：资源树上的轴/坐标系读点、jog 单步、上位机侧参数归驱动
    /// （<c>IProtocolDriver</c>）；本接口只管"整套轨迹"这一层。
    /// 连接归 <see cref="IMotionCardLink"/>，两边共用同一份实例（连接只建立一次）。
    /// </para>
    /// <para>
    /// <b>启停只有这一个入口</b>：资源树上的 <c>crd0.running</c>/<c>crd0.done</c> 是只读镜像，
    /// 驱动拒绝写 <c>crd{C}.start</c>/<c>crd{C}.abort</c>，避免同一实体动作两处入口。
    /// </para>
    /// <para>
    /// <b>典型调用顺序</b>：
    /// <list type="number">
    /// <item>换型（品种变了、轨迹变了）：<see cref="DownloadProgram"/> 一次</item>
    /// <item>每颗产品：<see cref="SetGlobalTransform"/> → <see cref="StartProgram"/></item>
    /// <item>异常 / 收工：<see cref="StopProgram"/>（急停走卡会话的 <c>EmgStop</c>）</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>位姿变化后要不要重新装载轨迹，由实现负责，调用方不用管</b>：
    /// 卡支持卡内变换（程序常驻 + 变量传位姿）时只需写几个变量；
    /// 卡不支持（只能整包下发一段程序）时，实现内部标记脏、到 <see cref="StartProgram"/> 时
    /// 用缓存的指令集重下。这条约定让上层流程对不同品牌保持一致。
    /// </para>
    /// </remarks>
    public interface IMotionCardService
    {
        /// <summary>
        /// 卡上是否有一份<b>完整</b>装载好的轨迹（<see cref="StartProgram"/> 的前置条件）。
        /// </summary>
        /// <remarks>装载中途失败 / 超时必须为 <c>false</c>，否则就可能去跑半条轨迹。</remarks>
        bool IsProgramLoaded { get; }

        /// <summary>
        /// 装载整套轨迹（<b>只装载，不启动</b>）。
        /// </summary>
        /// <param name="commands">
        /// 由 <c>DispenseProgram.BuildMotionCardCommands</c> 生成的厂商无关指令集。
        /// 落成什么形态（卡上程序 / G 代码文件 / 连续插补列表）由实现经
        /// <see cref="IMotionProgramWriter"/> 决定。
        /// </param>
        /// <returns>是否下发成功；失败抛异常并带原因。</returns>
        bool DownloadProgram(MotionCardCommandSet commands);

        /// <summary>
        /// 设置本颗产品的工件位姿偏移（旋转中心 + 平移 + 转角）。
        /// </summary>
        /// <remarks>
        /// 变换约定与 <c>TrajectorySegment.Transform</c> 一致：先绕旋转中心转 theta，再平移 (dx, dy)，Z 不参与。
        /// <b>调用后是否必须重新 <see cref="DownloadProgram"/></b> 由实现内部处理（见接口备注）。
        /// </remarks>
        /// <param name="dx">X 方向平移量 mm</param>
        /// <param name="dy">Y 方向平移量 mm</param>
        /// <param name="theta">绕 Z 轴旋转角，弧度</param>
        /// <param name="rotCenter">旋转中心</param>
        bool SetGlobalTransform(double dx, double dy, double theta, Point3D rotCenter);

        /// <summary>启动已装载的轨迹。</summary>
        /// <exception cref="System.InvalidOperationException">
        /// <see cref="IsProgramLoaded"/> 为 <c>false</c> 时拒绝启动——跑半条轨迹比不跑危险得多。
        /// </exception>
        bool StartProgram();

        /// <summary>停止运动与出胶（减速停止；急停走卡会话的 <c>EmgStop</c>）。</summary>
        bool StopProgram();
    }
}

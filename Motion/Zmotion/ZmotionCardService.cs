using Core.Motion;
using Core.Motion.Zmotion;
using Core.Zmotion;
using System;

namespace Motion.Zmotion
{
    /// <summary>
    /// 正运动工艺通道：<b>卡上程序常驻，每颗产品只传位姿</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么能"下载一次、每次只传位姿"</b>：正运动把程序（含变换公式）常驻在卡上，
    /// <see cref="SetGlobalTransform"/> 只写几个 VR，卡上循环自己算旋转矩阵。
    /// 不支持卡内变换的卡做不到这一点——那种实现必须在位姿变化后重下整段轨迹，
    /// 但两者都满足 <see cref="IMotionCardService"/> 的调用约定，所以上层流程不用分支。
    /// </para>
    /// <para>
    /// <b>本类拿不到连接手段</b>：构造参数是 <see cref="IZmotionCard"/>（业务能力），
    /// 里面没有 <c>Open</c>/<c>Close</c>——连接归 <see cref="ZmotionCardHost"/> 一家，
    /// 与其他用卡者（数据点驱动）共享同一个宿主。这样"各连一次""谁把卡关了"在编译期就不成立。
    /// 需要确保卡是通的，由组合根连接一次、或由数据点驱动调宿主的
    /// <see cref="IMotionCardConnector.EnsureConnected"/>（幂等）。
    /// </para>
    /// <para>
    /// <b>上位机的标准流程</b>：
    /// <list type="number">
    /// <item>换型：<see cref="DownloadProgram"/>（<see cref="LoadGeneration"/> 才会增加）</item>
    /// <item>每颗产品：<see cref="SetGlobalTransform"/> → <see cref="StartProgram"/> →
    /// 轮询 <see cref="IsCycleDone"/> / <see cref="ReadCardError"/></item>
    /// <item>异常 / 收工：<see cref="StopProgram"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>骨架状态</b>：所有真 API 调用都走注入的 <see cref="IZmotionCard"/>，尚未接入真实库
    /// （未接时调用会抛 <see cref="ZmotionApiNotBoundException"/>，绝不返回假成功）。
    /// 生成的卡上程序在"轨迹含 IO 事件"时会置错误码
    /// <see cref="ZmotionProgramContract.ErrorIoNotImplemented"/> 拒绝运行——阀门时序（PSO /
    /// <c>HW_PSWITCH</c>）参数序核实前，宁可拒绝也不空跑不出胶。上位机必须读这个码并停机报警。
    /// </para>
    /// </remarks>
    public sealed class ZmotionCardService : IMotionCardService
    {
        private readonly IZmotionCard card;
        private readonly IMotionProgramWriter writer;

        private volatile bool loadCompleted;
        private long loadGeneration;

        /// <param name="card">
        /// 卡的业务能力视图（典型传法：<c>ZmotionCardHost.ForEndpoint(ip, factory).Card</c>）；
        /// 故意不含连接手段，连接与服务无关。
        /// </param>
        /// <param name="writer">转换器；缺省用 <see cref="ZmotionProgramWriter"/></param>
        public ZmotionCardService(IZmotionCard card, IMotionProgramWriter? writer = null)
        {
            this.card = card ?? throw new ArgumentNullException(nameof(card));
            this.writer = writer ?? new ZmotionProgramWriter();
        }

        /// <summary>已装载次数：只有换型（重新下发轨迹）才该增加。</summary>
        public long LoadGeneration => loadGeneration;

        /// <summary>最近一次设置的位姿（诊断 / 追溯用）。</summary>
        public PoseOffset LastPose { get; private set; }

        /// <summary>当前转换器格式名（日志用）。</summary>
        public string WriterFormat => writer.FormatName;

        /// <summary>卡是否在线（只读诊断；连接不归本服务管）。</summary>
        public bool IsConnected => card.IsConnected;

        /// <summary>
        /// 本次装载是否做过"从卡上回读"的校验。
        /// </summary>
        /// <remarks>
        /// <c>false</c> 不代表失败，而是代表<b>这套 SDK 校验不了</b>（ZMotionSDK 没有文件存在性查询）。
        /// 联调时要靠别的证据确认下载生效（读 <c>VR(7)</c> 是否被程序改写、或改用 3FileRam 空间差值）。
        /// 这个属性存在的唯一目的，就是不许把"没校验"说成"校验通过"。
        /// </remarks>
        public bool ProgramReadBackVerified { get; private set; }

        /// <inheritdoc />
        public bool IsProgramLoaded => loadCompleted;

        /// <inheritdoc />
        public bool DownloadProgram(MotionCardCommandSet commands)
        {
            if (commands is null)
            {
                throw new ArgumentNullException(nameof(commands));
            }

            EnsureConnected();

            // 程序区即将被覆盖：先标脏，全程成功才置回真。
            // 中途失败（含超时）只会让 StartProgram 拒绝启动，不会跑半条轨迹
            loadCompleted = false;

            MotionProgramPayload payload = writer.Write(commands);

            card.DownloadProgram(payload.Name, payload.Text);

            // 写后回读校验：杜绝"协议 ACK 但程序没写进去"的假成功。
            // 卡不支持回读时（见 IZmotionCard.SupportsProgramReadBack）不假装校验过，只记"未校验"
            ProgramReadBackVerified = false;

            if (card.SupportsProgramReadBack)
            {
                if (!card.ProgramExists(payload.Name))
                {
                    throw new InvalidOperationException(
                        $"下发返回成功，但卡上找不到程序 {payload.Name}（下载没有生效）");
                }

                ProgramReadBackVerified = true;
            }

            card.SetCurrentProgram(payload.Name);

            loadCompleted = true;
            loadGeneration++;
            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 只写 VR（<see cref="ZmotionProgramContract.VrPoseDx"/> 等），<b>不重下轨迹、不做上位机变换</b>。
        /// 卡上程序在这个位姿下自己折算每个点，并先做软限位自检再动。
        /// </remarks>
        public bool SetGlobalTransform(double dx, double dy, double theta, Point3D rotCenter)
        {
            EnsureConnected();

            card.WriteVr(ZmotionProgramContract.VrPoseDx, (float)dx);
            card.WriteVr(ZmotionProgramContract.VrPoseDy, (float)dy);
            card.WriteVr(ZmotionProgramContract.VrPoseTheta, (float)theta);
            card.WriteVr(ZmotionProgramContract.VrRotCenterX, (float)rotCenter.X);
            card.WriteVr(ZmotionProgramContract.VrRotCenterY, (float)rotCenter.Y);

            LastPose = new PoseOffset(dx, dy, theta, rotCenter);
            return true;
        }

        /// <inheritdoc />
        public bool StartProgram()
        {
            EnsureConnected();

            if (!loadCompleted)
            {
                throw new InvalidOperationException(
                    "轨迹未完整装载（装载失败 / 从未下发），拒绝启动；请重新调用 DownloadProgram");
            }

            // 常驻程序只在第一次启动时建任务（Run 幂等），之后每颗产品只靠 VR 触发
            card.Run();

            card.WriteVr(ZmotionProgramContract.VrDone, 0);
            card.WriteVr(ZmotionProgramContract.VrRunRequest, 1);
            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 常驻程序<b>不因停止而失效</b>：程序还在卡上，下一颗产品可以直接再触发。
        /// （"一停就得重新装载"的实现也存在——这是卡能力差异导致的，
        /// 调用方不应假定 <c>Stop</c> 之后 <see cref="IsProgramLoaded"/> 的取值，只应读它。）
        /// </remarks>
        public bool StopProgram()
        {
            EnsureConnected();

            // 先置中止位（卡上程序每段都会判它，不会接着往下发指令），再停运动：双保险
            card.WriteVr(ZmotionProgramContract.VrAbortRequest, 1);
            card.Stop();
            return true;
        }

        // ───────────────── 状态位：上位机状态机靠它 ─────────────────

        /// <summary>本颗产品是否跑完（卡上程序置位 <see cref="ZmotionProgramContract.VrDone"/>）。</summary>
        public bool IsCycleDone => card.ReadVr(ZmotionProgramContract.VrDone) != 0;

        /// <summary>
        /// 读卡上程序的错误码。<b>非 0 必须停机报警</b>：
        /// <see cref="ZmotionProgramContract.ErrorIoNotImplemented"/> = 骨架还没生成阀门时序；
        /// 9002 = 变换后坐标越软限位。
        /// </summary>
        public int ReadCardError() => (int)card.ReadVr(ZmotionProgramContract.VrError);

        /// <summary>
        /// 没连卡就直接给出明确原因，而不是让底层 API 抛一个看不懂的错误码。
        /// </summary>
        private void EnsureConnected()
        {
            if (!card.IsConnected)
            {
                throw new InvalidOperationException(
                    "正运动卡未连接：连接归 ZmotionCardHost（组合根连一次，数据点驱动用 EnsureConnected 幂等确保），本服务不负责连接");
            }
        }
    }
}

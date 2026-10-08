using Core.Motion;
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
    /// <b>上位机的标准流程</b>：
    /// <list type="number">
    /// <item>换型：<see cref="DownloadProgram"/>（<see cref="LoadGeneration"/> 才会增加）</item>
    /// <item>每颗产品：<see cref="SetGlobalTransform"/> → <see cref="StartProgram"/> →
    /// 轮询 <see cref="IsCycleDone"/> / <see cref="ReadCardError"/></item>
    /// <item>异常 / 收工：<see cref="StopProgram"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>骨架状态</b>：所有真 API 调用都走 <see cref="IZmotionApi"/>，尚未接入真实库
    /// （未接时调用会抛 <see cref="ZmotionApiNotBoundException"/>，绝不返回假成功）。
    /// 生成的卡上程序在"轨迹含 IO 事件"时会置错误码
    /// <see cref="ZmotionProgramContract.ErrorIoNotImplemented"/> 拒绝运行——阀门时序（PSO /
    /// <c>HW_PSWITCH</c>）参数序核实前，宁可拒绝也不空跑不出胶。上位机必须读这个码并停机报警。
    /// </para>
    /// </remarks>
    public sealed class ZmotionCardService : IMotionCardService, IMotionCardLink
    {
        private readonly IZmotionApi api;
        private readonly IMotionProgramWriter writer;

        private volatile bool loadCompleted;
        private long loadGeneration;

        /// <param name="api">正运动 PC 函数库收口点（骨架期可传真库实现或测试替身）</param>
        /// <param name="writer">转换器；缺省用 <see cref="ZmotionProgramWriter"/></param>
        public ZmotionCardService(IZmotionApi api, IMotionProgramWriter? writer = null)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.writer = writer ?? new ZmotionProgramWriter();
        }

        /// <summary>已装载次数：只有换型（重新下发轨迹）才该增加。</summary>
        public long LoadGeneration => loadGeneration;

        /// <summary>最近一次设置的位姿（诊断 / 追溯用）。</summary>
        public PoseOffset LastPose { get; private set; }

        /// <summary>当前转换器格式名（日志用）。</summary>
        public string WriterFormat => writer.FormatName;

        // ───────────────── IMotionCardLink：连接 ─────────────────

        /// <inheritdoc />
        public bool IsConnected => api.IsConnected;

        /// <inheritdoc />
        public void Connect(string endpoint) => api.Open(endpoint);

        /// <inheritdoc />
        public void Disconnect() => api.Close();

        // ───────────────── IMotionCardService：工艺 ─────────────────

        /// <inheritdoc />
        public bool IsProgramLoaded => loadCompleted;

        /// <inheritdoc />
        public bool DownloadProgram(MotionCardCommandSet commands)
        {
            if (commands is null)
            {
                throw new ArgumentNullException(nameof(commands));
            }

            // 程序区即将被覆盖：先标脏，全程成功才置回真。
            // 中途失败（含超时）只会让 StartProgram 拒绝启动，不会跑半条轨迹
            loadCompleted = false;

            MotionProgramPayload payload = writer.Write(commands);

            api.DownloadProgram(payload.Name, payload.Text);

            // 写后回读校验：杜绝"协议 ACK 但程序没写进去"的假成功
            if (!api.ProgramExists(payload.Name))
            {
                throw new InvalidOperationException(
                    $"下发返回成功，但卡上找不到程序 {payload.Name}（下载没有生效）");
            }

            api.SetCurrentProgram(payload.Name);

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
            api.WriteVr(ZmotionProgramContract.VrPoseDx, (float)dx);
            api.WriteVr(ZmotionProgramContract.VrPoseDy, (float)dy);
            api.WriteVr(ZmotionProgramContract.VrPoseTheta, (float)theta);
            api.WriteVr(ZmotionProgramContract.VrRotCenterX, (float)rotCenter.X);
            api.WriteVr(ZmotionProgramContract.VrRotCenterY, (float)rotCenter.Y);

            LastPose = new PoseOffset(dx, dy, theta, rotCenter);
            return true;
        }

        /// <inheritdoc />
        public bool StartProgram()
        {
            if (!loadCompleted)
            {
                throw new InvalidOperationException(
                    "轨迹未完整装载（装载失败 / 从未下发），拒绝启动；请重新调用 DownloadProgram");
            }

            api.WriteVr(ZmotionProgramContract.VrDone, 0);
            api.WriteVr(ZmotionProgramContract.VrRunRequest, 1);
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
            api.Stop();
            return true;
        }

        // ───────────────── 状态位：上位机状态机靠它 ─────────────────

        /// <summary>本颗产品是否跑完（卡上程序置位 <see cref="ZmotionProgramContract.VrDone"/>）。</summary>
        public bool IsCycleDone => api.ReadVr(ZmotionProgramContract.VrDone) != 0;

        /// <summary>
        /// 读卡上程序的错误码。<b>非 0 必须停机报警</b>：
        /// <see cref="ZmotionProgramContract.ErrorIoNotImplemented"/> = 骨架还没生成阀门时序；
        /// 9002 = 变换后坐标越软限位。
        /// </summary>
        public int ReadCardError() => (int)api.ReadVr(ZmotionProgramContract.VrError);
    }
}

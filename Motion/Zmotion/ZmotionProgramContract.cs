namespace Motion.Zmotion
{
    /// <summary>
    /// 上位机与卡上程序之间的<b>数据契约</b>：VR 通道号与轨迹表布局。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上位机侧（<see cref="ZmotionCardService"/>）按这里的编号写 VR，
    /// 卡上程序（<see cref="ZmotionProgramWriter"/> 生成的 ZBasic）按同一编号读。
    /// 契约写成代码、两边都引用它，是为了不让"写的人"和"读的人"各记一套编号而漂移。
    /// </para>
    /// <para>
    /// <b>本契约按官方手册片段与文章撰写，尚未与手册正文逐条核对</b>（正运动官网对程序化抓取返回 403、
    /// PDF 直链 406）。首次联调必须核对：
    /// <list type="bullet">
    /// <item>VR / TABLE 的上限，以及 PC 侧读写函数名（<c>ZAux_Direct_*</c> 家族）</item>
    /// <item>程序下载 / 运行 / 停止的函数名（官方文章《RtBasic文件下载与连续轨迹加工的Python+Qt开发》）</item>
    /// <item>位置同步输出 PSO / <c>HW_PSWITCH</c> 的参数序（官方文章《精密点胶的PSO应用》）</item>
    /// <item>连续插补（缓冲）指令名，以及 <c>UNITS</c>、轴映射 <c>BASE</c> 的写法</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static class ZmotionProgramContract
    {
        // ── 位姿通道：上位机每颗产品只写这几个 ──

        /// <summary>X 方向平移量（mm）。</summary>
        public const int VrPoseDx = 0;

        /// <summary>Y 方向平移量（mm）。</summary>
        public const int VrPoseDy = 1;

        /// <summary>绕 Z 轴旋转角（弧度）。</summary>
        public const int VrPoseTheta = 2;

        /// <summary>旋转中心 X（mm）。标定后固定，换型时才写。</summary>
        public const int VrRotCenterX = 3;

        /// <summary>旋转中心 Y（mm）。</summary>
        public const int VrRotCenterY = 4;

        // ── 握手 ──

        /// <summary>运行请求：上位机写完位姿后置 1，卡上程序收下后清零。</summary>
        public const int VrRunRequest = 5;

        /// <summary>完成标志：卡上程序跑完一颗置 1，上位机清零。</summary>
        public const int VrDone = 6;

        /// <summary>错误码：非 0 = 拒绝启动 / 运行异常（<see cref="ErrorIoNotImplemented"/> 见下）。</summary>
        public const int VrError = 7;

        /// <summary>
        /// 骨架未实现阀门时序（PSO / <c>HW_PSWITCH</c> 参数序待核）时，卡上程序置的错误码。
        /// </summary>
        /// <remarks>
        /// 有这个码的意义：<b>宁可拒绝运行也不静默不出胶</b>。空跑一遍轨迹而不开阀，
        /// 在点胶机上等于把产品白白走一遍（还可能撞针），必须让上位机看见失败。
        /// </remarks>
        public const int ErrorIoNotImplemented = 9001;

        // ── 轨迹数据表：每段 <see cref="TableStride"/> 个数 ──

        /// <summary>每段占用的表项数。</summary>
        public const int TableStride = 7;

        /// <summary>段终点 X（mm，工件坐标）。</summary>
        public const int TableOffsetEndX = 0;

        /// <summary>段终点 Y（mm，工件坐标）。</summary>
        public const int TableOffsetEndY = 1;

        /// <summary>段速度（mm/s）。</summary>
        public const int TableOffsetSpeed = 2;

        /// <summary>段模式：<see cref="SegmentModeMove"/> = 空移，<see cref="SegmentModeDispense"/> = 点胶。</summary>
        public const int TableOffsetMode = 3;

        /// <summary>圆弧圆心 X（mm，绝对，工件坐标）；直线段填 0。</summary>
        public const int TableOffsetCenterX = 4;

        /// <summary>圆弧圆心 Y（mm，绝对，工件坐标）；直线段填 0。</summary>
        public const int TableOffsetCenterY = 5;

        /// <summary>
        /// 段几何类型：<see cref="SegmentKindLine"/> = 直线，<see cref="SegmentKindArc"/> = 圆弧。
        /// </summary>
        /// <remarks>
        /// 单独占一列而不是"圆心为 0 就当直线"：过原点的圆弧会被误判成直线，这种错在轨迹上很难看出来。
        /// </remarks>
        public const int TableOffsetSegType = 6;

        /// <summary>段几何类型：直线。</summary>
        public const double SegmentKindLine = 0;

        /// <summary>段几何类型：圆弧（由"终点 + 圆心"定义）。</summary>
        public const double SegmentKindArc = 1;

        /// <summary>空移（不出胶）。</summary>
        public const double SegmentModeMove = 0;

        /// <summary>点胶（出胶）。</summary>
        public const double SegmentModeDispense = 1;

        // ── IO 事件表：每事件 <see cref="IoStride"/> 个数 ──

        /// <summary>每个 IO 事件占用的表项数（段号、段内行程、开关）。</summary>
        public const int IoStride = 3;

        /// <summary>IO 事件：所属段号。</summary>
        public const int IoOffsetMark = 0;

        /// <summary>IO 事件：自段起点起的行程（mm）。</summary>
        public const int IoOffsetDistance = 1;

        /// <summary>IO 事件：1 = 开阀，0 = 关阀。</summary>
        public const int IoOffsetOn = 2;

        /// <summary>轨迹表在 TABLE 里的起始下标。</summary>
        public const int TableBaseSegments = 0;

        /// <summary>IO 事件表在 TABLE 里的起始下标（由生成器按段数偏移后再用）。</summary>
        /// <remarks>段表占 <c>段数 × TableStride</c> 项，IO 表紧跟其后，所以这里只是基址。</remarks>
        public const int IoBaseAfterSegments = 0;

        /// <summary>
        /// 软限位（mm）：变换后的任一点越界就拒绝启动。
        /// </summary>
        /// <remarks>
        /// 先按常量占位，接配置后应由设备行程注入（<c>App/production-line.json</c> 一类）。
        /// 卡上程序里这道自检是"把错误挡在动作之前"的最后一道闸。
        /// </remarks>
        public const double SoftLimitXMin = 0.0;

        /// <summary>软限位 X 上限（mm）。</summary>
        public const double SoftLimitXMax = 500.0;

        /// <summary>软限位 Y 下限（mm）。</summary>
        public const double SoftLimitYMin = 0.0;

        /// <summary>软限位 Y 上限（mm）。</summary>
        public const double SoftLimitYMax = 500.0;
    }
}

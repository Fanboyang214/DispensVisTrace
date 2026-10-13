using System;

namespace Core.Motion.Zmotion
{
    /// <summary>
    /// 连接生命周期：<b>只有连接宿主</b>（<c>Motion.Zmotion.ZmotionCardHost</c>）持有这一半。
    /// </summary>
    /// <remarks>
    /// 故意和业务能力拆开：业务代码只拿 <see cref="IZmotionCard"/>，
    /// 于是"自己 Open 一次"在编译期就写不出来（见 <c>Motion.Zmotion.ZmotionCardHost</c> 的说明）。
    /// </remarks>
    public interface IZmotionConnection
    {
        /// <summary>卡是否已连接。</summary>
        bool IsConnected { get; }

        /// <summary>连接（幂等：已连接时直接返回）。<c>待核</c>：以太网连接函数（<c>ZAux_OpenEth</c> 一类）与句柄管理。</summary>
        void Open(string endpoint);

        /// <summary>断开（幂等）。</summary>
        void Close();
    }

    /// <summary>
    /// 业务能力：注入给工艺通道（<c>Motion.Zmotion.ZmotionCardService</c>）与数据点驱动。
    /// </summary>
    /// <remarks>
    /// <b>没有 Open / Close</b>——这是"同一张卡只有一个连接"的第一道保证：
    /// 想各连一次、或随手关掉对方，代码根本写不出来。
    /// </remarks>
    public interface IZmotionCard
    {
        /// <summary>卡是否已连接（只读状态；连接不归本接口管）。</summary>
        bool IsConnected { get; }

        /// <summary>
        /// 是否支持"从卡上回读程序是否存在"。
        /// </summary>
        /// <remarks>
        /// 存在的意义是<b>不许假成功</b>：有的 SDK 没有文件存在性查询（ZMotionSDK 就是如此），
        /// 那就明说"校验不了"，让上层跳过校验并在诊断里留痕，而不是返回一个恒真的
        /// <see cref="ProgramExists"/> 假装校验过了。
        /// </remarks>
        bool SupportsProgramReadBack { get; }

        /// <summary>把程序整包下载到控制器。<c>待核</c>：下载函数名与参数（见官方文章《RtBasic文件下载与连续轨迹加工的Python+Qt开发》）。</summary>
        void DownloadProgram(string name, string text);

        /// <summary>回读校验：程序是否真的在卡上。<c>待核</c>：函数名与"文件大小"语义。</summary>
        bool ProgramExists(string name);

        /// <summary>把程序设为当前程序 / 建立运行任务。<c>待核</c>：是否"常驻程序 + 变量触发"模型。</summary>
        void SetCurrentProgram(string name);

        /// <summary>启动当前程序 / 触发任务。<c>待核</c>：与 <see cref="WriteVr"/> 置运行标志两者的取舍。</summary>
        void Run();

        /// <summary>停止运动与出胶。</summary>
        void Stop();

        /// <summary>写 VR 变量（位姿通道）。<c>待核</c>：函数名与类型（<c>ZAux_Direct_SetVr*</c>）。</summary>
        void WriteVr(int index, float value);

        /// <summary>读 VR 变量。</summary>
        float ReadVr(int index);

        // 指令位置 mm
        float GetDpos(int axis);    
        // 当前速度 mm/s
        float GetSpeed(int axis);     
        // 静止/运动完成
        bool GetIsIdle(int axis);       
        // 回零完成
        bool GetHomeStatus(int axis);       
        // 点位运动
        void MoveAbsolute(int axis, float position);
        
        //jog运动
        void Jog(int axis, bool forward);
        //停止jog运动
        void StopJog(int axis);

        /// <summary>
        /// 批量读 axisCount 根轴的运行状态（位置 / 速度 / 回零 / 限位 / 报警）。
        /// </summary>
        /// <remarks>
        /// 界面 10 Hz 轮询用这一个调用，而不是逐项调 <see cref="GetDpos"/> / <see cref="GetSpeed"/>：
        /// 一张卡每多一次往返就多一次超时窗口，批量读把状态刷新压到一次通信里。
        /// </remarks>
        /// <param name="axisCount">轴数量（从 0 轴开始，通常 3：X/Y/Z）。</param>
        IReadOnlyList<AxisMotionSnapshot> ReadAxisStates(int axisCount);

        /// <summary>读一个控制器轴参数的当前值（参数名含义见 <see cref="AxisParameter"/>）。</summary>
        float ReadAxisParameter(int axis, AxisParameter parameter);

        /// <summary>
        /// 写一个控制器轴参数（单轴运动参数 / 回零参数 / 前瞻参数都走这里）。
        /// </summary>
        /// <remarks>
        /// 写的是<b>控制器参数</b>，立即生效；调用方负责先校验范围（例如软限位不能反过来）。
        /// </remarks>
        void WriteAxisParameter(int axis, AxisParameter parameter, float value);

        /// <summary>单轴相对运动（相对当前位置走 distance，mm）。</summary>
        void MoveRelative(int axis, float distance);

        /// <summary>
        /// 单轴回零。
        /// </summary>
        /// <param name="axis">轴号</param>
        /// <param name="mode">回零方式（0..7，含义见控制器手册与现场接线）</param>
        void GoHome(int axis, int mode);

        /// <summary>设置回零偏移（mm）：回零完成后把该轴坐标置为这个值。</summary>
        void SetHomeOffset(int axis, float offset);

        /// <summary>读回零偏移（mm）——与 <see cref="SetHomeOffset"/> 同一个控制器参数。</summary>
        float GetHomeOffset(int axis);


        // 报警/停止原因（可选，排障很有用）
        //int GetAxisStopReason(int axis);    
    }

    /// <summary>
    /// 正运动 PC 函数库（<c>ZAux_Direct_*</c> 家族）的<b>唯一收口点</b>：真 API 调用只允许出现在实现里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么有 SDK 还要这一层？</b>因为正运动的"SDK"是 handle 式<b>函数库</b>
    /// （手册里的调用形如 <c>ret = ZAux_Direct_SetAtype(handle, 0, AType)</c>，成功返 0、非 0 查错误码），
    /// 它提供的是<b>能力</b>，不含我们的<b>使用约定</b>。SDK 不会阻止你 <c>OpenEth</c> 两次，
    /// 不管句柄并发安全，也不知道"轨迹装了一半不许启动"。本接口的存在理由只有两条：
    /// </para>
    /// <list type="number">
    /// <item><b>给连接宿主一个可替换面</b>：句柄归 <c>Motion.Zmotion.ZmotionCardHost</c> 独占，
    /// 业务侧只拿到收窄后的视图（<see cref="IZmotionCard"/> / <c>IMotionCardConnector</c>）。</item>
    /// <item><b>让业务不变量能离线测试</b>：SDK 是静态 <c>extern</c>，卡不在场就完全跑不起来；
    /// 而"装载世代保护""装完才允许启动""回读校验""两个消费者只连一次"这些不变量恰恰最容易写错、
    /// 也最贵（撞针 / 过胶）。有一个可替换面，就能用替身把它们钉在回归里，不必等真机。</item>
    /// </list>
    /// <para>
    /// <b>什么时候该删掉这一层：</b>如果到货后发现 SDK 提供的是<b>可实例化的面向对象封装</b>
    /// （<c>new ZmotionController(ip)</c> 一类，内部自己管句柄），那就别包两层——
    /// 直接让 <c>Motion.Zmotion.ZmotionCardHost</c> 持有 SDK 的对象，本接口连同 <see cref="IZmotionConnection"/> 一起删。
    /// 反之若确如手册所示是 handle 式函数库（大概率），保留。
    /// <b>无论哪种，<c>Motion.Zmotion.ZmotionCardHost</c>（唯一连接 + 幂等）与
    /// <see cref="IZmotionCard"/> / <c>IMotionCardConnector</c> 两个视图都必须保留</b>——
    /// 它们解决的是"谁敢连、谁不能断"，不是抽象崇拜。
    /// </para>
    /// <para>
    /// <b>实现会同时具备两半</b>（<see cref="IZmotionConnection"/> + <see cref="IZmotionCard"/>），
    /// 但只应该被 <c>Motion.Zmotion.ZmotionCardHost</c> 持有；业务侧拿到的永远是
    /// <see cref="IZmotionCard"/> 这一个视图。真库实现建议放在<b>与本文件同程序集</b>并把构造函数
    /// 设为 <c>internal</c>，这样外面连"造一个 API 实例"都做不到，绕过宿主的可能就被封死了。
    /// </para>
    /// <para>
    /// 实现方注意：<b>卡句柄不保证并发安全</b>，实现内部要自己串行化。建议两把锁：
    /// 一把锁"每个原生调用"（防库本身不可重入），一把锁"多步命令序列"（防装载到一半被启停插进来），
    /// 且<b>读点只取前者</b>——这样长序列不会把采集周期堵死。
    /// </para>
    /// </remarks>
    public interface IZmotionApi : IZmotionConnection, IZmotionCard
    {
        /// <summary>
        /// 卡是否已连接。
        /// </summary>
        /// <remarks>
        /// 两半基接口都有这个成员，这里显式合并一次，否则 <c>api.IsConnected</c> 会报 CS0229 二义性。
        /// </remarks>
        new bool IsConnected { get; }
    }

    /// <summary>
    /// 未接入真实 API 时抛出的异常。
    /// </summary>
    /// <remarks>
    /// 骨架期故意让每个未实现的调用"响亮地失败"：绝不返回假成功，
    /// 否则现场会出现"上位机说下发成功、卡上什么都没有"这种最难查的故障。
    /// </remarks>
    public sealed class ZmotionApiNotBoundException : NotSupportedException
    {
        public ZmotionApiNotBoundException(string member)
            : base($"正运动 API 尚未接入：{member}（待实现 IZmotionApi，见 Core/Motion/Zmotion/ZmotionCardApi.cs）")
        {
        }
    }
}

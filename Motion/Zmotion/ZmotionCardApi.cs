using System;

namespace Motion.Zmotion
{
    /// <summary>
    /// 正运动 PC 函数库（<c>ZAux_Direct_*</c> 家族）的<b>唯一收口点</b>：真 API 调用只允许出现在实现里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存在的意义：硬件还没到、手册还没逐条核，业务骨架（装载世代、位姿下发、启停顺序、回读校验）
    /// 现在就能写完并单测；到货后只需实现本接口，把注释里的"待核"逐条填上，上层一行不动。
    /// </para>
    /// <para>
    /// 实现方注意：<b>卡句柄不保证并发安全</b>，实现内部要自己串行化。建议两把锁：
    /// 一把锁"每个原生调用"（防库本身不可重入），一把锁"多步命令序列"（防装载到一半被启停插进来），
    /// 且<b>读点只取前者</b>——这样长序列不会把采集周期堵死。
    /// </para>
    /// </remarks>
    public interface IZmotionApi
    {
        /// <summary>卡是否已连接。</summary>
        bool IsConnected { get; }

        /// <summary>连接（幂等）。<c>待核</c>：以太网连接函数（<c>ZAux_OpenEth</c> 一类）与句柄管理。</summary>
        void Open(string endpoint);

        /// <summary>断开（幂等）。</summary>
        void Close();

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
            : base($"正运动 API 尚未接入：{member}（待实现 IZmotionApi，见 Motion/Zmotion/ZmotionCardApi.cs）")
        {
        }
    }
}

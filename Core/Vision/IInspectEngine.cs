using Core.Models;
using System;

namespace Core.Vision
{
    public interface IInspectEngine : IDisposable
    {
        /// <summary>
        /// 初始化引擎（读取配置、加载模板）。重复调用必须幂等。
        /// </summary>
        /// <returns>是否初始化成功；失败时禁止调用 <see cref="Inspect"/>。</returns>
        bool Initialize(InspectConfig config);

        /// <summary>
        /// 对指定图像执行一次缺陷检测。
        /// 线程安全：同一实例不保证并发安全，调用方需自行串行化。
        /// </summary>
        /// <param name="image">待检测的图像。</param>
        void Inspect(InspectionImage image);

        /// <summary>
        /// 最近一次 <see cref="Inspect"/> 产出的置信度 / 得分（就是与配置阈值相比的那个值）；
        /// 没跑过、取图失败或中途异常时为 <c>null</c>。
        /// </summary>
        /// <remarks>
        /// 引擎只负责把值说出来，<b>不负责上报</b>：上报（如资源树的 <c>cam/inspect-conf</c>）
        /// 由调用方通过 <c>IDataPointPublisher</c> 做——引擎不该知道 MQTT / 资源树的存在。
        /// </remarks>
        double? LastConfidence { get; }
    }
}

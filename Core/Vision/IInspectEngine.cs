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
    }
}

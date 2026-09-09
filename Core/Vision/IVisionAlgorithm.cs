using Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Core.Vision
{
    /// <summary>
    /// 所有视觉检测算法引擎的统一抽象。
    /// 使用完毕后必须调用 Dispose 释放引擎句柄和模型资源。
    /// </summary>
    public interface IVisionAlgorithm:IDisposable
    {
        /// <summary>
        /// 引擎名称，用于追溯与日志，如 "OpenCvSharp" / "Halcon" / "VisionMaster"。
        /// </summary>
        public string AlgorithmName { get; }
        /// <summary>
        /// 引擎是否初始化
        /// 未初始化禁止调用<see cref="DetectAsync"/>
        /// </summary>
        public bool IsInitiated { get; }
        /// <summary>
        /// 初始化引擎（加载模型、配置参数、申请句柄等）。
        /// 重复调用必须幂等。
        /// </summary>
        /// <param name="config"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        Task InitializeAsync(AlgorithmConfig config, CancellationToken ct);
        /// <summary>
        /// 执行一次定位检测并返回结果。
        /// 线程安全：同一实例不保证并发安全，调用方需自行串行化。
        /// </summary>
        /// <param name="image"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        Task DetectLocateAsync(InspectionImage image, CancellationToken ct);

        /// <summary>
        /// 执行一次缺陷检测并返回结果。
        /// 线程安全：同一实例不保证并发安全，调用方需自行串行化。
        /// </summary>
        /// <param name="image"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        Task DetectInspectAsync(InspectionImage image, CancellationToken ct);

    }
}

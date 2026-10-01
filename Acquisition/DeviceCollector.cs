using Core.Interfaces;
using Core.Logging;
using Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Acquisition
{
    /// <summary>
    /// 单设备采集调度器 — 基于 <see cref="PeriodicTimer"/> 的长期运行采集循环。
    /// 每个 <see cref="DeviceConfig"/> 对应一个 Collector 实例，
    /// 由上层 <see cref="AcquisitionHost"/> 管理生命周期。
    /// </summary>
    public sealed class DeviceCollector
    {
        private readonly DeviceConfig _device;
        private readonly IProtocolDriver _driver;
        private readonly ChannelWriter<TagValue> _writer;
        private readonly ILogService _log;
        private int _consecutiveFailures;

        /// <summary>
        /// 初始化设备采集器。
        /// </summary>
        /// <param name="device">设备配置（周期、超时、重试等）</param>
        /// <param name="driver">协议驱动实例（由工厂创建）</param>
        /// <param name="writer">采集数据管道的写入端</param>
        /// <param name="log">日志服务；本类在构造时自行取 <see cref="DeviceCollector"/> 上下文的子日志器</param>
        public DeviceCollector(
            DeviceConfig device,
            IProtocolDriver driver,
            ChannelWriter<TagValue> writer,
            ILogService log)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<DeviceCollector>();
        }

        /// <summary>
        /// 启动采集循环。此方法会持续运行直到 <paramref name="ct"/> 被取消。
        /// </summary>
        /// <param name="ct">外部取消令牌，用于热重载时优雅停止当前采集任务。</param>
        public async Task RunAsync(CancellationToken ct)
        {
            _log.Info("设备 {DeviceName} 采集器启动 (周期 {CycleMs}ms)", _device.Name, _device.CycleTimeMs);

            // 首次连接（带重试）
            await ConnectWithRetryAsync(ct);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_device.CycleTimeMs));

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // 连接断开时尝试重连
                if (!_driver.IsConnected)
                {
                    _log.Warn("设备 {DeviceName} 连接已断开，尝试重连", _device.Name);
                    await ConnectWithRetryAsync(ct);

                    if (!_driver.IsConnected)
                        continue; // 重连失败，跳过本轮采集，等待下个周期
                }

                try
                {
                    IReadOnlyList<TagValue> values =
                        await _driver.ReadTagsAsync(_device.Tags, ct).ConfigureAwait(false);

                    foreach (TagValue value in values)
                    {
                        await _writer.WriteAsync(value, ct).ConfigureAwait(false);
                    }

                    // 采集成功，重置失败计数器
                    _consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 外部取消是正常退出路径，不记录错误
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _log.Error(ex,
                        "设备 {DeviceName} 采集失败 (连续失败 {Count} 次)",
                        _device.Name, _consecutiveFailures);

                    // 标记驱动为断开状态，触发下轮重连
                    try { await _driver.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* 尽力清理 */ }

                    // 指数退避 + 抖动
                    await DelayWithBackoffAsync(_consecutiveFailures, ct).ConfigureAwait(false);
                }
            }

            _log.Info("设备 {DeviceName} 采集器已停止", _device.Name);
        }

        /// <summary>
        /// 指数退避 + ±20% 抖动，防止惊群效应。
        /// 基数 1s，每次翻倍，上限 60s。
        /// </summary>
        private async Task DelayWithBackoffAsync(int failureCount, CancellationToken ct)
        {
            // 基数 1s，指数增长：1s, 2s, 4s, 8s, 16s, 32s, 64s (封顶)
            double baseMs = 1000.0 * Math.Pow(2, Math.Min(failureCount - 1, 6));

            // ±20% 随机抖动，避免多个采集器同时重试
            double jitterFactor = 1.0 + (Random.Shared.NextDouble() * 0.4 - 0.2); // [0.8, 1.2]
            double delayMs = Math.Clamp(baseMs * jitterFactor, 100, 60_000);

            _log.Debug("设备 {DeviceName} 退避等待 {DelayMs:F0}ms 后重试", _device.Name, delayMs);

            try 
            {
                await Task.Delay((int)delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消期间忽略延迟中断，由外层循环处理
            }
        }

        /// <summary>
        /// 带重试的连接逻辑。指数退避直到连接成功或外部取消。
        /// </summary>
        private async Task ConnectWithRetryAsync(CancellationToken ct)
        {
            int attempt = 0;

            while (!ct.IsCancellationRequested && !_driver.IsConnected)
            {
                attempt++;
                try
                {
                    await _driver.ConnectAsync(ct).ConfigureAwait(false);
                    _consecutiveFailures = 0;
                    _log.Info("设备 {DeviceName} 连接成功 (第 {Attempt} 次尝试)", _device.Name, attempt);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    bool exceeded = attempt >= _device.RetryCount;

                    // 超过重试上限记 Error，未超过记 Warn（原 MEL 版用变量选级别，这里显式分派，
                    // 因为 ILogService 的便捷方法按级别固定为 Error/Warn 等独立方法）
                    if (exceeded)
                    {
                        _log.Error(ex,
                            "设备 {DeviceName} 连接失败 (第 {Attempt}/{MaxRetry} 次尝试)",
                            _device.Name, attempt, _device.RetryCount);

                        // 超过重试上限后仍然继续尝试，但降低日志级别避免刷屏
                        _log.Error("设备 {DeviceName} 已达最大重试次数 {MaxRetry}，将持续尝试重连", _device.Name, _device.RetryCount);
                    }
                    else
                    {
                        _log.Warn(ex,
                            "设备 {DeviceName} 连接失败 (第 {Attempt}/{MaxRetry} 次尝试)",
                            _device.Name, attempt, _device.RetryCount);
                    }

                    await DelayWithBackoffAsync(attempt, ct).ConfigureAwait(false);
                }
            }
        }
    }

}

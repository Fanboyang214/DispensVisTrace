using Core.Interfaces;
using Core.Logging;
using Core.Models;
using Microsoft.Extensions.Hosting;
using MvCameraControl;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Acquisition
{
    /// <summary>
    /// 采集宿主服务 — 管理所有设备的 <see cref="DeviceCollector"/> 生命周期，
    /// 支持启动、停止、热重载。作为 <see cref="IHostedService"/> 注册到 DI 容器。
    /// </summary>
    public sealed class AcquisitionHost : IHostedService
    {
        private readonly ConcurrentDictionary<string, DeviceRunState> _devices = new();
        private readonly IDriverFactory _driverFactory;
        private readonly AcquisitionChannel _channel;
        private readonly ILogService _log;
        /// <summary>
        /// 未加类型上下文的日志服务，专用于为子组件派生日志器。
        /// 不能改用 <see cref="_log"/>：<see cref="ILogService.ForContext"/> 是替换名称而非追加，
        /// 传下去会让 DeviceCollector 的日志来源错误地显示为 AcquisitionHost。
        /// </summary>
        private readonly ILogService _logFactoryService;
        private CancellationTokenSource? _hostCts;

        /// <summary>
        /// 初始化采集宿主。
        /// </summary>
        /// <param name="driverFactory">驱动工厂，用于为每台设备创建协议驱动</param>
        /// <param name="channel">共享采集管道，所有设备写入同一管道</param>
        /// <param name="log">日志服务；本类取 <see cref="AcquisitionHost"/> 上下文的子日志器，
        /// 同时保留原服务用于为每台设备的 <see cref="DeviceCollector"/> 派生日志器</param>
        public AcquisitionHost(
            IDriverFactory driverFactory,
            AcquisitionChannel channel,
            ILogService log)
        {
            _driverFactory = driverFactory ?? throw new ArgumentNullException(nameof(driverFactory));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _logFactoryService = log ?? throw new ArgumentNullException(nameof(log));
            _log = _logFactoryService.ForContext<AcquisitionHost>();
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _hostCts = new CancellationTokenSource();
            _log.Info("采集宿主已启动");
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _log.Info("采集宿主正在停止，等待所有设备采集任务退出...");

            // 优雅关闭所有设备
            string[] deviceIds = _devices.Keys.ToArray();
            foreach (string deviceId in deviceIds)
            {
                await StopDeviceAsync(deviceId);
            }

            _hostCts?.Cancel();
            _hostCts?.Dispose();

            // 标记管道写入完成，下游 HistoryWriter 收到 Completion 后退出
            _channel.Writer.Complete();

            _log.Info("采集宿主已停止");
        }

        /// <summary>
        /// 启动单台设备的采集任务。
        /// </summary>
        /// <param name="config">设备配置</param>
        public async Task StartDeviceAsync(DeviceConfig config)
        {
            if (_hostCts is null)
                throw new InvalidOperationException("采集宿主尚未启动，请先调用 StartAsync");

            if (_devices.ContainsKey(config.Id))
            {
                _log.Warn("设备 {DeviceName} (ID:{DeviceId}) 已在运行中，跳过", config.Name, config.Id);
                return;
            }

            // 通过工厂创建驱动
            IProtocolDriver driver = await _driverFactory.CreateDriverAsync(config, _hostCts.Token)
                .ConfigureAwait(false);

            // 为每台设备创建独立的取消令牌（链接到宿主令牌）
            var deviceCts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);

            // DeviceCollector 自行按类型取子日志器，这里把共享的日志服务传下去即可
            var collector = new DeviceCollector(config, driver, _channel.Writer, _logFactoryService);

            // 启动采集循环 Task
            Task runTask = Task.Run(
                async () => await collector.RunAsync(deviceCts.Token).ConfigureAwait(false),
                deviceCts.Token);

            var state = new DeviceRunState(runTask, deviceCts, driver, config);
            _devices[config.Id] = state;

            _log.Info("设备 {DeviceName} (ID:{DeviceId}) 采集已启动，周期 {CycleMs}ms",
                config.Name, config.Id, config.CycleTimeMs);
        }

        /// <summary>
        /// 停止单台设备的采集任务并释放驱动资源。
        /// </summary>
        /// <param name="deviceId">设备唯一标识</param>
        public async Task StopDeviceAsync(string deviceId)
        {
            if (!_devices.TryRemove(deviceId, out DeviceRunState? state))
            {
                _log.Warn("设备 {DeviceId} 未找到或已停止", deviceId);
                return;
            }

            // 发送取消信号
            state.Cts.Cancel();

            try
            {
                await state.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消是优雅退出的正常路径
            }
            catch (Exception ex)
            {
                _log.Error(ex, "设备 {DeviceId} 采集任务退出时发生异常", deviceId);
            }

            // 释放驱动和取消令牌
            await state.Driver.DisposeAsync().ConfigureAwait(false);
            state.Cts.Dispose();

            _log.Info("设备 {DeviceId} 已停止并释放资源", deviceId);
        }

        /// <summary>
        /// 热重载设备配置 — 先停止旧任务，再用新配置启动。
        /// 整个过程无需重启应用。
        /// </summary>
        /// <param name="config">更新后的设备配置（DeviceId 不变）</param>
        public async Task ReloadDeviceAsync(DeviceConfig config)
        {
            _log.Info("设备 {DeviceName} (ID:{DeviceId}) 热重载中...", config.Name, config.Id);

            await StopDeviceAsync(config.Id).ConfigureAwait(false);
            await StartDeviceAsync(config).ConfigureAwait(false);

            _log.Info("设备 {DeviceName} 热重载完成", config.Name);
        }

        /// <summary>
        /// 获取指定设备的协议驱动，用于上位机写入操作。
        /// </summary>
        /// <param name="deviceId">设备唯一标识</param>
        /// <returns>协议驱动实例，如果设备未启动则返回 null</returns>
        public IProtocolDriver? GetDriver(string deviceId)
        {
            return _devices.TryGetValue(deviceId, out DeviceRunState? state) ? state.Driver : null;
        }

        /// <summary>
        /// 获取设备当前连接状态。设备未启动或驱动已断开时均返回 false。
        /// UI 只读取运行时状态，不直接依赖具体协议驱动实现。
        /// </summary>
        public bool IsDeviceConnected(string deviceId) =>
            _devices.TryGetValue(deviceId, out DeviceRunState? state) && state.Driver.IsConnected;

        /// <summary>
        /// 获取所有已启动设备的配置列表。
        /// </summary>
        public IReadOnlyList<DeviceConfig> GetDevices() =>
            _devices.Values.Select(s => s.Config).ToList();

        /// <summary>
        /// 单台设备的运行时状态。
        /// </summary>
        private sealed record DeviceRunState(
            Task Task,
            CancellationTokenSource Cts,
            IProtocolDriver Driver,
            DeviceConfig Config);
    }
}



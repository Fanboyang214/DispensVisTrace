using Core.Acquisition;
using Core.Bridge;
using Core.Logging;
using System;
using System.Collections.Generic;

namespace Acquisition.Camera;

/// <summary>
/// 采集侧的"图像就绪"发布者：相机把图落盘后，把这一帧（<b>路径与元数据</b>）发到总线上。
/// </summary>
/// <remarks>
/// <para>
/// 它只订阅采集服务的事件、只往 <see cref="IImageReadyPublisher"/> 里发，
/// <b>不认识视觉引擎，也不做任何图像处理</b>——本模块的职责到"图在共享目录里、通知已发出"为止。
/// 谁订阅、拿图去干什么（定位 / 检测 / 存证 / 上传），是订阅方的事。
/// </para>
/// <para>
/// 载荷就是 <see cref="CameraFrameEventArgs"/> 本身，不另造通知类型：
/// 那份类型已经是"帧已落盘、只带路径与元数据"的语义，复制一份必然漂移。
/// </para>
/// <para>
/// 发布是非阻塞的：总线满了只记一条警告，绝不把相机回调线程堵住。
/// </para>
/// </remarks>
public sealed class ImageReadyPublisher : IDisposable
{
    private readonly IImageReadyPublisher _bus;
    private readonly ILogService _log;
    private readonly List<ICameraAcquisitionService> _cameras = new();

    private bool _disposed;

    /// <param name="cameras">相机运行时（订阅其中每台相机的成帧事件）</param>
    /// <param name="bus">图像就绪总线（进程内实现 / MQTT 实现都可）</param>
    /// <param name="log">日志服务</param>
    public ImageReadyPublisher(CameraRuntime cameras, IImageReadyPublisher bus, ILogService log)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<ImageReadyPublisher>();

        foreach (ICameraAcquisitionService camera in cameras.Cameras)
        {
            camera.FrameCaptured += OnFrameCaptured;
            _cameras.Add(camera);
        }
    }

    /// <summary>已接入发布的相机名（诊断 / 自检用）。</summary>
    public IReadOnlyList<string> Cameras
    {
        get
        {
            var names = new List<string>(_cameras.Count);
            foreach (ICameraAcquisitionService camera in _cameras)
            {
                names.Add(camera.CameraName);
            }

            return names;
        }
    }

    private void OnFrameCaptured(object? sender, CameraFrameEventArgs frame)
    {
        if (_disposed)
        {
            return;
        }

        if (_bus.Publish(frame))
        {
            return;
        }

        // 发布失败必须留痕：静默丢弃在现场表现成"某几颗产品没检测"，最难查
        _log.Warn("{Camera} 图像就绪通知发布失败（总线已满，累计丢弃 {Dropped} 帧）：订阅侧处理跟不上触发，第 {Sequence} 帧 {Path}",
            frame.CameraName, _bus.DroppedCount, frame.SequenceNumber, frame.ImagePath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (ICameraAcquisitionService camera in _cameras)
        {
            camera.FrameCaptured -= OnFrameCaptured;
        }

        _cameras.Clear();
    }
}

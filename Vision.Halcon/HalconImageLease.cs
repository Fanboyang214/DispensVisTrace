using Core.Models;
using HalconDotNet;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Vision.Halcon
{
    /// <summary>
    /// 给 HALCON 引擎取一张 <see cref="HImage"/>：<b>优先用内存里的像素，没有就按路径读文件</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要两种来源：</b>采集侧对外的成帧事件（<c>CameraFrameEventArgs</c>）只带<b>路径</b>
    /// ——像素不进总线，因为那条总线要能换成 MQTT / 资源树（跨进程时 5MB/帧的像素根本传不动），
    /// 也因为共享目录里的文件才是追溯的事实来源。所以视觉侧拿到的 <see cref="InspectionImage"/>
    /// 平时只有 <see cref="InspectionImage.SourcePath"/>；而驱动直采、自检或将来"内存直通"的场景
    /// 会给 <see cref="InspectionImage.RawData"/>。
    /// </para>
    /// <para>
    /// <b>为什么要 leasing 而不是返回 HImage：</b>用内存像素时必须把 <c>byte[]</c> 钉住（pin）
    /// 才能把指针交给 HALCON；如果只返回 HImage，句柄就会在方法返回时被释放，HALCON 读到的是一块
    /// 已经能移动的内存——表现为偶发花屏/崩溃。所以句柄与 HImage 的生命周期绑在一起，
    /// 由调用方 <c>using</c> 控制。
    /// </para>
    /// <para>
    /// <b>按路径读的是 HALCON 自己</b>（<c>ReadImage</c>，按扩展名解码 BMP/PNG/TIFF…），
    /// 不在 .NET 里解一遍再拷一份：少一次解码、少一份内存，而且检测的就是归档下来的那张图。
    /// </para>
    /// </remarks>
    internal sealed class HalconImageLease : IDisposable
    {
        private GCHandle _handle;
        private bool _disposed;

        private HalconImageLease(HImage image, GCHandle handle)
        {
            Image = image;
            _handle = handle;
        }

        /// <summary>已经打开的 HALCON 图像；<b>不要自己 Dispose</b>，由本对象负责。</summary>
        public HImage Image { get; }

        /// <summary>
        /// 打开一张图：有像素用像素（钉住内存），否则按 <see cref="InspectionImage.SourcePath"/> 读文件。
        /// </summary>
        /// <exception cref="InvalidOperationException">既没有像素也没有可读的文件</exception>
        public static HalconImageLease Open(InspectionImage image)
        {
            ArgumentNullException.ThrowIfNull(image);

            if (image.RawData is { Length: > 0 })
            {
                GCHandle handle = GCHandle.Alloc(image.RawData, GCHandleType.Pinned);
                try
                {
                    var hImage = new HImage("byte", image.ImageWidth, image.ImageHeight, handle.AddrOfPinnedObject());
                    return new HalconImageLease(hImage, handle);
                }
                catch
                {
                    // 构造失败时不能把钉住的内存漏掉
                    handle.Free();
                    throw;
                }
            }

            if (!string.IsNullOrWhiteSpace(image.SourcePath) && File.Exists(image.SourcePath))
            {
                // 与模板加载用的是同一个调用（HALCON 按扩展名解码），不额外引入解码库
                HOperatorSet.ReadImage(out HObject fromFile, image.SourcePath);
                return new HalconImageLease(new HImage(fromFile), default);
            }

            throw new InvalidOperationException(
                $"图像既没有像素数据（RawData 为空），也没有可读的文件：{image.SourcePath ?? "(未给出路径)"}" +
                "（共享目录不通 / 文件被清理 / 权限不足？）");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Image?.Dispose();

            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
    }
}

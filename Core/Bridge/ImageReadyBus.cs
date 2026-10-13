using Core.Acquisition;
using Core.Logging;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Core.Bridge
{
    /// <summary>
    /// 发布侧视图：把"这一帧已经落到共享目录"发出去，只管发，不知道谁在听。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>载荷为什么直接用 <see cref="CameraFrameEventArgs"/>，而不是另定义一个通知类型：</b>
    /// 那个类型的语义本来就是"帧已落盘、只带路径与元数据，图像本体去共享目录取"
    /// （见 <c>ICameraAcquisitionService.FrameCaptured</c>），与"图像就绪"完全同一件事。
    /// 再定义一份必然与它漂移——少一个字段、加一个字段就要改两处，
    /// 而两边不一致的表现是"视觉拿到的元数据跟采集发出去的不一样"这种极难查的故障。
    /// 一份定义，总线只负责把它分发到别的模块 / 别的进程。
    /// </para>
    /// <para>
    /// 发布方（Acquisition）与订阅方（Vision）都只依赖本文件，<b>互不认识</b>：
    /// 换实现（进程内总线 / MQTT / 资源树数据点）两边都不用改。
    /// </para>
    /// </remarks>
    public interface IImageReadyPublisher
    {
        /// <summary>
        /// 发布一帧。<b>非阻塞</b>：队列满时返回 <c>false</c>（调用方负责留痕），
        /// 绝不阻塞相机回调线程——丢一条通知远好过把采集堵死。
        /// </summary>
        bool Publish(CameraFrameEventArgs frame);

        /// <summary>累计丢弃帧数（诊断用；非 0 说明订阅侧跟不上触发）。</summary>
        long DroppedCount { get; }
    }

    /// <summary>
    /// 订阅侧视图：只管收，不知道谁在发。
    /// </summary>
    public interface IImageReadySubscriber : IDisposable
    {
        /// <summary>
        /// 有一帧图像已就绪。<b>在总线的派发线程上同步调用</b>（不是在相机 SDK 线程上），
        /// 因此订阅者可以直接做几十毫秒级的活儿；处理失败必须自己吞掉，不要抛给总线。
        /// </summary>
        event EventHandler<CameraFrameEventArgs>? ImageReady;
    }

    /// <summary>
    /// 进程内实现：发布即入队，另有<b>单个泵线程</b>顺序派发事件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要入队而不是直接触发事件：</b>发布发生在相机 SDK 的回调线程上，
    /// 直接同步触发订阅者会把 SDK 堵死（丢帧、缓冲耗尽）。入队后由泵线程派发，
    /// 采集侧只付出一次入队成本。
    /// </para>
    /// <para>
    /// <b>为什么单消费者：</b>事件顺序 = 发布顺序，于是"产品 N 的图像先于 N+1 被处理"，
    /// 这正是点胶要的因果序；同时下游引擎（HALCON 一类不保证并发安全）也天然被串行化。
    /// </para>
    /// <para>
    /// 跨进程的将来：MQTT / 资源树数据点实现同样这两个接口即可替换本类
    /// （发布 = 发主题，订阅 = 收到主题后派发），两侧代码不用动。
    /// </para>
    /// </remarks>
    public sealed class InProcessImageReadyBus : IImageReadyPublisher, IImageReadySubscriber
    {
        /// <summary>默认队列深度：点胶节拍是一帧/几秒，32 条足够吸收一次卡顿。</summary>
        public const int DefaultCapacity = 32;

        /// <summary>关闭时给泵线程的收尾时间。</summary>
        private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

        private readonly Channel<CameraFrameEventArgs> _channel;
        private readonly ILogService? _log;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;

        private long _dropped;
        private bool _disposed;

        /// <param name="capacity">队列深度</param>
        /// <param name="log">日志服务；订阅者抛异常时留痕（没有也能跑）</param>
        public InProcessImageReadyBus(int capacity = DefaultCapacity, ILogService? log = null)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "队列深度必须为正");
            }

            Capacity = capacity;
            _log = log?.ForContext<InProcessImageReadyBus>();

            _channel = Channel.CreateBounded<CameraFrameEventArgs>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,    // 只有泵线程读 → 事件顺序 = 发布顺序
                SingleWriter = false,   // 多台相机可能同时发
                FullMode = BoundedChannelFullMode.Wait
            });

            _pump = Task.Run(PumpAsync);
        }

        /// <summary>队列深度。</summary>
        public int Capacity { get; }

        /// <inheritdoc />
        public long DroppedCount => Interlocked.Read(ref _dropped);

        /// <inheritdoc />
        public event EventHandler<CameraFrameEventArgs>? ImageReady;

        /// <inheritdoc />
        public bool Publish(CameraFrameEventArgs frame)
        {
            ArgumentNullException.ThrowIfNull(frame);

            if (_disposed)
            {
                return false;
            }

            if (_channel.Writer.TryWrite(frame))
            {
                return true;
            }

            Interlocked.Increment(ref _dropped);
            return false;
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (CameraFrameEventArgs frame in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    Raise(frame);
                }
            }
            catch (OperationCanceledException)
            {
                // 关闭时的正常路径
            }
        }

        private void Raise(CameraFrameEventArgs frame)
        {
            EventHandler<CameraFrameEventArgs>? handlers = ImageReady;
            if (handlers is null)
            {
                return;
            }

            // 逐个订阅者调，且各自吞异常：一个订阅者坏了不能把其它订阅者和泵一起带走
            foreach (EventHandler<CameraFrameEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, frame);
                }
                catch (Exception ex)
                {
                    _log?.Error(ex, "{Camera} 的图像 {Path} 有一个订阅者处理失败，已跳过该订阅者",
                        frame.CameraName, frame.ImagePath);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // 先让泵把已入队的帧派发完，再限时收尾
            _channel.Writer.TryComplete();
            _cts.CancelAfter(ShutdownGrace);

            try
            {
                _pump.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // 收尾超时，正常退出
            }

            _cts.Dispose();
        }
    }
}

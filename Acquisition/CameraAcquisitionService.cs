using Core.Acquisition;
using Core.Logging;

namespace Acquisition;

/// <summary>
/// 单台相机的采集服务。
/// <para>
/// 硬触发走相机 IO：产品到位信号直接进相机的触发输入，相机自己出图，本服务只负责收图、落盘、回报。
/// 软触发由上位机经 MQTT 送来，最终落到 <see cref="SoftwareTriggerAsync"/>。
/// </para>
/// <para>
/// 一个实例对应一台相机，<see cref="CameraName"/> 取自驱动，不依赖配置是否已加载。
/// </para>
/// </summary>
public sealed class CameraAcquisitionService : ICameraAcquisitionService
{
    private readonly ICameraDriver _driver;
    private readonly ILogService _log;
    private readonly ProductCodeGenerator _productCodes;

    /// <summary>串行化 Start/Stop/触发，避免上位机软触发和停机流程撞在一起。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CameraConfig? _config;
    private ImageStorageOptions? _storage;
    private CameraState _state = CameraState.Disconnected;
    private long _sequence;

    /// <summary>当前在制品条码。取帧时快照，换产品期间拍到的帧仍带拍摄那一刻的条码。</summary>
    private volatile string? _productId;

    /// <param name="productCodes">
    /// 产品条码发生器。<b>必须与其它相机服务共用同一个实例</b>，否则同一件产品在两台相机上会拿到不同条码。
    /// </param>
    public CameraAcquisitionService(ICameraDriver driver, ILogService log, ProductCodeGenerator productCodes)
    {
        _driver = driver;
        _log = log.ForContext<CameraAcquisitionService>();
        _productCodes = productCodes;

        _driver.FrameCaptured += OnDriverFrameCaptured;
        _driver.ErrorOccurred += OnDriverError;

#if DEBUG
        ImageFileWriter.VerifyMono8Bmp();
#endif
    }

    public string CameraName => _driver.CameraName;

    public CameraInfo? Info => _driver.Info;

    public CameraState State => _state;

    public string? CurrentProductId => _productId;

    public string BeginProduct()
    {
        var code = _productCodes.Next();
        SetCurrentProduct(code);
        return code;
    }

    public void SetCurrentProduct(string? productId)
    {
        var normalized = string.IsNullOrWhiteSpace(productId) ? null : productId.Trim();
        if (_productId == normalized)
            return;

        _productId = normalized;
        _log.Info("{Camera} 在制品条码更新为 {ProductId}", CameraName, normalized ?? "(空)");
    }

    public event EventHandler<CameraFrameEventArgs>? FrameCaptured;
    public event EventHandler<CameraState>? StateChanged;
    public event EventHandler<CameraErrorEventArgs>? ErrorOccurred;

    public async Task StartAsync(CameraConfig config, ImageStorageOptions storage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(storage);

        await _gate.WaitAsync(ct);
        try
        {
            if (_state is CameraState.Armed)
                throw new InvalidOperationException($"{CameraName} 已处于采集状态，请先 StopAsync");

            // 相机名对不上是配置错误，早失败好过图存到另一个相机的名下
            if (config.CameraName != CameraName)
                throw new ArgumentException($"配置的相机名 '{config.CameraName}' 与本实例的 '{CameraName}' 不一致", nameof(config));

            // 目录先建出来：共享目录不通就现在失败，不要等到第一颗产品才丢图
            Directory.CreateDirectory(storage.RootDirectory);

            SetState(CameraState.Connecting);
            await _driver.OpenAsync(config, ct);

            _config = config;
            _storage = storage;
            Interlocked.Exchange(ref _sequence, 0);

            SetState(CameraState.Connected);

            await _driver.SetTriggerSourceAsync(config.TriggerSource, ct);
            await _driver.StartCaptureAsync(ct);

            SetState(CameraState.Armed);
            _log.Info("{Camera} 采集已启动：触发方式 {Trigger}，曝光 {Exposure}us，增益 {Gain}dB，落盘目录 {Directory}",
                CameraName, config.TriggerSource, config.ExposureTimeUs, config.GainDb, storage.RootDirectory);
        }
        catch (Exception ex)
        {
            Fail($"启动采集失败：{ex.Message}", ex, isFatal: true);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is CameraState.Disconnected)
                return;

            if (_driver.IsConnected)
            {
                // 停采集失败也要把相机关掉，否则相机可能一直挂在触发等待上
                try
                {
                    await _driver.StopCaptureAsync(ct);
                }
                catch (Exception ex)
                {
                    _log.Warn(ex, "{Camera} 停止采集失败，继续断开", CameraName);
                }

                await _driver.CloseAsync(ct);
            }

            SetState(CameraState.Disconnected);
            _log.Info("{Camera} 采集已停止", CameraName);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SoftwareTriggerAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is not CameraState.Armed)
            {
                _log.Warn("{Camera} 未处于预备采集状态（当前 {State}），忽略软触发", CameraName, _state);
                return;
            }

            if (_config?.TriggerSource is TriggerSource.Hardware)
            {
                _log.Warn("{Camera} 当前为硬触发模式，相机不会响应软触发，已忽略", CameraName);
                return;
            }

            await _driver.SoftwareTriggerAsync(ct);
        }
        catch (Exception ex)
        {
            Fail($"软触发失败：{ex.Message}", ex, isFatal: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 驱动线程回调：落盘 + 回报。这里不能抛异常，否则会把相机 SDK 的回调线程带崩。
    /// </summary>
    private void OnDriverFrameCaptured(object? sender, CameraFrameData frame)
    {
        var storage = _storage;
        if (storage is null)
        {
            _log.Warn("{Camera} 收到图像但尚未配置落盘目录，已丢弃（序号 {Sequence}）", frame.CameraName, frame.SequenceNumber);
            return;
        }

        var timestamp = frame.Timestamp == default ? DateTime.Now : frame.Timestamp;
        var sequence = Interlocked.Increment(ref _sequence);

        // 硬触发 = 产品到位 = 换了一件新产品，自动开新号。
        // 软触发（Camera02 多帧检测、调试补拍）沿用当前号，让整件产品的多帧共用一个条码；
        // 还没有号时（第一件、或刚被清空）也开一个，保证任何一帧都不缺条码。
        if (frame.TriggerSource is TriggerSource.Hardware || _productId is null)
            BeginProduct();

        // 快照：落盘可能耗时，期间条码可能已经换成下一件产品
        var productId = _productId;
        var path = storage.BuildImagePath(frame.CameraName, timestamp, sequence);

        try
        {
            ImageFileWriter.WriteMono8Bmp(path, frame.PixelData, frame.Width, frame.Height);
        }
        catch (Exception ex)
        {
            // 单张图写失败只报错不断流：停线比丢一张图贵得多
            Fail($"{frame.CameraName} 图像落盘失败（{path}）：{ex.Message}", ex, isFatal: false);
            return;
        }

        if (productId is null)
        {
            // 图还是要存，但追溯链断了，必须让人看到
            _log.Warn("{Camera} 第 {Sequence} 帧没有在制品条码，追溯信息不完整", frame.CameraName, sequence);
        }

        FrameCaptured?.Invoke(this, new CameraFrameEventArgs(
            frame.CameraName,
            path,
            productId,
            frame.Width,
            frame.Height,
            frame.PixelFormat,
            sequence,
            timestamp,
            frame.TriggerSource));
    }

    private void OnDriverError(object? sender, CameraErrorEventArgs e)
    {
        if (e.IsFatal)
            SetState(CameraState.Error);

        ErrorOccurred?.Invoke(this, e);
    }

    private void Fail(string message, Exception ex, bool isFatal)
    {
        var args = new CameraErrorEventArgs(CameraName, ex.GetType().Name, message, isFatal);
        if (isFatal)
            SetState(CameraState.Error);

        _log.Error(ex, "{Camera} {Message}", CameraName, message);
        ErrorOccurred?.Invoke(this, args);
    }

    private void SetState(CameraState newState)
    {
        if (_state == newState) return;

        _state = newState;
        StateChanged?.Invoke(this, newState);
    }
}

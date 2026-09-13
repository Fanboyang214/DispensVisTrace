namespace Acquisition;

/// <summary>
/// 产品条码发生器：内部自增编号，不依赖扫码枪或上位机下发。
/// <para>
/// <b>必须注册为单例并注入给所有相机服务。</b> Camera01（定位）和 Camera02（检测）看的是同一件
/// 产品，各自持有独立实例的话，同一件产品会在两台相机上拿到两个不同条码，追溯链直接断裂。
/// </para>
/// </summary>
public sealed class ProductCodeGenerator
{
    private long _sequence;

    /// <summary>取下一个产品条码，形如 <c>P000001</c>。</summary>
    public string Next() => $"P{Interlocked.Increment(ref _sequence):D6}";

    // ponytail: 纯内存计数，进程重启后从 P000001 重新开始，跨班次/跨天会重号——
    // 条码重复意味着两张不同产品的图在追溯库里指向同一条记录。当前 Persistence 还没有落地，
    // 等追溯存储确定后，这里改成从库里取当天最大序号续号（或落盘到共享目录）。
}

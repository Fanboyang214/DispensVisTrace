using Core.Acquisition;
using Core.Logging;

namespace Acquisition.Camera;

/// <summary>
/// 按 <see cref="CameraConfig.SdkType"/> 造相机驱动：换品牌只改这里 + 加一个 <see cref="ICameraDriver"/> 实现。
/// </summary>
/// <remarks>
/// <para>
/// 放在采集侧而不是组合根：App 不该知道"海康走 MVS、大恒走哪个 SDK"这类厂商细节。
/// 组合根只决定"有几台相机、什么角色"，驱动选择由配置里的 <c>sdkType</c> 决定。
/// </para>
/// <para>
/// 认不出的类型直接抛：配错 SDK 名时，"没有相机可用"和"少了一台相机"在现场是两种完全不同的故障，
/// 必须当场说清楚。
/// </para>
/// </remarks>
public static class CameraDriverFactory
{
    /// <summary>仿真驱动（无硬件、联调、CI）。</summary>
    public const string SdkSimulation = "Simulation";

    /// <summary>海康 MVS（MvCameraControl.Net）。</summary>
    public const string SdkHikRobot = "HikRobot";

    /// <summary>
    /// 造一台相机的驱动。
    /// </summary>
    /// <param name="config">相机配置（只看 <see cref="CameraConfig.CameraName"/> 与 <see cref="CameraConfig.SdkType"/>）</param>
    /// <param name="log">日志服务</param>
    /// <exception cref="NotSupportedException">sdkType 不认识</exception>
    public static ICameraDriver Create(CameraConfig config, ILogService log)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);

        // 现场的写法不统一：MVS / Hikvision / HikRobot 都指海康；大小写与空格一律忽略
        switch (config.SdkType.Trim().ToLowerInvariant())
        {
            case "simulation":
            case "sim":
                return new SimulationCameraDriver(config.CameraName);

            case "hikrobot":
            case "hikvision":
            case "mvs":
                return new HikvisionMvsCameraDriver(config.CameraName, log);

            default:
                throw new NotSupportedException(
                    $"相机 {config.CameraName} 的 sdkType '{config.SdkType}' 没有对应驱动" +
                    $"（已支持 {SdkSimulation} / {SdkHikRobot}）；" +
                    "接 Basler / 大恒时，先按 ICameraDriver 实现一个驱动再在这里登记");
        }
    }
}

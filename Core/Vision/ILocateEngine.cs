using Core.Models;
using Core.Motion;
using System;
using System.Threading.Tasks;

namespace Core.Vision
{
    public interface ILocateEngine:IDisposable
    {
        /// <summary>
        /// 初始化引擎（读取配置、加载模板）。重复调用必须幂等。
        /// </summary>
        /// <returns>是否初始化成功；失败时禁止调用 执行后续定位方法</returns>
        bool Initialize(LocateConfig config);
        /// <summary>
        /// 定位图像的图像质量检查。该方法在 <see cref="FindPoseOffset"/> 之前调用，确保图像满足定位要求。
        /// </summary>
        /// <param name="image"></param>
        /// <returns></returns>
        ImageQualityResult CheckImageQuality(InspectionImage image);
        /// <summary>
        /// 定位图像中产品相对<b>示教位</b>的位姿偏移（绕 Z 旋转 + XY 平移，机械坐标 mm / 弧度）。
        /// 该方法在 <see cref="CheckImageQuality"/> 之后调用，确保图像满足定位要求。
        /// </summary>
        /// <remarks>
        /// 返回的是<b>刚体变换参数</b>而不是图像里的行列坐标：基轨迹（点胶路径）归 Motion 模块持有，
        /// 视觉侧只负责把"产品现在偏了多少"算出来交给它，自己不再加载 / 变换胶路。
        /// 语义与 <c>Motion.TrajectorySegment.RotateThenTranslate</c> 一致：
        /// <c>p' = RotCenter + R(Δθ)·(p - RotCenter) + (ΔX, ΔY)</c>。
        /// </remarks>
        /// <param name="image">待定位的图像。</param>
        /// <returns>求解出的偏移；未配模板、匹配失败或标定无效时为 <c>null</c>。</returns>
        PoseOffset? FindPoseOffset(InspectionImage image);
        /// <summary>
        /// 最近一次 <see cref="FindPoseOffset"/> 产出的模板匹配分（就是与配置 minScore 相比的那个值）；
        /// 没跑过、匹配失败或中途异常时为 <c>null</c>。
        /// </summary>
        /// <remarks>
        /// 引擎只负责把值说出来，<b>不负责上报</b>：上报（如资源树的 <c>cam/locate-conf</c>）由调用方做。
        /// </remarks>
        double? LastScore { get; }
    }
}

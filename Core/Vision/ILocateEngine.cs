using Core.Models;
using System;
using System.Runtime.CompilerServices;
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
        /// 定位图像的图像质量检查。该方法在 <see cref="FindProductPose"/> 之前调用，确保图像满足定位要求。
        /// </summary>
        /// <param name="image"></param>
        /// <returns></returns>
        ImageQualityResult CheckImageQuality(InspectionImage image);
        /// <summary>
        /// 定位图像中产品的姿态（位置和角度）。该方法在 <see cref="CheckImageQuality"/> 之后调用，确保图像满足定位要求。
        /// </summary>
        /// <param name="image"></param>
        /// <returns></returns>
        ProductPose? FindProductPose(InspectionImage image);
        /// <summary>
        /// 加载产品的胶点路径。该方法在 <see cref="FindProductPose"/> 之后调用，确保产品姿态已定位。
        /// 原路径只加载一次，后续调用只返回缓存的路径数据。
        /// </summary>
        /// <param name="pose"></param>
        /// <returns></returns>
        GluePath LoadCadGluePath(ProductPose pose);
    }
}

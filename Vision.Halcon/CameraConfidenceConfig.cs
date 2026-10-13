using Core.Acquisition;
using Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vision.Halcon
{
    /// <summary>
    /// 从算法配方里取"某个相机角色生效的置信度阈值"，以及在没有产品型号选择时该用哪一份配方。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要有这个映射：</b>资源树的 <c>cam/locate-conf</c> / <c>cam/inspect-conf</c> 在
    /// 链接相机时先发<b>配置阈值</b>（让上位机知道当前生效的门限），出结果后由
    /// <see cref="VisionFrameWorker"/> 用实际置信度覆盖。阈值取自这里，与实际判定用的必须是同一个字段，
    /// 否则"上位机看到的门限"和"引擎真正在比的数"会不一致——那种不一致比不发还坏。
    /// </para>
    /// <para>
    /// 取哪些字段（与引擎 <c>Initialize</c> 读的是同一批）：
    /// 定位 = <c>locate.templateMatching.minScore</c>（<c>FindShapeModel</c> 的 minScore）；
    /// 检测 = <c>inspect.imageQualityGate.scoreThreshold</c>（检测引擎唯一产出的分数所比的门限）。
    /// </para>
    /// </remarks>
    internal static class CameraConfidenceConfig
    {
        /// <summary>取该角色生效的置信度阈值；配方里没有这个键时返回 null（不猜默认值）。</summary>
        public static double? ThresholdOf(AlgorithmConfig? config, CameraRole role)
        {
            if (config is null)
            {
                return null;
            }

            return role switch
            {
                CameraRole.Locate => ReadDouble(config.Locate?.TemplateMatching, "minScore"),
                CameraRole.Inspect => ReadDouble(config.Inspect?.ImageQualityGate, "scoreThreshold"),
                _ => null
            };
        }

        /// <summary>
        /// 选出"当前生效"的配方。
        /// </summary>
        /// <remarks>
        /// 现在没有"当前产品型号"这个概念（点胶程序将来会定），所以规则是：
        /// 唯一启用 → 用它；多个启用 → 按型号名取第一个并在 <paramref name="note"/> 里明说这是暂定值；
        /// 一个都没启用 → 返回 null（宁可不发，也不发一个编的阈值）。
        /// </remarks>
        /// <param name="configs">可用配方（通常是全部启用的）</param>
        /// <param name="note">需要提醒调用方的话；无需提醒时为 null</param>
        public static AlgorithmConfig? Select(IEnumerable<AlgorithmConfig>? configs, out string? note)
        {
            note = null;

            List<AlgorithmConfig> enabled = (configs ?? Enumerable.Empty<AlgorithmConfig>())
                .Where(c => c is not null)
                .OrderBy(c => c.ProductModel, StringComparer.Ordinal)
                .ToList();

            if (enabled.Count == 0)
            {
                note = "没有可用的算法配方（config/algorithm-profiles.json 里 enabled 全为 false，或文件读不到）：置信度初值不发布";
                return null;
            }

            if (enabled.Count > 1)
            {
                note = $"有 {enabled.Count} 个启用的算法配方（{string.Join("、", enabled.Select(c => c.ProductModel))}），" +
                       $"当前没有产品型号选择：置信度初值暂按 {enabled[0].ProductModel} 发布，选定型号后随配置变更刷新";
            }

            return enabled[0];
        }

        /// <summary>从 <c>Dictionary&lt;string, object&gt;</c> 配置袋里读一个 double（JSON 数字反序列化成 JsonElement，交给 Convert 处理）。</summary>
        private static double? ReadDouble(Dictionary<string, object>? bag, string key)
        {
            if (bag is null || !bag.TryGetValue(key, out object? raw) || raw is null)
            {
                return null;
            }

            try
            {
                return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // 值写成了非数字：宁可当作"没有"，也不要发一个 0 出去
                return null;
            }
        }
    }
}

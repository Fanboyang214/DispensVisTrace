using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.ResourceTree
{
    /// <summary>
    /// 强类型规格化资源路径。
    /// 工业运行时服务将其作为设备、标签、菜单项、报警、规则和权限的共享地址。
    /// </summary>
    public readonly record struct ResourcePath
    {
        private static readonly char[] s_separator = ['/', '\\'];

        /// <summary>
        /// 初始化资源路径的新实例。
        /// </summary>
        /// <param name="value">路径字符串。</param>
        public ResourcePath(string value)
        {
            Value = Normalize(value);
            Segments = Array.AsReadOnly(Value.Split('/'));
        }

        /// <summary>
        /// 获取路径的规格化字符串值（使用 / 作为分隔符）。
        /// </summary>
        public string Value { get; }

        /// <summary>
        /// 获取路径的所有分段。
        /// </summary>
        public ReadOnlyCollection<string> Segments { get; }

        /// <summary>
        /// 获取路径深度（分段数量）。
        /// </summary>
        public int Depth => Segments.Count;

        /// <summary>
        /// 获取资源名称（路径的最后一个分段）。
        /// </summary>
        public string Name => Segments[^1];

        /// <summary>
        /// 获取一个值，指示该路径是否为根路径（深度为 1）。
        /// </summary>
        public bool IsRoot => Depth == 1;

        /// <summary>
        /// 获取父级路径。如果是根路径，则返回 null。
        /// </summary>
        public ResourcePath? Parent
        {
            get
            {
                if (IsRoot)
                {
                    return null;
                }

                return new ResourcePath(string.Join('/', Segments.Take(Depth - 1)));
            }
        }

        /// <summary>
        /// 获取该路径的所有祖先路径。
        /// </summary>
        /// <param name="includeSelf">是否包含自身。</param>
        /// <returns>祖先路径列表，按从深到浅排序。</returns>
        public IReadOnlyList<ResourcePath> GetAncestors(bool includeSelf = false)
        {
            var ancestors = new List<ResourcePath>();

            if (includeSelf)
            {
                ancestors.Add(this);
            }

            for (var depth = Depth - 1; depth >= 1; depth--)
            {
                ancestors.Add(new ResourcePath(string.Join('/', Segments.Take(depth))));
            }

            return ancestors;
        }

        /// <summary>
        /// 检查该路径是否为指定父路径的后代。
        /// </summary>
        /// <param name="parent">父路径。</param>
        /// <param name="includeSelf">如果路径相同，是否视为后代。</param>
        /// <returns>如果是后代，则为 true；否则为 false。</returns>
        public bool IsDescendantOf(ResourcePath parent, bool includeSelf = false)
        {
            if (includeSelf && Equals(parent))
            {
                return true;
            }

            if (Depth <= parent.Depth)
            {
                return false;
            }

            return Value.StartsWith(parent.Value + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 返回路径的规格化字符串表示。
        /// </summary>
        public override string ToString() => Value;

        /// <summary>
        /// 解析路径字符串。
        /// </summary>
        /// <param name="value">路径字符串。</param>
        /// <returns>资源路径实例。</returns>
        public static ResourcePath Parse(string value) => new(value);

        /// <summary>
        /// 尝试解析路径字符串。
        /// </summary>
        /// <param name="value">路径字符串。</param>
        /// <param name="path">解析成功的资源路径实例。</param>
        /// <returns>解析成功则为 true。</returns>
        public static bool TryParse(string? value, out ResourcePath path)
        {
            try
            {
                path = new ResourcePath(value ?? string.Empty);
                return true;
            }
            catch
            {
                path = default;
                return false;
            }
        }

        /// <summary>
        /// 规格化路径字符串。
        /// 移除前导和尾随空格、分隔符，并统一使用 / 分隔。
        /// </summary>
        /// <param name="value">原始路径字符串。</param>
        /// <returns>规格化后的路径字符串。</returns>
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("资源路径不能为空。", nameof(value));
            }

            var segments = value
                .Trim()
                .Trim(s_separator)
                .Split(s_separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (segments.Length == 0)
            {
                throw new ArgumentException("资源路径必须至少包含一个分段。", nameof(value));
            }

            if (segments.Any(static segment => string.IsNullOrWhiteSpace(segment)))
            {
                throw new ArgumentException("资源路径包含空分段。", nameof(value));
            }

            if (segments.Any(static segment => segment.Contains('*', StringComparison.Ordinal)))
            {
                throw new ArgumentException("资源路径不能包含通配符。", nameof(value));
            }

            return string.Join('/', segments);
        }
    }

}

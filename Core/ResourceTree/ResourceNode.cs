using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;

namespace Core.ResourceTree
{
    /// <summary>
    /// 工业运行时资源树节点的领域模型。
    /// 节点在运行时是不可变的；配置更改会生成新的快照，因此读取者永远不会观察到更新一半的树。
    /// </summary>
    public sealed record ResourceNode
    {
        /// <summary>
        /// 获取节点的唯一标识符。
        /// </summary>
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 获取或设置父节点的标识符。
        /// </summary>
        public string? ParentId { get; init; }

        /// <summary>
        /// 获取资源路径。
        /// </summary>
        public ResourcePath Path { get; init; }

        /// <summary>
        /// 获取节点内部名称。
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// 获取节点的显示名称。
        /// </summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>
        /// 获取资源类型。
        /// </summary>
        public ResourceType ResourceType { get; init; } = ResourceType.Unknown;

        /// <summary>
        /// 获取排序顺序。
        /// </summary>
        public int SortOrder { get; init; }

        /// <summary>
        /// 获取一个值，指示该节点是否已启用。
        /// </summary>
        public bool IsEnabled { get; init; } = true;

        /// <summary>
        /// 获取关联的元数据（JSON 格式）。
        /// </summary>
        public string? MetadataJson { get; init; }

        /// <summary>
        /// 获取版本号，用于并发控制和热重载。
        /// </summary>
        public long Version { get; init; } = 1;

        /// <summary>
        /// 获取创建时间（UTC）。
        /// </summary>
        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 获取最后更新时间（UTC）。
        /// </summary>
        public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 获取一个值，指示该节点是否为根节点。
        /// </summary>
        public bool IsRoot => Path.IsRoot;

        /// <summary>
        /// 获取父级路径。
        /// </summary>
        public ResourcePath? ParentPath => Path.Parent;
    }

}

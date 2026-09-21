using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.ResourceTree
{
    /// <summary>
    /// 资源树的不可变内存视图。
    /// 所有热重载操作都会构建一个新实例，然后原子地发布它。
    /// </summary>
    public sealed class ResourceTreeSnapshot
    {
        private static readonly StringComparer s_pathComparer = StringComparer.OrdinalIgnoreCase;

        private readonly IReadOnlyDictionary<string, ResourceNode> _nodesByPath;
        private readonly IReadOnlyDictionary<string, ResourceNode> _nodesById;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<ResourceNode>> _childrenByParentPath;
        private readonly IReadOnlyList<ResourceNode> _roots;

        private ResourceTreeSnapshot(
            IReadOnlyDictionary<string, ResourceNode> nodesByPath,
            IReadOnlyDictionary<string, ResourceNode> nodesById,
            IReadOnlyDictionary<string, IReadOnlyList<ResourceNode>> childrenByParentPath,
            IReadOnlyList<ResourceNode> roots,
            long version)
        {
            _nodesByPath = nodesByPath;
            _nodesById = nodesById;
            _childrenByParentPath = childrenByParentPath;
            _roots = roots;
            Version = version;
        }

        /// <summary>
        /// 获取空快照。
        /// </summary>
        public static ResourceTreeSnapshot Empty { get; } = new(
            new Dictionary<string, ResourceNode>(s_pathComparer),
            new Dictionary<string, ResourceNode>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<ResourceNode>>(s_pathComparer),
            [],
            0);

        /// <summary>
        /// 获取快照版本。
        /// </summary>
        public long Version { get; }

        /// <summary>
        /// 获取快照中的节点总数。
        /// </summary>
        public int Count => _nodesByPath.Count;

        /// <summary>
        /// 获取所有根节点。
        /// </summary>
        public IReadOnlyList<ResourceNode> Roots => _roots;

        /// <summary>
        /// 获取快照中的所有节点。
        /// </summary>
        public IReadOnlyCollection<ResourceNode> Nodes => _nodesByPath.Values.ToArray();

        /// <summary>
        /// 从节点集合构建新的资源树快照。
        /// </summary>
        /// <param name="nodes">资源节点集合。</param>
        /// <returns>构建的资源树快照。</returns>
        /// <exception cref="InvalidOperationException">当存在重复路径、重复 ID 或孤儿节点时抛出。</exception>
        public static ResourceTreeSnapshot Build(IEnumerable<ResourceNode> nodes)
        {
            ArgumentNullException.ThrowIfNull(nodes);

            var orderedNodes = nodes
                .OrderBy(static node => node.Path.Depth)
                .ThenBy(static node => node.SortOrder)
                .ThenBy(static node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var byPath = new Dictionary<string, ResourceNode>(s_pathComparer);
            var byId = new Dictionary<string, ResourceNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in orderedNodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id))
                {
                    throw new InvalidOperationException($"资源节点 '{node.Path}' 的 ID 为空。");
                }

                if (!byPath.TryAdd(node.Path.Value, node))
                {
                    throw new InvalidOperationException($"重复的资源路径 '{node.Path}'。");
                }

                if (!byId.TryAdd(node.Id, node))
                {
                    throw new InvalidOperationException($"重复的资源节点 ID '{node.Id}'。");
                }
            }

            foreach (var node in orderedNodes)
            {
                var parentPath = node.ParentPath;
                if (parentPath is not null && !byPath.ContainsKey(parentPath.Value.Value))
                {
                    throw new InvalidOperationException(
                        $"资源路径 '{node.Path}' 引用了缺失的父路径 '{parentPath.Value.Value}'。");
                }
            }

            var children = orderedNodes
                .Where(static node => node.ParentPath is not null)
                .GroupBy(static node => node.ParentPath!.Value.Value, s_pathComparer)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlyList<ResourceNode>)group
                        .OrderBy(static node => node.SortOrder)
                        .ThenBy(static node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    s_pathComparer);

            var roots = orderedNodes
                .Where(static node => node.IsRoot)
                .OrderBy(static node => node.SortOrder)
                .ThenBy(static node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var version = orderedNodes.Count == 0 ? 0 : orderedNodes.Max(static node => node.Version);
            return new ResourceTreeSnapshot(byPath, byId, children, roots, version);
        }

        /// <summary>
        /// 尝试根据路径获取节点。
        /// </summary>
        /// <param name="path">资源路径。</param>
        /// <param name="node">获取到的节点。</param>
        /// <returns>如果找到则为 true。</returns>
        public bool TryGet(ResourcePath path, out ResourceNode node) =>
            _nodesByPath.TryGetValue(path.Value, out node!);

        /// <summary>
        /// 根据路径查找节点。
        /// </summary>
        /// <param name="path">资源路径。</param>
        /// <returns>找到的节点，否则为 null。</returns>
        public ResourceNode? Find(ResourcePath path) =>
            _nodesByPath.TryGetValue(path.Value, out var node) ? node : null;

        /// <summary>
        /// 根据 ID 查找节点。
        /// </summary>
        /// <param name="id">节点唯一标识。</param>
        /// <returns>找到的节点，否则为 null。</returns>
        public ResourceNode? FindById(string id) =>
            _nodesById.TryGetValue(id, out var node) ? node : null;

        /// <summary>
        /// 获取指定父路径的所有直接子节点。
        /// </summary>
        /// <param name="parentPath">父资源路径。</param>
        /// <returns>子节点列表。</returns>
        public IReadOnlyList<ResourceNode> GetChildren(ResourcePath parentPath)
        {
            return _childrenByParentPath.TryGetValue(parentPath.Value, out var children)
                ? children
                : [];
        }

        /// <summary>
        /// 获取指定路径的所有祖先节点。
        /// </summary>
        /// <param name="path">资源路径。</param>
        /// <param name="includeSelf">是否包含自身。</param>
        /// <returns>祖先节点列表。</returns>
        public IReadOnlyList<ResourceNode> GetAncestors(ResourcePath path, bool includeSelf = false)
        {
            return path.GetAncestors(includeSelf)
                .Select(Find)
                .Where(static node => node is not null)
                .Cast<ResourceNode>()
                .ToArray();
        }

        /// <summary>
        /// 获取指定父路径的所有后代节点（递归）。
        /// </summary>
        /// <param name="parentPath">父资源路径。</param>
        /// <param name="includeSelf">是否包含自身。</param>
        /// <returns>后代节点列表。</returns>
        public IReadOnlyList<ResourceNode> GetDescendants(ResourcePath parentPath, bool includeSelf = false)
        {
            var result = new List<ResourceNode>();

            if (includeSelf && TryGet(parentPath, out var parent))
            {
                result.Add(parent);
            }

            AddChildren(parentPath, result);
            return result;
        }

        private void AddChildren(ResourcePath parentPath, List<ResourceNode> result)
        {
            foreach (var child in GetChildren(parentPath))
            {
                result.Add(child);
                AddChildren(child.Path, result);
            }
        }
    }

}

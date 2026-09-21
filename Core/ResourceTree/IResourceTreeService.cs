using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.ResourceTree
{

    /// <summary>
    /// 面向运行时的资源树服务。
    /// 使用者从不可变的快照中读取数据；重载操作是显式的且异步的。
    /// </summary>
    public interface IResourceTreeService
    {
        /// <summary>
        /// 获取当前的资源树不可变快照。
        /// </summary>
        ResourceTreeSnapshot Current { get; }

        /// <summary>
        /// 从持久化存储中异步重载资源树并发布新快照。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>新发布的资源树快照。</returns>
        Task<ResourceTreeSnapshot> ReloadAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 在当前快照中查找指定路径的节点。
        /// </summary>
        /// <param name="path">资源路径。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>找到的节点，否则为 null。</returns>
        ValueTask<ResourceNode?> FindAsync(ResourcePath path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取指定路径在当前快照中的直接子节点。
        /// </summary>
        /// <param name="parentPath">父路径。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>直接子节点列表。</returns>
        ValueTask<IReadOnlyList<ResourceNode>> GetChildrenAsync(ResourcePath parentPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取指定路径在当前快照中的所有后代节点（递归）。
        /// </summary>
        /// <param name="parentPath">父路径。</param>
        /// <param name="includeSelf">是否包含自身。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>后代节点列表。</returns>
        ValueTask<IReadOnlyList<ResourceNode>> GetDescendantsAsync(ResourcePath parentPath, bool includeSelf = false, CancellationToken cancellationToken = default);
    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.ResourceTree
{
    /// <summary>
    /// 资源树配置的持久化边界。
    /// 不同的实现可以使用 SQLite、PostgreSQL、Redis 支持的配置或远程配置服务，而无需更改运行时服务。
    /// </summary>
    public interface IResourceTreeRepository
    {
        /// <summary>
        /// 异步加载数据库中定义的所有资源节点。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>资源节点列表。</returns>
        Task<IReadOnlyList<ResourceNode>> LoadAllAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 根据路径异步查找资源节点。
        /// </summary>
        /// <param name="path">资源路径。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>找到的节点，否则为 null。</returns>
        Task<ResourceNode?> FindByPathAsync(ResourcePath path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 插入或更新资源节点。
        /// </summary>
        /// <param name="node">要保存的节点。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        Task UpsertAsync(ResourceNode node, CancellationToken cancellationToken = default);

        /// <summary>
        /// 根据路径异步删除资源节点及其所有后代。
        /// </summary>
        /// <param name="path">要删除的资源路径。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        Task DeleteAsync(ResourcePath path, CancellationToken cancellationToken = default);
    }


}

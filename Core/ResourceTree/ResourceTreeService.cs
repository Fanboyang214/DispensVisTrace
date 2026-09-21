using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.ResourceTree
{
    /// <summary>
    /// 默认的运行时资源树服务实现，由存储库和原子发布的不可变快照支持。
    /// 这是热重载模块的典型模式：构建、验证、原子替换。
    /// </summary>
    public sealed class ResourceTreeService : IResourceTreeService
    {
        private readonly IResourceTreeRepository _repository;
        private readonly SemaphoreSlim _reloadLock = new(1, 1);
        private volatile ResourceTreeSnapshot _current = ResourceTreeSnapshot.Empty;

        /// <summary>
        /// 初始化资源树服务的新实例。
        /// </summary>
        /// <param name="repository">资源树存储库。</param>
        public ResourceTreeService(IResourceTreeRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>
        /// 获取当前的资源树不可变快照。
        /// </summary>
        public ResourceTreeSnapshot Current => _current;

        /// <summary>
        /// 从存储库异步加载所有节点，构建并发布新快照。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>新发布的快照。</returns>
        public async Task<ResourceTreeSnapshot> ReloadAsync(CancellationToken cancellationToken = default)
        {
            await _reloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var nodes = await _repository.LoadAllAsync(cancellationToken).ConfigureAwait(false);
                var next = ResourceTreeSnapshot.Build(nodes);

                // 原子引用赋值，确保读取者始终观察到完整的旧树或完整的新树。
                _current = next;
                return next;
            }
            finally
            {
                _reloadLock.Release();
            }
        }

        /// <summary>
        /// 在当前快照中查找节点。
        /// </summary>
        public ValueTask<ResourceNode?> FindAsync(ResourcePath path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Current.Find(path));
        }

        /// <summary>
        /// 获取直接子节点。
        /// </summary>
        public ValueTask<IReadOnlyList<ResourceNode>> GetChildrenAsync(
            ResourcePath parentPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Current.GetChildren(parentPath));
        }

        /// <summary>
        /// 获取后代节点。
        /// </summary>
        public ValueTask<IReadOnlyList<ResourceNode>> GetDescendantsAsync(
            ResourcePath parentPath,
            bool includeSelf = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Current.GetDescendants(parentPath, includeSelf));
        }
    }

}

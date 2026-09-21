using Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Core.ResourceTree
{

    /// <summary>
    /// 资源树同步器 — 将 <see cref="AcquisitionHost"/> 中的实时设备树（DeviceConfig / TagPoint）
    /// 作为单一事实源，写入 <c>resource_nodes</c> 持久化表，并原子刷新 <see cref="IResourceTreeService"/> 快照。
    /// <para>
    /// 报警规则页的设备/数据点下拉框只读取该快照，因此本同步器解决了「实际设备与下拉框不一致」的问题：
    /// 凡是 AcquisitionHost 中已启动的设备，都会被镜像到资源树；快照中属于 <c>Devices/</c> 前缀、
    /// 但已不在实时设备树中的节点会被清理，保持资源树与现场严格一致。
    /// </para>
    /// </summary>
    public sealed class ResourceTreeSynchronizer
    {
        private const string RootPath = "Devices";
        private const string RootId = "$devices-root$";

        private readonly IResourceTreeRepository _repository;
        private readonly IResourceTreeService _resourceTreeService;

        public ResourceTreeSynchronizer(
            IResourceTreeRepository repository,
            IResourceTreeService resourceTreeService)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _resourceTreeService = resourceTreeService ?? throw new ArgumentNullException(nameof(resourceTreeService));
        }

        /// <summary>
        /// 将给定设备集合同步进资源树：写入实时节点、清理过期节点、刷新快照。
        /// </summary>
        /// <param name="devices">来自 AcquisitionHost 的实时设备配置。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public async Task SyncFromDevicesAsync(
            IEnumerable<DeviceConfig> devices,
            CancellationToken cancellationToken = default)
        {
            var desired = BuildNodes(devices).ToList();

            // 1. 写入（新增或更新）实时设备树节点
            foreach (var node in desired)
            {
                await _repository.UpsertAsync(node, cancellationToken).ConfigureAwait(false);
            }

            // 2. 清理「本应由此同步器管理」但已不在实时设备树中的过期节点
            var validPaths = new HashSet<string>(
                desired.Select(static n => n.Path.Value),
                StringComparer.OrdinalIgnoreCase);

            var snapshot = _resourceTreeService.Current;
            foreach (var existing in snapshot.Nodes)
            {
                if (existing.Path.Value.StartsWith(RootPath + "/", StringComparison.OrdinalIgnoreCase) &&
                    !validPaths.Contains(existing.Path.Value))
                {
                    await _repository.DeleteAsync(existing.Path, cancellationToken).ConfigureAwait(false);
                }
            }

            // 3. 原子刷新内存快照，报警页下拉框随即反映真实设备
            await _resourceTreeService.ReloadAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 由设备集合构建分层资源节点：
        /// <c>Devices/{设备名}/{数据点名}</c>，其中数据点节点携带 <c>tagId</c> 元数据，
        /// 供报警规则页的向导式流程（ResolveTagId）与报警引擎（按 TagId 匹配）使用。
        /// </summary>
        private static IEnumerable<ResourceNode> BuildNodes(IEnumerable<DeviceConfig> devices)
        {
            yield return new ResourceNode
            {
                Id = RootId,
                Path = new ResourcePath(RootPath),
                Name = RootPath,
                DisplayName = "设备树",
                ResourceType = ResourceType.Factory,
                SortOrder = 0,
                IsEnabled = true
            };

            var usedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            usedPaths[RootPath] = 0;

            int deviceOrder = 1;
            foreach (var device in devices)
            {
                var deviceBase = Sanitize(device.Name);
                var devicePath = EnsureUnique($"{RootPath}/{deviceBase}", usedPaths);

                yield return new ResourceNode
                {
                    Id = device.Id,
                    ParentId = RootId,
                    Path = new ResourcePath(devicePath),
                    Name = device.Name,
                    DisplayName = device.Name,
                    ResourceType = ResourceType.Device,
                    SortOrder = deviceOrder++,
                    IsEnabled = true,
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        deviceId = device.Id,
                        driverType = device.DriverType,
                        ipAddress = device.IpAddress,
                        port = device.Port
                    })
                };

                int tagOrder = 1;
                foreach (var tag in device.Tags)
                {
                    var tagPath = EnsureUnique($"{devicePath}/{Sanitize(tag.Name)}", usedPaths);

                    yield return new ResourceNode
                    {
                        Id = tag.Id,
                        ParentId = device.Id,
                        Path = new ResourcePath(tagPath),
                        Name = tag.Name,
                        DisplayName = tag.Name,
                        ResourceType = ResourceType.Tag,
                        SortOrder = tagOrder++,
                        IsEnabled = true,
                        MetadataJson = JsonSerializer.Serialize(new
                        {
                            tagId = tag.Id,
                            deviceId = device.Id,
                            address = tag.Address,
                            dataType = tag.DataType.ToString()
                        })
                    };
                }
            }
        }

        /// <summary>
        /// 将名称规范化为资源路径段：去除首尾空白，并将路径分隔符替换为连字符。
        /// </summary>
        private static string Sanitize(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? "unnamed"
                : value.Trim().Replace('/', '-').Replace('\\', '-');

        /// <summary>
        /// 保证路径在已用集合中唯一，冲突时追加 -2、-3 … 后缀。
        /// </summary>
        private static string EnsureUnique(string candidate, ConcurrentDictionary<string, byte> usedPaths)
        {
            if (usedPaths.TryAdd(candidate, 0))
                return candidate;

            for (int i = 2; i < int.MaxValue; i++)
            {
                var alt = $"{candidate}-{i}";
                if (usedPaths.TryAdd(alt, 0))
                    return alt;
            }

            return candidate;
        }
    }
}

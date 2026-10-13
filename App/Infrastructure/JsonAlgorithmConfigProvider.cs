using Core.Models;
using Core.Vision;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace App.Infrastructure
{
    /// <summary>
    /// 从 <c>config/algorithm-profiles.json</c> 读算法配方（按产品型号）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 文件结构是<b>根对象里的 profiles 数组</b>：
    /// <code>
    /// { "$schema": "...", "version": "1.0.0", "profiles": [ { "productModel": "MODEL-A", "enabled": true, "locate": {...}, "inspect": {...} } ] }
    /// </code>
    /// 型号名用连字符（<c>MODEL-A</c>），而 <see cref="ProductModel"/> 枚举成员是下划线（<c>MODEL_A</c>），
    /// 这里做一次归一化映射；<b>型号名不认识时直接抛</b>——配错了却当成"没有配方"，
    /// 现场会表现成"检测参数全走默认值"，比启动失败难查得多。
    /// </para>
    /// <para>文件不存在 = 本机没有配方（返回空集，不抛）：这是"没配"，不是"配错"。</para>
    /// </remarks>
    public class JsonAlgorithmConfigProvider : IAlgorithmConfigProvider
    {
        private readonly string _configPath;
        private readonly ConcurrentDictionary<ProductModel, AlgorithmConfig> _cache;
        private readonly FileSystemWatcher? _watcher;

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public event EventHandler<AlgorithmConfigChangedEventArgs>? ProfileChanged;

        public JsonAlgorithmConfigProvider()
        {
            _configPath = Path.Combine(AppContext.BaseDirectory, "config", "algorithm-profiles.json");
            _cache = new ConcurrentDictionary<ProductModel, AlgorithmConfig>();

            LoadeAll();

            string? directory = Path.GetDirectoryName(_configPath);
            if (directory is null || !Directory.Exists(directory))
            {
                return;
            }

            _watcher = new FileSystemWatcher(directory)
            {
                Filter = Path.GetFileName(_configPath),
                InternalBufferSize = 65536,
                EnableRaisingEvents = true

            };

            _watcher.Changed += async (_, _) =>
            {
                Thread.Sleep(150);
                LoadeAll();
                ProfileChanged?.Invoke(this, new AlgorithmConfigChangedEventArgs());

            };
        }

        private void LoadeAll()
        {
            // 没有文件就是没有配方：不抛（"没配"与"配错"必须分开）
            if (!File.Exists(_configPath))
            {
                return;
            }

            try
            {
                using var stream = File.OpenRead(_configPath);
                AlgorithmProfilesFile? root = JsonSerializer.Deserialize<AlgorithmProfilesFile>(stream, Options);

                if (root?.Profiles is null)
                {
                    return;
                }

                foreach (AlgorithmConfig profile in root.Profiles)
                {
                    if (!profile.Enabled)
                    {
                        continue;
                    }

                    if (!TryMapModel(profile.ProductModel, out ProductModel model))
                    {
                        throw new InvalidOperationException(
                            $"algorithm-profiles.json 里的 productModel “{profile.ProductModel}” 不是已知型号" +
                            $"（{string.Join(" / ", Enum.GetNames<ProductModel>())}）；新增型号要同时在 Core.Vision.ProductModel 里加成员");
                    }

                    _cache.AddOrUpdate(model, profile, (_, _) => profile);
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new IOException($"{Path.GetFileName(_configPath)}文件读取失败:{ex.Message}");
            }
        }

        /// <summary>型号名映射：去掉连字符/空格并转大写后与枚举成员比对（MODEL-A → MODEL_A）。</summary>
        private static bool TryMapModel(string? productModel, out ProductModel model)
        {
            model = default;

            if (string.IsNullOrWhiteSpace(productModel))
            {
                return false;
            }

            string normalized = productModel.Replace('-', '_').Replace(' ', '_').Trim();

            return Enum.TryParse(normalized, ignoreCase: true, out model);
        }

        public IReadOnlyList<AlgorithmConfig> GetAllConfigs()
        {
            var list = new List<AlgorithmConfig>();
            foreach (var item in _cache.Keys)
            {
                AlgorithmConfig algorithmConfig;
                _cache.TryGetValue(item, out algorithmConfig);
                list.Add(algorithmConfig);
            }
            return list;
        }

        public AlgorithmConfig GetConfig(ProductModel model)
        {
            AlgorithmConfig algorithmConfig;
            _cache.TryGetValue(model, out algorithmConfig);
            return algorithmConfig;
        }

        public bool TryGetConfig(ProductModel model, out AlgorithmConfig? profile)
        {
            return _cache.TryGetValue(model, out profile);
        }

        /// <summary>文件根对象。</summary>
        private sealed class AlgorithmProfilesFile
        {
            public List<AlgorithmConfig> Profiles { get; set; } = new();
        }
    }
}

using Core.Models;
using Core.Vision;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace App.Infrastructure
{
    public class JsonAlgorithmConfigProvider : IAlgorithmConfigProvider
    {
        private readonly string _configPath;
        private readonly ConcurrentDictionary<ProductModel, AlgorithmConfig> _cache;
        private readonly FileSystemWatcher _watcher;

        public event EventHandler<AlgorithmConfigChangedEventArgs>? ProfileChanged;
        
        public JsonAlgorithmConfigProvider()
        {
            _configPath = Path.Combine(AppContext.BaseDirectory, "config", "algorithm-profile.json");
            _cache = new ConcurrentDictionary<ProductModel, AlgorithmConfig>();

            LoadeAll();

            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_configPath)!)
            {
                Filter = Path.GetFileName(_configPath),
                InternalBufferSize = 65536,
                EnableRaisingEvents = true

            };

            _watcher.Changed += async (_, _) =>
            {
                Thread.Sleep(150);
                LoadeAll();
                ProfileChanged?.Invoke(this,new AlgorithmConfigChangedEventArgs());

            };
            
        }

        private void LoadeAll()
        {
            try
            {
                using var stream = File.OpenRead(_configPath);
                var root = JsonSerializer.Deserialize<Dictionary<ProductModel, AlgorithmConfig>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (root == null) return;

                foreach (var item in root.Where(item => item.Value.Enabled))
                {
                    _cache.AddOrUpdate(item.Key,item.Value,(k,old)=>item.Value);
                }
                    
                
            }catch(Exception ex)
            {
                throw new IOException($"{Path.GetFileName(_configPath)}文件读取失败:{ex.Message}");

            }
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
    }
}

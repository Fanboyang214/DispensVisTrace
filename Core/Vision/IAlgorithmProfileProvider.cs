using Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Vision
{
    public interface IAlgorithmConfigProvider
    {
        AlgorithmConfig GetConfig(ProductModel model);
        bool TryGetConfig(ProductModel model, out AlgorithmConfig? profile);

        // 可选：热更新支持
        IReadOnlyList<AlgorithmConfig> GetAllConfigs();
        event EventHandler<AlgorithmConfigChangedEventArgs>? ProfileChanged;
    }

    public enum ProductModel
    {
        MODEL_A,
        MODEL_B
    }

}

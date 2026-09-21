using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.ResourceTree
{
    /// <summary>
    /// 描述资源树节点所代表的工业运行时资源类型。
    /// 该值以文本形式持久化，以便在不更改数据库中现有数值的情况下添加新的资源类型。
    /// </summary>
    public enum ResourceType
    {
        /// <summary> 未知类型 </summary>
        Unknown = 0,
        /// <summary> 工厂 </summary>
        Factory = 1,
        /// <summary> 区域 </summary>
        Area = 2,
        /// <summary> 生产线 </summary>
        Line = 3,
        /// <summary> 单元/工位 </summary>
        Cell = 4,
        /// <summary> 设备 </summary>
        Device = 5,
        /// <summary> 标签/数据点 </summary>
        Tag = 6,
        /// <summary> 菜单 </summary>
        Menu = 7,
        /// <summary> 报警 </summary>
        Alarm = 8,
        /// <summary> 规则/逻辑 </summary>
        Rule = 9,
        /// <summary> 系统 </summary>
        System = 10
    }

}

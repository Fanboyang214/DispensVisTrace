using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 多条件报警定义的连接模式。
    /// </summary>
    public enum AlarmExpressionJoin : byte
    {
        /// <summary> 逻辑与：所有条件必须同时满足 </summary>
        And = 0,
        /// <summary> 逻辑或：任一条件满足即可触发 </summary>
        Or = 1
    }

    /// <summary>
    /// 报警定义的确认策略。
    /// </summary>
    public enum AlarmAckPolicy : byte
    {
        /// <summary> 不需要操作员确认 </summary>
        NotRequired = 0,
        /// <summary> 必须确认（可在清除前或清除后） </summary>
        Required = 1,
        /// <summary> 必须在报警条件清除前确认 </summary>
        RequiredBeforeClear = 2
    }

    /// <summary>
    /// 活跃报警返回清除状态的方式。
    /// </summary>
    public enum AlarmClearPolicy : byte
    {
        /// <summary> 当报警条件不再满足时自动清除 </summary>
        AutoClearWhenConditionFalse = 0,
        /// <summary> 通过显式的清除表达式判定 </summary>
        ExplicitClearExpression = 1,
        /// <summary> 需要人工重置 </summary>
        ManualReset = 2
    }

    /// <summary>
    /// 请求的工作流家族。
    /// 可执行的工作流由 RuleBuilder 独立生成，并可在运行时热切。
    /// </summary>
    public enum AlarmWorkflowType : byte
    {
        /// <summary> 基于表达式触发的简单报警 </summary>
        Expression = 0,
        /// <summary> 组合报警（依赖多个标签或条件） </summary>
        Composite = 1,
        /// <summary> 状态敏感报警（根据设备当前状态改变策略） </summary>
        StateAware = 2
    }
}

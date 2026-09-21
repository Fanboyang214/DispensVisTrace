using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 报警比较运算符 — 用于结构化条件配置。
    /// 替代让用户手写 ConditionExpression，改为下拉框选择。
    /// </summary>
    public enum AlarmOperator : byte
    {
        /// <summary>大于</summary>
        GreaterThan = 0,

        /// <summary>大于等于</summary>
        GreaterThanOrEqual = 1,

        /// <summary>小于</summary>
        LessThan = 2,

        /// <summary>小于等于</summary>
        LessThanOrEqual = 3,

        /// <summary>等于</summary>
        Equal = 4,

        /// <summary>不等于</summary>
        NotEqual = 5,

        /// <summary>区间内（Low &lt;= Value &lt;= High）</summary>
        InRange = 6,

        /// <summary>区间外（Value &lt; Low 或 Value &gt; High）</summary>
        OutOfRange = 7,

        /// <summary>变化率超过阈值</summary>
        RateOfChange = 8
    }
}

using System;

namespace Motion
{
    /// <summary>
    /// 轨迹段几何类型
    /// </summary>
    public enum SegmentType
    {
        /// <summary>直线段</summary>
        Line,

        /// <summary>圆弧段</summary>
        Arc
    }

    /// <summary>
    /// 运动模式：点胶（出胶） / 空移（抬刀，不出胶）
    /// </summary>
    public enum MotionMode
    {
        /// <summary>点胶，出胶</summary>
        Dispense,

        /// <summary>空移，抬刀不出胶</summary>
        Move
    }

    /// <summary>
    /// IO动作类型
    /// </summary>
    public enum IoActionType
    {
        /// <summary>开胶阀</summary>
        ValveOn,

        /// <summary>关胶阀</summary>
        ValveOff,

        /// <summary>延时等待</summary>
        Delay
    }

    
}

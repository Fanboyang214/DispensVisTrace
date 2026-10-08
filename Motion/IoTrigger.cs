using Core.Motion;

namespace Motion
{
    /// <summary>
    /// 轨迹段内 IO 触发事件
    /// </summary>
    /// <remarks>
    /// 绑定的是<b>本段起点开始的行进距离</b>，不是绝对机械坐标。
    /// 因此工件旋转平移补偿时，IO 时序自动跟随，无需修改 IO 配置。
    /// </remarks>
    public class IoTrigger
    {
        /// <summary>从本段起点行进多少 mm 触发动作</summary>
        public double DistanceOnSegment;

        /// <summary>IO 动作类型</summary>
        public IoActionType Action;

        /// <summary>延时毫秒，仅 <see cref="IoActionType.Delay"/> 类型生效</summary>
        public double DelayMs;
    }
}

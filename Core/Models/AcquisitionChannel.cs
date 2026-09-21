using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 采集数据管道 — 封装 <see cref="Channel{T}"/> 作为生产者-消费者队列，
    /// 支持背压（有界容量 50000），防止消费者过载导致内存溢出。
    /// </summary>
    public sealed class AcquisitionChannel
    {
        private readonly Channel<TagValue> _channel;

        /// <summary>
        /// 创建有界采集管道。
        /// </summary>
        /// <param name="capacity">最大缓冲容量，默认 50000 条测点值</param>
        public AcquisitionChannel(int capacity = 50_000)
        {
            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait // 背压：消费者慢时生产者等待
            };
            _channel = Channel.CreateBounded<TagValue>(options);
        }

        /// <summary>消费者读取端，由下游 Processing/Storage 层消费。</summary>
        public ChannelReader<TagValue> Reader => _channel.Reader;

        /// <summary>生产者写入端，由 <see cref="DeviceCollector"/> 写入采集数据。</summary>
        public ChannelWriter<TagValue> Writer => _channel.Writer;
    }


}

using Core.Interfaces;
using Core.Models;
using Core.Motion;
using Core.Motion.Zmotion;
using Opc.Ua;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Plugins.Motion
{
    public class ZMotionDriver : IProtocolDriver
    {

        private readonly IMotionCardConnector _connector;
        private readonly IZmotionCard _zmotionCard;
        private readonly string endpoint;
        private readonly Dictionary<int, float> pendingTargets = new();
        private readonly Dictionary<string, object?> parameters = new(StringComparer.OrdinalIgnoreCase);

        public string DriverType => "ZMotionDriver";

        public bool IsConnected => _connector.IsConnected;

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            _connector.EnsureConnected(endpoint);

            return;

        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(DisconnectAsync());
        }

        public Task<IReadOnlyList<TagValue>> ReadTagsAsync(IEnumerable<TagPoint> tags, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsConnected)
            {
                throw new InvalidOperationException("未连接到ZMotion卡");
            }
            var tagValues = new List<TagValue>();
            foreach (var tag in tags.Where(tag => tag.Access != TagAccess.Write))
            {
                Quality quality = IsConnected ? Quality.Good : Quality.Uncertain;

                tagValues.Add(ReadOne(tag, quality));

            }
            return Task.FromResult<IReadOnlyList<TagValue>>(tagValues);
        }

        private TagValue ReadOne(TagPoint tag, Quality quality)
        {
            var address = tag.Address.Split('.');
            Match m = Regex.Match(address[0], @"^(.*?)(\d+)$");
            if (address.Length < 2 || !m.Success)
            {
                throw new ArgumentException($"无效地址，来自 {tag.Name}. 正确示例: 'axis1.idle'");
            }
            Object? value = null;
            switch (m.Groups[0].Value)
            {
                case "axis":
                    switch (address[1])
                    {
                        case "dpos":
                            value = _zmotionCard.GetDpos(int.Parse(m.Groups[1].Value));
                            break;
                        case "speed":
                            value = _zmotionCard.GetSpeed(int.Parse(m.Groups[1].Value));
                            break;
                        case "idle":
                            value = _zmotionCard.GetIsIdle(int.Parse(m.Groups[1].Value));
                            break;
                        case "homed":
                            value = _zmotionCard.GetHomeStatus(int.Parse(m.Groups[1].Value));
                            break;
                        case "target":
                            parameters.TryGetValue(tag.Name, out value);
                            break;

                        default:

                            throw new ArgumentException($"无效地址，来自 {tag.Name}. 正确示例: 'axis1.idle'");

                    }

                    break;

                case "program":

                case "param":
                    if (!parameters.TryGetValue(tag.Name, out value))
                    {
                        quality = Quality.Bad;
                    }
                    break;

                default:
                    throw new ArgumentException($"无效地址，来自 {tag.Name}. 正确示例: 'axis1.idle'");
            }

            return new TagValue
            {
                TagId = tag.Id,
                TagName = tag.Name,
                Value = value,
                Quality = quality,
                Timestamp = DateTimeOffset.Now,
                DataType = tag.DataType switch
                {
                    TagDataType.Bool => typeof(bool),
                    TagDataType.Int16 => typeof(short),
                    TagDataType.Int32 => typeof(int),
                    TagDataType.Int64 => typeof(long),
                    TagDataType.Float32 => typeof(float),
                    TagDataType.Float64 => typeof(double),
                    TagDataType.UInt16 => typeof(ushort),
                    TagDataType.UInt32 => typeof(uint),
                    TagDataType.String => typeof(string),
                    _ => throw new NotImplementedException(),
                }
            };
        }

        public Task WriteTagAsync(TagPoint tag, object value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            throw new NotSupportedException("ZMotionDriver不支持写入标签"); 

            //if (!IsConnected)
            //{
            //    throw new InvalidOperationException("未连接到ZMotion卡");
            //}
            //var address = tag.Address.Split('.');
            //Match m = Regex.Match(address[0], @"^(.*?)(\d+)$");
            //if (address.Length < 2 || !m.Success)
            //{
            //    throw new ArgumentException($"无效地址，来自 {tag.Name}. 正确示例: 'axis1.idle'");
            //}

            //switch (m.Groups[0].Value)
            //{
            //    case "axis":
            //        switch (address[1])
            //        {


            //            case "target":
            //                parameters.Add(tag.Name, value);
            //                _zmotionCard.MoveAbsolute(int.Parse(m.Groups[1].Value), (float)Convert.ToDouble(value));
            //                break;

            //            case "jog":
            //                switch (address[2])
            //                {
            //                    case "move":
            //                        _zmotionCard.Jog(int.Parse(m.Groups[1].Value), Convert.ToBoolean(value));
            //                        break;
            //                    default:
            //                        throw new ArgumentException($"无效地址，来自 {tag.Name}. 正确示例: 'axis3.jog.move'");
            //                }
            //                break;
            //            default:

            //                throw new ArgumentException($"无效地址，来自 {tag.Name}. 正确示例: 'axis1.idle'");

            //        }

            //        break;


            //    case "param":
            //       parameters.Add(tag.Name, value);
            //        break;

            //    default:
            //        throw new ArgumentException($"无效地址，来自 {tag.Name}. 正确示例: 'axis1.idle'");


            //}
            return Task.CompletedTask;
        }
    } 
}

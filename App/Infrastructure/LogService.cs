using Core.Logging;
using Microsoft.Extensions.Logging;
using System;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace App.Infrastructure
{
    /// <summary>
    /// 基于 Microsoft.Extensions.Logging（MEL）的 <see cref="ILogService"/> 实现。
    /// <para>
    /// 这里是两套日志体系的汇合点：MEL 的 <see cref="ILoggerFactory"/> 由 NLog 的
    /// LoggerProvider 支撑（见 App.xaml.cs 的 ConfigureLogging），因此本类与项目里
    /// 直接注入 <c>ILogger&lt;T&gt;</c> 的模块（如 Acquisition）最终写的是同一批文件、
    /// 走同一份规则。
    /// </para>
    /// <para>
    /// 日志的落点（目录、滚动、归档、级别过滤）全部由可执行文件目录下的 NLog.config 决定，
    /// 现场需要调整日志行为时改配置即可，无需重新编译。
    /// </para>
    /// <para>
    /// 实例本身是轻量的：MEL 内部按类别名缓存 Logger，因此可以放心地通过
    /// <see cref="ForContext(string)"/> 为每个类型创建一个实例。
    /// </para>
    /// </summary>
    public sealed class LogService : ILogService
    {
        /// <summary>默认日志器名称，用于还没有类型上下文的场景（如程序启动引导阶段）。</summary>
        public const string DefaultLoggerName = "DispensVisTrace";

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private readonly string _name;

        /// <summary>
        /// 使用共享的日志工厂创建实例。
        /// <para>
        /// 这是唯一入口，也是本类与项目内其他模块共用同一套日志管线的保证：
        /// 请勿在构造函数里自行 <c>LoggerFactory.Create</c>，否则会多出一个独立的
        /// Provider，重复写入同一批 NLog 目标。
        /// </para>
        /// </summary>
        /// <param name="loggerFactory">日志工厂，其 Provider 已由宿主配置为 NLog。</param>
        /// <param name="name">日志器名称；为空时回退到 <see cref="DefaultLoggerName"/>。</param>
        public LogService(ILoggerFactory loggerFactory, string name)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _name = string.IsNullOrWhiteSpace(name) ? DefaultLoggerName : name;
            _logger = loggerFactory.CreateLogger(_name);
        }

        /// <inheritdoc />
        public string Name => _name;

        /// <inheritdoc />
        public bool IsEnabled(Core.Logging.LogLevel level) => _logger.IsEnabled(ToMelLevel(level));

        /// <inheritdoc />
        public ILogService ForContext(string name) => new LogService(_loggerFactory, name);

        /// <inheritdoc />
        public void Log(Core.Logging.LogLevel level, string message) => Write(level, null, message, Array.Empty<object?>());

        /// <inheritdoc />
        public void Log(Core.Logging.LogLevel level, string messageTemplate, params object?[] args) => Write(level, null, messageTemplate, args);

        /// <inheritdoc />
        public void Log(Core.Logging.LogLevel level, Exception exception, string message) => Write(level, exception, message, Array.Empty<object?>());

        /// <inheritdoc />
        public void Log(Core.Logging.LogLevel level, Exception exception, string messageTemplate, params object?[] args) => Write(level, exception, messageTemplate, args);

        /// <summary>
        /// 所有重载的唯一出口：把核心层级别映射为 MEL 级别后交给 <see cref="ILogger"/>。
        /// MEL 会把异常与消息模板原样传给 NLog。Provider，由 NLog.config 的
        /// <c>${message:withexception=true}</c> 渲染，堆栈不会丢。
        /// </summary>
        private void Write(Core.Logging.LogLevel level, Exception? exception, string message, object?[] args)
        {
            var melLevel = ToMelLevel(level);
            if (!_logger.IsEnabled(melLevel))
                return;

            _logger.Log(melLevel, exception, message, args);
        }

        /// <summary>
        /// 把 <see cref="Core.Logging.LogLevel"/> 映射为 MEL 的 <see cref="MelLogLevel"/>。
        /// 两套枚举的数值顺序一致（Trace=0…Fatal/Critical=5），但仍然显式映射，
        /// 避免将来任何一方调整数值时静默错级。
        /// </summary>
        private static MelLogLevel ToMelLevel(Core.Logging.LogLevel level) => level switch
        {
            Core.Logging.LogLevel.Trace => MelLogLevel.Trace,
            Core.Logging.LogLevel.Debug => MelLogLevel.Debug,
            Core.Logging.LogLevel.Info => MelLogLevel.Information,
            Core.Logging.LogLevel.Warn => MelLogLevel.Warning,
            Core.Logging.LogLevel.Error => MelLogLevel.Error,
            Core.Logging.LogLevel.Fatal => MelLogLevel.Critical,
            // 未定义的枚举值按 Error 处理：宁可多记也不静默丢弃
            _ => MelLogLevel.Error
        };
    }
}

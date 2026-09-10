using Core.Logging;
using System;
using NLogLevel = NLog.LogLevel;
using NLogLogger = NLog.Logger;

namespace App.Infrastructure
{
    /// <summary>
    /// 基于 NLog 的 <see cref="ILogService"/> 实现。
    /// <para>
    /// 本类只负责把 <see cref="Core.Logging.LogLevel"/> 映射到 NLog 并转发，
    /// 日志的落点（目录、滚动、归档、级别过滤）全部由可执行文件目录下的 NLog.config 决定，
    /// 现场需要调整日志行为时改配置即可，无需重新编译。
    /// </para>
    /// <para>
    /// 实例本身是轻量的：NLog 内部按名称缓存 Logger，因此可以放心地通过
    /// <see cref="ForContext(string)"/> 为每个类型创建一个实例。
    /// </para>
    /// </summary>
    public sealed class LogService : ILogService
    {
        /// <summary>默认日志器名称，用于还没有类型上下文的场景（如程序启动引导阶段）。</summary>
        public const string DefaultLoggerName = "DispensVisTrace";

        private readonly NLogLogger _logger;

        /// <summary>使用默认日志器名称创建实例。</summary>
        public LogService()
            : this(DefaultLoggerName)
        {
        }

        /// <summary>使用指定名称创建实例，名称通常取类型全名。</summary>
        /// <param name="name">日志器名称；为空时回退到 <see cref="DefaultLoggerName"/>。</param>
        public LogService(string name)
        {
            _logger = NLog.LogManager.GetLogger(string.IsNullOrWhiteSpace(name) ? DefaultLoggerName : name);
        }

        /// <inheritdoc />
        public string Name => _logger.Name;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel level) => _logger.IsEnabled(ToNLogLevel(level));

        /// <inheritdoc />
        public ILogService ForContext(string name) => new LogService(name);

        /// <inheritdoc />
        public void Log(LogLevel level, string message) => _logger.Log(ToNLogLevel(level), message);

        /// <inheritdoc />
        public void Log(LogLevel level, string messageTemplate, params object?[] args) => _logger.Log(ToNLogLevel(level), messageTemplate, args);

        /// <inheritdoc />
        public void Log(LogLevel level, Exception exception, string message) => _logger.Log(ToNLogLevel(level), exception, message);

        /// <inheritdoc />
        public void Log(LogLevel level, Exception exception, string messageTemplate, params object?[] args) => _logger.Log(ToNLogLevel(level), exception, messageTemplate, args);

        /// <summary>把 <see cref="Core.Logging.LogLevel"/> 映射为 NLog 的级别。</summary>
        private static NLogLevel ToNLogLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => NLogLevel.Trace,
            LogLevel.Debug => NLogLevel.Debug,
            LogLevel.Info => NLogLevel.Info,
            LogLevel.Warn => NLogLevel.Warn,
            LogLevel.Error => NLogLevel.Error,
            LogLevel.Fatal => NLogLevel.Fatal,
            // 未定义的枚举值按 Error 处理：宁可多记也不静默丢弃
            _ => NLogLevel.Error
        };
    }
}

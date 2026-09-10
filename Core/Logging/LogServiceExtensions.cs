using System;

namespace Core.Logging
{
    /// <summary>
    /// <see cref="ILogService"/> 的便捷扩展，提供 Trace/Debug/Info/Warn/Error/Fatal 六个级别
    /// 与「纯文本 / 消息模板 / 异常 / 异常+消息模板」四种形态的组合。
    /// </summary>
    public static class LogServiceExtensions
    {
        /// <summary>按类型创建子日志器，等价于 <c>log.ForContext(typeof(T).FullName)</c>。</summary>
        public static ILogService ForContext<T>(this ILogService log) => log.ForContext(typeof(T).FullName!);

        public static void Trace(this ILogService log, string message) => log.Log(LogLevel.Trace, message);

        public static void Trace(this ILogService log, string messageTemplate, params object?[] args) => log.Log(LogLevel.Trace, messageTemplate, args);

        public static void Trace(this ILogService log, Exception? exception, string message) => Write(log, LogLevel.Trace, exception, message);

        public static void Trace(this ILogService log, Exception? exception, string messageTemplate, params object?[] args) => Write(log, LogLevel.Trace, exception, messageTemplate, args);

        public static void Debug(this ILogService log, string message) => log.Log(LogLevel.Debug, message);

        public static void Debug(this ILogService log, string messageTemplate, params object?[] args) => log.Log(LogLevel.Debug, messageTemplate, args);

        public static void Debug(this ILogService log, Exception? exception, string message) => Write(log, LogLevel.Debug, exception, message);

        public static void Debug(this ILogService log, Exception? exception, string messageTemplate, params object?[] args) => Write(log, LogLevel.Debug, exception, messageTemplate, args);

        public static void Info(this ILogService log, string message) => log.Log(LogLevel.Info, message);

        public static void Info(this ILogService log, string messageTemplate, params object?[] args) => log.Log(LogLevel.Info, messageTemplate, args);

        public static void Info(this ILogService log, Exception? exception, string message) => Write(log, LogLevel.Info, exception, message);

        public static void Info(this ILogService log, Exception? exception, string messageTemplate, params object?[] args) => Write(log, LogLevel.Info, exception, messageTemplate, args);

        public static void Warn(this ILogService log, string message) => log.Log(LogLevel.Warn, message);

        public static void Warn(this ILogService log, string messageTemplate, params object?[] args) => log.Log(LogLevel.Warn, messageTemplate, args);

        public static void Warn(this ILogService log, Exception? exception, string message) => Write(log, LogLevel.Warn, exception, message);

        public static void Warn(this ILogService log, Exception? exception, string messageTemplate, params object?[] args) => Write(log, LogLevel.Warn, exception, messageTemplate, args);

        public static void Error(this ILogService log, string message) => log.Log(LogLevel.Error, message);

        public static void Error(this ILogService log, string messageTemplate, params object?[] args) => log.Log(LogLevel.Error, messageTemplate, args);

        public static void Error(this ILogService log, Exception? exception, string message) => Write(log, LogLevel.Error, exception, message);

        public static void Error(this ILogService log, Exception? exception, string messageTemplate, params object?[] args) => Write(log, LogLevel.Error, exception, messageTemplate, args);

        public static void Fatal(this ILogService log, string message) => log.Log(LogLevel.Fatal, message);

        public static void Fatal(this ILogService log, string messageTemplate, params object?[] args) => log.Log(LogLevel.Fatal, messageTemplate, args);

        public static void Fatal(this ILogService log, Exception? exception, string message) => Write(log, LogLevel.Fatal, exception, message);

        public static void Fatal(this ILogService log, Exception? exception, string messageTemplate, params object?[] args) => Write(log, LogLevel.Fatal, exception, messageTemplate, args);

        /// <summary>异常为 null 时退化为普通日志，避免调用方到处写空判断。</summary>
        private static void Write(ILogService log, LogLevel level, Exception? exception, string message)
        {
            if (exception is null)
                log.Log(level, message);
            else
                log.Log(level, exception, message);
        }

        /// <summary>异常为 null 时退化为普通日志，避免调用方到处写空判断。</summary>
        private static void Write(ILogService log, LogLevel level, Exception? exception, string messageTemplate, object?[] args)
        {
            if (exception is null)
                log.Log(level, messageTemplate, args);
            else
                log.Log(level, exception, messageTemplate, args);
        }
    }
}

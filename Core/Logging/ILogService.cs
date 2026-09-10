using System;

namespace Core.Logging
{
    /// <summary>
    /// 日志服务抽象。
    /// <para>
    /// Core 及各业务模块只依赖本接口，具体实现（目前是 NLog）由宿主程序 App 通过容器注入，
    /// 因此类库不需要引用任何日志框架。
    /// </para>
    /// <para>
    /// 日常调用建议使用 <see cref="LogServiceExtensions"/> 提供的 Info/Warn/Error 等便捷方法。
    /// </para>
    /// </summary>
    public interface ILogService
    {
        /// <summary>当前日志器名称，通常为类型全名。</summary>
        string Name { get; }

        /// <summary>
        /// 判断指定级别是否会被输出。
        /// 用于在拼装日志文本本身开销较大时提前跳过，例如：
        /// <c>if (log.IsEnabled(LogLevel.Debug)) log.Debug(JsonSerializer.Serialize(result));</c>
        /// </summary>
        bool IsEnabled(LogLevel level);

        /// <summary>
        /// 创建带指定名称的子日志器，便于按模块或类型区分的日志来源。
        /// </summary>
        /// <param name="name">日志器名称，建议使用类型全名。</param>
        ILogService ForContext(string name);

        /// <summary>记录一条纯文本日志。</summary>
        void Log(LogLevel level, string message);

        /// <summary>
        /// 记录一条消息模板日志，如 <c>"定位耗时 {Elapsed} ms"</c>。
        /// 占位符会按顺序被 <paramref name="args"/> 填充。
        /// </summary>
        void Log(LogLevel level, string messageTemplate, params object?[] args);

        /// <summary>记录一条带异常堆栈的日志。</summary>
        void Log(LogLevel level, Exception exception, string message);

        /// <summary>记录一条带异常堆栈的消息模板日志。</summary>
        void Log(LogLevel level, Exception exception, string messageTemplate, params object?[] args);
    }
}

namespace Core.Logging
{
    /// <summary>
    /// 日志级别，严重程度由低到高，与 NLog 的级别一一对应。
    /// </summary>
    public enum LogLevel
    {
        /// <summary>最详细的跟踪信息，仅用于开发期排查。</summary>
        Trace = 0,

        /// <summary>调试信息，用于还原程序内部执行过程。</summary>
        Debug = 1,

        /// <summary>应用生命周期、配置变更等正常但值得记录的信息。</summary>
        Info = 2,

        /// <summary>可恢复的异常、校验告警等需要注意但不影响运行的情况。</summary>
        Warn = 3,

        /// <summary>功能已失败或捕获到异常。</summary>
        Error = 4,

        /// <summary>致命错误，应用即将终止。</summary>
        Fatal = 5
    }
}

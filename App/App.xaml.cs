using App.Infrastructure;
using Core.Logging;
using Core.Vision;
using NLog;
using NLog.Config;
using NLog.Targets;
using Prism.Ioc;
using Prism.Unity;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace App
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    /// <remarks>
    /// 类名不叫 App：项目名与命名空间都是 App，若应用类也叫 App，
    /// XAML 生成的入口代码 App.App app = new App.App(); 会因为
    /// “App”先绑定到类型而不是命名空间而报 CS0426。
    /// </remarks>
    public partial class DispensVisTraceApp : PrismApplication
    {
        /// <summary>日志根目录，与 NLog.config 中的 logDir 保持一致（仅用于提示操作员）。</summary>
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DispensVisTrace",
            "logs");

        private ILogService _log = new LogService(LogService.DefaultLoggerName);

        protected override void OnStartup(StartupEventArgs e)
        {
            // 必须早于 base.OnStartup：Prism 会在其中创建容器和主窗口，
            // 在此之前装载好日志，启动阶段的任何异常才留得下记录
            ConfigureLogging();

            base.OnStartup(e);

            _log = Container.Resolve<ILogService>();

            HookGlobalExceptionHandlers();
            _log.Info("应用启动，版本 {Version}，日志目录 {LogDirectory}", GetType().Assembly.GetName().Version, LogDirectory);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _log.Info("应用退出，退出码 {ExitCode}", e.ApplicationExitCode);

            // 全量日志走 AsyncWrapper 异步写入，退出前必须 Flush，否则最后一批日志会丢
            LogManager.Shutdown();

            base.OnExit(e);
        }

        protected override Window CreateShell()
        {
            return Container.Resolve<MainWindow>();
        }

        protected override void InitializeModules()
        {
            base.InitializeModules();
        }

        protected override void OnInitialized()
        {
            base.OnInitialized();
        }

        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterSingleton<IAlgorithmConfigProvider, JsonAlgorithmConfigProvider>();

            // 日志服务全局单例：LogService 本身无状态，按类型 ForContext<T>() 可得到独立命名的子日志器
            containerRegistry.RegisterSingleton<ILogService>(() => new LogService(LogService.DefaultLoggerName));
        }

        /// <summary>
        /// 装载 exe 同目录下的 NLog.config。
        /// 配置缺失或解析失败时退回内置的最小文件配置，避免出现
        /// “日志系统自己坏了所以什么都看不到”的最坏情况。
        /// </summary>
        private static void ConfigureLogging()
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "NLog.config");

            try
            {
                if (!File.Exists(configPath))
                    throw new FileNotFoundException($"未找到 NLog 配置文件：{configPath}", configPath);

                LogManager.Setup().LoadConfigurationFromFile(configPath);

                // XML 结构错误时 NLog 默认只写内部日志而不抛异常，这里显式判定，
                // 保证配置真的坏了会走到下面的兜底分支
                if (LogManager.Configuration is null || LogManager.Configuration.AllTargets.Count == 0)
                    throw new InvalidOperationException($"NLog.config 未解析出任何 target：{configPath}");
            }
            catch (Exception ex)
            {
                ApplyFallbackLogging(ex);
            }
        }

        /// <summary>兜底配置：只有一个同步文件目标，保证任何情况下都有日志可查。</summary>
        private static void ApplyFallbackLogging(Exception cause)
        {
            var fileTarget = new FileTarget("fallbackFile")
            {
                FileName = Path.Combine(LogDirectory, "app-${shortdate}.log"),
                Layout = "${longdate}|${level:uppercase=true}|${logger}|${message:withexception=true}",
                Header = "===== DispensVisTrace (兜底日志配置) 启动 @ ${longdate} =====",
                Encoding = Encoding.UTF8,
                CreateDirs = true,
                KeepFileOpen = true,
                AutoFlush = true,
                MaxArchiveDays = 7
            };

            var config = new LoggingConfiguration();
            config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, fileTarget);
            LogManager.Configuration = config;

            LogManager.GetLogger(LogService.DefaultLoggerName)
                      .Fatal(cause, "NLog.config 装载失败，已退回内置兜底日志配置（仅单个文件目标，保留 7 天）");
        }

        private void HookGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        /// <summary>UI 线程未处理异常：记录后提示操作员，尽量不让产线程序直接消失。</summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _log.Fatal(e.Exception, "UI 线程发生未处理异常");
            NotifyOperator(e.Exception);

            // 置为已处理避免进程立即退出；若希望异常直接终止程序，删掉这一行即可
            e.Handled = true;
        }

        /// <summary>非 UI 线程的未处理异常，进程即将终止，此处是最后一条日志。</summary>
        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
                _log.Fatal(exception, "发生无法捕获的致命错误，进程即将终止（IsTerminating={IsTerminating}）", e.IsTerminating);
            else
                _log.Fatal("发生无法捕获的致命错误，进程即将终止（IsTerminating={IsTerminating}，异常对象：{ExceptionObject}）", e.IsTerminating, e.ExceptionObject);

            // 进程马上要终止，异步目标必须同步 Flush 一次
            LogManager.Shutdown();
        }

        /// <summary>被遗弃的 Task 异常，默认不会导致崩溃，记录后标记为已观察。</summary>
        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            _log.Error(e.Exception, "后台任务出现未观察异常");
            e.SetObserved();
        }

        private static void NotifyOperator(Exception exception)
        {
            var message = $"程序发生未处理异常，已写入日志。\n\n{exception.Message}\n\n日志目录：{LogDirectory}";

            try
            {
                MessageBox.Show(message, "DispensVisTrace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // 连弹窗都失败时不再向上抛，避免二次异常掩盖原始异常
            }
        }
    }
}

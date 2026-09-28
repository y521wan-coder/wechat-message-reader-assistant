using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WeChatMessageReaderAssistant.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly object ErrorReportLock = new();
    private const string SingleInstanceMutexName = @"Local\WeChatMessageReaderAssistant.SingleInstance";
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "微信消息朗读助手已经在运行了，不需要重复打开。\r\n\r\n如果你想打开设置，请按 Windows 加 B 到通知区域，找到微信消息朗读助手，按菜单键或 Shift 加 F10，然后选择打开主窗口和设置。",
                "微信消息朗读助手已经在运行",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // Ignore mutex cleanup errors during application exit.
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteErrorReport("界面线程未处理错误", e.Exception);
        e.Handled = true;

        System.Windows.MessageBox.Show(
            "程序遇到一个错误，已经在绿色版目录生成 错误报告.txt。请把这个文件发给开发者。程序会尽量继续运行；如果朗读异常，请重新打开软件。",
            "微信消息朗读助手错误报告",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteErrorReport("程序严重错误", e.ExceptionObject as Exception, e.ExceptionObject?.ToString());
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteErrorReport("后台任务未观察错误", e.Exception);
        e.SetObserved();
    }

    private static void WriteErrorReport(string title, Exception? exception, string? extraText = null)
    {
        try
        {
            var reportPath = Path.Combine(AppContext.BaseDirectory, "错误报告.txt");
            var builder = new StringBuilder();
            builder.AppendLine("============================================================");
            builder.AppendLine("微信消息朗读助手 错误报告");
            builder.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"错误类型: {title}");
            builder.AppendLine($"程序目录: {AppContext.BaseDirectory}");
            builder.AppendLine($"程序版本: {GetAppVersion()}");
            builder.AppendLine($"系统版本: {Environment.OSVersion}");
            builder.AppendLine($".NET版本: {Environment.Version}");
            builder.AppendLine($"电脑用户名: {Environment.UserName}");
            builder.AppendLine();

            if (exception != null)
            {
                builder.AppendLine("异常内容: ");
                builder.AppendLine(exception.ToString());
            }
            else if (!string.IsNullOrWhiteSpace(extraText))
            {
                builder.AppendLine("错误内容: ");
                builder.AppendLine(extraText);
            }
            else
            {
                builder.AppendLine("没有取得详细异常内容。");
            }

            builder.AppendLine();
            lock (ErrorReportLock)
            {
                File.AppendAllText(reportPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 写错误报告本身不能再影响程序运行。
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return "Unknown";
            }

            var info = FileVersionInfo.GetVersionInfo(processPath);
            return info.FileVersion ?? info.ProductVersion ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }
}



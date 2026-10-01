using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland;

public partial class App : Application
{
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 注册本地代码页（936 = GB2312 等），推送接口要靠它兜底解码中文
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 注册失败只影响非 UTF-8 的推送，不影响其他功能
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteLog("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteLog("Task", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog("Dispatcher", e.Exception);
        // 单个异常不该让整个岛消失
        e.Handled = true;
    }

    private static void WriteLog(string source, Exception? ex)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LogPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine();
            File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 日志失败就算了
        }
    }
}

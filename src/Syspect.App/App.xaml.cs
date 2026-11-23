using System.Windows;
using Syspect.App.Services;

namespace Syspect.App;

public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashHandlerService.ReportException(
                "AppDomain.UnhandledException",
                args.ExceptionObject as Exception,
                isFatal: args.IsTerminating,
                extraMessage: args.ExceptionObject?.ToString());
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            CrashHandlerService.ReportException("TaskScheduler.UnobservedTaskException", args.Exception, isFatal: false);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            CrashHandlerService.ReportException("App.DispatcherUnhandledException", args.Exception, isFatal: false);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            CrashHandlerService.RegisterMainWindow(mainWindow);
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            CrashHandlerService.ReportException("App.OnStartup", ex, isFatal: true);
            Shutdown(1);
        }
    }
}

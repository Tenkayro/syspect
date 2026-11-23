using Microsoft.UI.Xaml;
using Syspect.App.Services;

namespace Syspect.App;

public partial class App : Application
{
    private Window? _window;

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

        InitializeComponent();

        UnhandledException += (_, args) =>
        {
            args.Handled = true;
            CrashHandlerService.ReportException("App.UnhandledException", args.Exception, isFatal: false, extraMessage: args.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var mainWindow = new MainWindow();
            _window = mainWindow;
            CrashHandlerService.RegisterMainWindow(mainWindow);
            mainWindow.Activate();
        }
        catch (Exception ex)
        {
            CrashHandlerService.ReportException("App.OnLaunched", ex, isFatal: true);
        }
    }
}

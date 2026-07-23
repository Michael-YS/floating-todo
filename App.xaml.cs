namespace FloatingTodo;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
            AppLogger.Error("Unhandled UI exception.", args.Exception);
        System.AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is System.Exception exception)
            {
                AppLogger.Error("Unhandled application exception.", exception);
            }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            AppLogger.Error("Unobserved background task exception.", args.Exception);

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}

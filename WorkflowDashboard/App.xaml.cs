using System.IO;
using System.Threading;
using System.Windows;

namespace WorkflowDashboard;

public partial class App : Application
{
    private const string MutexName = @"Local\WorkflowDashboard_SingleInstance";
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        this.DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkflowDashboard");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    DateTime.Now + " " + args.Exception + Environment.NewLine + Environment.NewLine);
            }
            catch { }
            args.Handled = true;
        };

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            NativeMethods.ActivateExistingDashboardWindow();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var main = new MainWindow();
        MainWindow = main;
        main.Topmost = true;
        main.Show();
        main.Activate();
        main.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    main.Topmost = true;
                    NativeMethods.ForceForegroundWindow(hwnd);
                }
            }
            catch { }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch { }
        base.OnExit(e);
    }
}
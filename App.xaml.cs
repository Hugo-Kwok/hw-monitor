using System.Windows;
using System.Windows.Threading;

namespace BetterHwMonitor;

public partial class App : Application
{
    private const string MutexName = "BetterHwMonitor.SingleInstance";
    private const string ShowEventName = "BetterHwMonitor.ShowWindow";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one copy runs. Starting the exe again just brings the running window back.
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch
            {
                // the other copy is shutting down; nothing to do
            }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        base.OnStartup(e);
        Theme.Apply(Theme.LoadPreference());

        var window = new BetterHwMonitor.MainWindow();
        MainWindow = window;
        window.Show();

        var listener = new Thread(() =>
        {
            while (true)
            {
                _showEvent.WaitOne();
                Dispatcher.InvokeAsync(window.ShowFromTray);
            }
        })
        {
            IsBackground = true,
            Name = "ShowWindowListener",
        };
        listener.Start();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows sign-out / shutdown: save and let the window close for real.
        (MainWindow as BetterHwMonitor.MainWindow)?.PrepareForExit();
        base.OnSessionEnding(e);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as BetterHwMonitor.MainWindow)?.PrepareForExit();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Better HW Monitor - unexpected error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

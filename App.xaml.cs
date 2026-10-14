using System.Windows;

namespace SupportStatus;

// Application entry point.
public partial class App : Application
{
    // Names used to detect whether the app is already running for this user.
    const string MutexName = @"Local\SupportStatus";
    const string ShowEventName = @"Local\SupportStatus.Show";

    // Kept in fields so they live as long as the app.
    static Mutex singleInstance;
    static EventWaitHandle showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one copy per user. If it is already running, ask that window to show itself and exit.
        singleInstance = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        // Read settings.json and check for the /admin switch.
        var settings = Settings.Load();
        bool admin = e.Args.Any(a => a.Equals("/admin", StringComparison.OrdinalIgnoreCase));
        var win = new MainWindow(settings, admin);
        MainWindow = win;
        win.Show();

        // Wait for a signal from a second launch, then show the window.
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(win.ShowWidget), null, Timeout.Infinite, false);
    }
}

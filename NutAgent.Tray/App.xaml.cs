using System.Threading;
using System.Windows;

namespace NutAgent.Tray;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static bool   _ownsMutex;
    private TrayManager?  _tray;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        _mutex = new Mutex(true, "NutAgentTray", out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }
        _ownsMutex = true;

        _tray = new TrayManager();
        _tray.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

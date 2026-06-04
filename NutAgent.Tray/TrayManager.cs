using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using NutAgent.Tray.Ipc;

namespace NutAgent.Tray;

public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon      _notifyIcon;
    private readonly DispatcherTimer _pollTimer;
    private readonly Icon            _iconOnline;
    private readonly Icon            _iconOnBattery;
    private readonly Icon            _iconLowBattery;
    private readonly Icon            _iconDisconnected;
    private ToolStripMenuItem        _statusItem = null!;
    private SettingsWindow?          _settingsWindow;

    public TrayManager()
    {
        _iconOnline       = LoadIcon("icon_online");
        _iconOnBattery    = LoadIcon("icon_onbattery");
        _iconLowBattery   = LoadIcon("icon_lowbattery");
        _iconDisconnected = LoadIcon("icon_disconnected");

        _notifyIcon = new NotifyIcon
        {
            Icon    = _iconDisconnected,
            Text    = "NutAgent — connecting...",
            Visible = true,
        };

        BuildMenu();
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
    }

    public void Start()
    {
        _pollTimer.Start();
        _ = PollAsync();
    }

    private void BuildMenu()
    {
        _statusItem = new ToolStripMenuItem("Connecting...") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _notifyIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        });

        _notifyIcon.ContextMenuStrip = menu;
    }

    private async Task PollAsync()
    {
        try
        {
            var s = await PipeClient.GetStatusAsync();
            if (s != null) UpdateTray(s);
            else           SetDisconnected();
        }
        catch { SetDisconnected(); }
    }

    private void UpdateTray(StatusResponse s)
    {
        bool onBattery  = s.Status.Contains("OB");
        bool lowBattery = s.Status.Contains("LB");

        // Prefer the discharge-tracker estimate; fall back to device-reported runtime.
        int? runtimeSecs = s.RuntimeEst > 0 ? s.RuntimeEst : (s.RuntimeSeconds > 0 ? s.RuntimeSeconds : null);
        string rt = runtimeSecs is int sec
            ? TimeSpan.FromSeconds(sec) is var span && span.TotalHours >= 1
                ? $"~{(int)span.TotalHours}h{span.Minutes:D2}m"
                : $"~{(int)span.TotalMinutes}m"
            : "";

        Icon   icon;
        string label;

        if (!s.IsConnected)
        {
            icon  = _iconDisconnected;
            label = "UPS not connected";
        }
        else if (lowBattery)
        {
            icon  = _iconLowBattery;
            label = $"Critical — {s.Charge:F0}%{(rt != "" ? $" {rt}" : "")}";
        }
        else if (onBattery)
        {
            icon  = _iconOnBattery;
            label = $"On Battery — {s.Charge:F0}%{(rt != "" ? $" {rt}" : "")}";
        }
        else
        {
            icon  = _iconOnline;
            label = $"Online — {s.Charge:F0}%{(rt != "" ? $" {rt}" : "")}";
        }

        string model   = !string.IsNullOrEmpty(s.Model) ? s.Model : "UPS";
        string tooltip = $"NutAgent · {label}";
        if (tooltip.Length > 63) tooltip = tooltip[..63];

        _notifyIcon.Icon = icon;
        _notifyIcon.Text = tooltip;
        _statusItem.Text = $"{model} — {label}";
    }

    private void SetDisconnected()
    {
        _notifyIcon.Icon = _iconDisconnected;
        _notifyIcon.Text = "NutAgent — service not running";
        _statusItem.Text = "Service not running";
    }

    private void OpenSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
            _settingsWindow.Focus();
        }
    }

    private static Icon LoadIcon(string name)
    {
        var uri  = new Uri($"pack://application:,,,/Resources/{name}.ico");
        var info = System.Windows.Application.GetResourceStream(uri);
        return info != null ? new Icon(info.Stream) : SystemIcons.Application;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _iconOnline.Dispose();
        _iconOnBattery.Dispose();
        _iconLowBattery.Dispose();
        _iconDisconnected.Dispose();
    }
}

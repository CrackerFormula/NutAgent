using System.Windows;
using NutAgent.Tray.Ipc;

namespace NutAgent.Tray;

public partial class SettingsWindow : Window
{
    private TrayConfig? _original;

    public SettingsWindow()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        SaveBtn.IsEnabled = false;
        try
        {
            _original = await PipeClient.GetConfigAsync();
            if (_original != null) Populate(_original);
        }
        catch
        {
            MessageBox.Show("Could not connect to the NutAgent service.",
                "Connection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SaveBtn.IsEnabled = true; }
    }

    private void Populate(TrayConfig c)
    {
        bool server = c.Mode.Equals("Server", StringComparison.OrdinalIgnoreCase);
        ServerRadio.IsChecked = server;
        ClientRadio.IsChecked = !server;

        UpsNameBox.Text       = c.UpsName;
        PortBox.Text          = c.Port.ToString();
        RemoteHostBox.Text    = c.RemoteHost;
        RemotePortBox.Text    = c.RemotePort.ToString();
        RemoteUpsBox.Text     = c.RemoteUpsName;
        ChargeThreshBox.Text  = c.ShutdownBatteryThreshold.ToString();
        RuntimeThreshBox.Text = c.ShutdownRuntimeMinutes.ToString();
        ShutdownDelayBox.Text = c.ShutdownDelaySeconds.ToString();
    }

    private void ServerRadio_Checked(object sender, RoutedEventArgs e)
    {
        ServerPanel.Visibility = Visibility.Visible;
        ClientPanel.Visibility = Visibility.Collapsed;
    }

    private void ClientRadio_Checked(object sender, RoutedEventArgs e)
    {
        ServerPanel.Visibility = Visibility.Collapsed;
        ClientPanel.Visibility = Visibility.Visible;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildConfig(out var config)) return;

        SaveBtn.IsEnabled = false;
        try
        {
            var result = await PipeClient.SetConfigAsync(config!);
            if (result?.RequiresRestart == true)
                MessageBox.Show(
                    "The mode change has been saved.\n\n" +
                    "Restart the NutAgent service for it to take effect.",
                    "Restart Required", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch
        {
            MessageBox.Show("Failed to save configuration. Is the NutAgent service running?",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SaveBtn.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private bool TryBuildConfig(out TrayConfig? config)
    {
        config = null;

        if (!int.TryParse(PortBox.Text.Trim(),          out int port)    ||
            !int.TryParse(RemotePortBox.Text.Trim(),     out int rport)   ||
            !int.TryParse(ChargeThreshBox.Text.Trim(),   out int charge)  ||
            !int.TryParse(RuntimeThreshBox.Text.Trim(),  out int runtime) ||
            !int.TryParse(ShutdownDelayBox.Text.Trim(),  out int delay))
        {
            MessageBox.Show("Please enter valid whole numbers in all numeric fields.",
                "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        config = new TrayConfig
        {
            Mode                     = ServerRadio.IsChecked == true ? "Server" : "Client",
            UpsName                  = UpsNameBox.Text.Trim(),
            UpsDescription           = _original?.UpsDescription   ?? "UPS",
            Port                     = port,
            RemoteHost               = RemoteHostBox.Text.Trim(),
            RemotePort               = rport,
            RemoteUpsName            = RemoteUpsBox.Text.Trim(),
            RemoteUsername           = _original?.RemoteUsername    ?? "",
            RemotePassword           = _original?.RemotePassword    ?? "",
            ShutdownBatteryThreshold = charge,
            ShutdownRuntimeMinutes   = runtime,
            ShutdownDelaySeconds     = delay,
            PollIntervalSeconds      = _original?.PollIntervalSeconds ?? 30,
        };
        return true;
    }
}

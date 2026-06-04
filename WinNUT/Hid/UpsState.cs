namespace WinNUT.Hid;

public class UpsState
{
    public double BatteryCharge { get; set; } = 100;
    public double BatteryVoltage { get; set; }
    public double BatteryVoltageNominal { get; set; }
    public int RuntimeSeconds { get; set; }
    public double InputVoltage { get; set; }
    public double InputVoltageNominal { get; set; }
    public double OutputVoltage { get; set; }
    public double Load { get; set; }
    public UpsStatus Status { get; set; } = UpsStatus.OnLine;
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public int VendorId { get; set; }
    public int ProductId { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

[Flags]
public enum UpsStatus
{
    OnLine = 1,
    OnBattery = 2,
    LowBattery = 4,
    Charging = 8,
    Discharging = 16,
    ReplaceBattery = 32,
}

public static class UpsStatusExtensions
{
    public static string ToNutString(this UpsStatus status)
    {
        var parts = new List<string>();
        if (status.HasFlag(UpsStatus.OnLine)) parts.Add("OL");
        if (status.HasFlag(UpsStatus.OnBattery)) parts.Add("OB");
        if (status.HasFlag(UpsStatus.LowBattery)) parts.Add("LB");
        if (status.HasFlag(UpsStatus.Charging)) parts.Add("CHRG");
        if (status.HasFlag(UpsStatus.Discharging)) parts.Add("DISCHRG");
        if (status.HasFlag(UpsStatus.ReplaceBattery)) parts.Add("RB");
        return parts.Count > 0 ? string.Join(" ", parts) : "OL";
    }

    public static bool IsOnBattery(this UpsStatus status) =>
        status.HasFlag(UpsStatus.OnBattery);

    public static bool IsLowBattery(this UpsStatus status) =>
        status.HasFlag(UpsStatus.LowBattery);
}

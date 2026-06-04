namespace NutAgent.Hid;

public class UpsState
{
    // Typed fields used by shutdown decision logic
    public double    BatteryCharge  { get; set; } = 100;
    public int       RuntimeSeconds { get; set; }
    public UpsStatus Status         { get; set; } = UpsStatus.OnLine;

    // Identity — populated once at device connect from USB string descriptors
    public string Manufacturer { get; set; } = "";
    public string Model        { get; set; } = "";
    public string Serial       { get; set; } = "";
    public int    VendorId     { get; set; }
    public int    ProductId    { get; set; }

    // All NUT variables discovered dynamically from HID reports — drives the NUT server output
    public Dictionary<string, string> Variables { get; set; } = new();

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public UpsState Clone()
    {
        var clone = (UpsState)MemberwiseClone();
        clone.Variables = new Dictionary<string, string>(Variables);
        return clone;
    }
}

[Flags]
public enum UpsStatus
{
    OnLine            = 1 << 0,
    OnBattery         = 1 << 1,
    LowBattery        = 1 << 2,
    Charging          = 1 << 3,
    Discharging       = 1 << 4,
    ReplaceBattery    = 1 << 5,
    ShutdownImminent  = 1 << 6,
    Overload          = 1 << 7,
    Boosting          = 1 << 8,  // UPS boosting low input voltage
    Trimming          = 1 << 9,  // UPS trimming high input voltage
}

public static class UpsStatusExtensions
{
    public static string ToNutString(this UpsStatus status)
    {
        var parts = new List<string>();
        if (status.HasFlag(UpsStatus.OnLine))           parts.Add("OL");
        if (status.HasFlag(UpsStatus.OnBattery))        parts.Add("OB");
        if (status.HasFlag(UpsStatus.LowBattery))       parts.Add("LB");
        if (status.HasFlag(UpsStatus.Charging))         parts.Add("CHRG");
        if (status.HasFlag(UpsStatus.Discharging))      parts.Add("DISCHRG");
        if (status.HasFlag(UpsStatus.ReplaceBattery))   parts.Add("RB");
        if (status.HasFlag(UpsStatus.ShutdownImminent)) parts.Add("SD");
        if (status.HasFlag(UpsStatus.Overload))         parts.Add("OVER");
        if (status.HasFlag(UpsStatus.Boosting))         parts.Add("BOOST");
        if (status.HasFlag(UpsStatus.Trimming))         parts.Add("TRIM");
        return parts.Count > 0 ? string.Join(" ", parts) : "OL";
    }

    public static bool IsOnBattery(this UpsStatus status)  => status.HasFlag(UpsStatus.OnBattery);
    public static bool IsLowBattery(this UpsStatus status) => status.HasFlag(UpsStatus.LowBattery);
}

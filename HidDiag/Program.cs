using System;
using System.Linq;
using System.Threading;
using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Input;

var device = DeviceList.Local.GetHidDevices()
    .FirstOrDefault(d =>
    {
        try { return d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => (u >> 16) == 0x84 || (u >> 16) == 0x85)); }
        catch { return false; }
    });

if (device == null) { Console.WriteLine("No HID UPS device found."); return; }

Console.WriteLine($"Device: {device.GetFriendlyName()}  VID={device.VendorID:X4} PID={device.ProductID:X4}");
Console.WriteLine($"Manufacturer: {TryGet(() => device.GetManufacturer())}");
Console.WriteLine($"Product:      {TryGet(() => device.GetProductName())}");
Console.WriteLine($"Serial:       {TryGet(() => device.GetSerialNumber())}");
Console.WriteLine();

var descriptor = device.GetReportDescriptor();
var items = descriptor.DeviceItems.ToArray();

// --- Descriptor dump ---
Console.WriteLine("=== REPORT DESCRIPTOR USAGES ===");
foreach (var item in items)
{
    Console.WriteLine($"  DeviceItem usages: {string.Join(", ", item.Usages.GetAllValues().Select(u => $"0x{u:X8}"))}");
    foreach (var report in item.InputReports.Concat<Report>(item.FeatureReports))
    {
        string kind = item.InputReports.Contains(report) ? "INPUT " : "FEATURE";
        Console.WriteLine($"    [{kind} ID={report.ReportID}]");
        foreach (var field in report.DataItems)
        {
            string usages = string.Join(", ", field.Usages.GetAllValues().Select(u => $"0x{u:X8}"));
            Console.WriteLine($"      usages={usages}  logical=[{field.LogicalMinimum}..{field.LogicalMaximum}]  physical=[{field.PhysicalMinimum}..{field.PhysicalMaximum}]  unitExp={field.UnitExponent}  bits={field.ElementBits}");
        }
    }
}

Console.WriteLine();

// --- Feature report dump ---
Console.WriteLine("=== FEATURE REPORTS (raw values) ===");
using var stream = device.Open();
stream.ReadTimeout = 500;
int maxLen = device.GetMaxFeatureReportLength();
var buf = new byte[maxLen];

foreach (var item in items)
{
    foreach (var report in item.FeatureReports)
    {
        Array.Clear(buf, 0, buf.Length);
        buf[0] = report.ReportID;
        try
        {
            stream.GetFeature(buf);
            Console.WriteLine($"  Feature ID={report.ReportID}: {BitConverter.ToString(buf, 0, Math.Min(buf.Length, 16))}");
            report.Read(buf, 0, (DataValue v) =>
            {
                foreach (uint u in v.Usages.Select(x => (uint)x))
                    Console.WriteLine($"    usage=0x{u:X8}  logical={v.GetLogicalValue()}  physical={v.GetPhysicalValue():F4}");
            });
        }
        catch (Exception ex) { Console.WriteLine($"  Feature ID={report.ReportID}: ERROR {ex.Message}"); }
    }
}

Console.WriteLine();

// --- Input report live capture ---
Console.WriteLine("=== INPUT REPORTS (5 seconds) ===");
var receiver = descriptor.CreateHidDeviceInputReceiver();
var parsers = items.Select(i => i.CreateDeviceItemInputParser()).ToArray();
var inputBuf = new byte[device.GetMaxInputReportLength()];
receiver.Start(stream);
var deadline = DateTime.UtcNow.AddSeconds(5);
int reportCount = 0;
while (DateTime.UtcNow < deadline)
{
    Report report;
    while (receiver.TryRead(inputBuf, 0, out report))
    {
        reportCount++;
        Console.WriteLine($"  Input ID={report.ReportID}: {BitConverter.ToString(inputBuf, 0, Math.Min(inputBuf.Length, 16))}");
        for (int i = 0; i < parsers.Length; i++)
        {
            if (!parsers[i].TryParseReport(inputBuf, 0, report)) continue;
            for (int j = 0; j < parsers[i].ValueCount; j++)
            {
                var v = parsers[i].GetValue(j);
                foreach (uint u in v.Usages.Select(x => (uint)x))
                    Console.WriteLine($"    usage=0x{u:X8}  logical={v.GetLogicalValue()}  physical={v.GetPhysicalValue():F4}");
            }
        }
    }
    Thread.Sleep(100);
}
Console.WriteLine($"  ({reportCount} input reports received in 5s)");

static string TryGet(Func<string> f) { try { return f(); } catch { return "(unavailable)"; } }

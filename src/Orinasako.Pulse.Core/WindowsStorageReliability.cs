using System.Management;
using System.Runtime.Versioning;

namespace Orinasako.Pulse.Core;

public sealed record ReliabilityCounters(
    int? TemperatureC,
    int? TemperatureMaxC,
    int? WearPercent,
    ushort? PowerOnHours,
    uint? StartStopCycles,
    ulong? ReadErrorsTotal,
    ulong? ReadErrorsUncorrected,
    ulong? WriteErrorsTotal,
    ulong? WriteErrorsUncorrected,
    string? HealthStatus);

/// <summary>Read-only Windows Storage WMI supplement. Missing provider data remains absent.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsStorageReliability
{
    public static IReadOnlyDictionary<int, ReliabilityCounters> ReadByDiskNumber()
    {
        if (!OperatingSystem.IsWindows()) return new Dictionary<int, ReliabilityCounters>();
        var result = new Dictionary<int, ReliabilityCounters>();
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\Microsoft\\Windows\\Storage",
                "SELECT DeviceId, Temperature, TemperatureMax, Wear, PowerOnHours, StartStopCycleCount, " +
                "ReadErrorsTotal, ReadErrorsUncorrected, WriteErrorsTotal, WriteErrorsUncorrected FROM MSFT_StorageReliabilityCounter");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (!int.TryParse(Convert.ToString(item["DeviceId"], System.Globalization.CultureInfo.InvariantCulture), out var number) || number < 0) continue;
                    result[number] = new ReliabilityCounters(
                        Byte(item, "Temperature"), Byte(item, "TemperatureMax"), Byte(item, "Wear"),
                        UShort(item, "PowerOnHours"), UInt(item, "StartStopCycleCount"),
                        ULong(item, "ReadErrorsTotal"), ULong(item, "ReadErrorsUncorrected"),
                        ULong(item, "WriteErrorsTotal"), ULong(item, "WriteErrorsUncorrected"), null);
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        catch (InvalidOperationException) { }

        try
        {
            using var searcher = new ManagementObjectSearcher("root\\Microsoft\\Windows\\Storage",
                "SELECT Number, HealthStatus FROM MSFT_Disk");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (!int.TryParse(Convert.ToString(item["Number"], System.Globalization.CultureInfo.InvariantCulture), out var number) || number < 0) continue;
                    var value = Convert.ToInt32(item["HealthStatus"], System.Globalization.CultureInfo.InvariantCulture);
                    if (result.TryGetValue(number, out var counters)) result[number] = counters with { HealthStatus = Health(value) };
                    else result[number] = new ReliabilityCounters(null, null, null, null, null, null, null, null, null, Health(value));
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        catch (InvalidOperationException) { }
        return result;
    }

    private static int? Byte(ManagementObject value, string name) =>
        byte.TryParse(Text(value, name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static ushort? UShort(ManagementObject value, string name) =>
        ushort.TryParse(Text(value, name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static uint? UInt(ManagementObject value, string name) =>
        uint.TryParse(Text(value, name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static ulong? ULong(ManagementObject value, string name) =>
        ulong.TryParse(Text(value, name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static string? Text(ManagementObject value, string name) =>
        Convert.ToString(value[name], System.Globalization.CultureInfo.InvariantCulture);

    private static string? Health(int value) => value switch { 0 => "healthy", 1 => "warning", 2 => "critical", _ => null };
}

using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text.Json;
using StorageAgent.Contracts;

namespace StorageAgent.Collectors;

public interface ISystemCollector
{
    SystemCategory Category { get; }
    TimeSpan DefaultInterval { get; }
    SystemSection Collect(CancellationToken token);
}

// Only fixed, allowlisted WMI queries and scalar fields. No arbitrary WQL or remote commands.
internal sealed record Field(string Property, string Name, string? Unit = null, bool KelvinTenths = false);
internal sealed record Probe(string Name, string Namespace, string Class, Field[] Fields, string? Where = null);
internal sealed class WmiCollector(SystemCategory category, int seconds, params Probe[] probes) : ISystemCollector
{
    public SystemCategory Category => category;
    public TimeSpan DefaultInterval => TimeSpan.FromSeconds(seconds);

    public SystemSection Collect(CancellationToken token)
    {
        var output = new List<SystemProbe>();
        foreach (var probe in probes)
        {
            token.ThrowIfCancellationRequested();
            output.Add(OperatingSystem.IsWindows() ? Query(probe, token) :
                new SystemProbe(probe.Name, probe.Class, MetricStatus.Unsupported, [], "Windows required."));
        }
        return new(Category, DateTimeOffset.UtcNow, output);
    }

    [SupportedOSPlatform("windows")]
    private static SystemProbe Query(Probe probe, CancellationToken token)
    {
        var source = probe.Namespace + ":" + probe.Class;
        try
        {
            var query = "SELECT " + string.Join(",", probe.Fields.Select(f => f.Property).Distinct()) +
                " FROM " + probe.Class + (probe.Where is null ? "" : " WHERE " + probe.Where);
            using var searcher = new ManagementObjectSearcher(new ManagementScope(probe.Namespace),
                new ObjectQuery(query), new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(5) });
            using var results = searcher.Get();
            var rows = new List<IReadOnlyList<SystemMetric>>();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    token.ThrowIfCancellationRequested();
                    if (rows.Count >= 32) return new(probe.Name, source, MetricStatus.Available, rows, "Limited to 32 instances.");
                    var values = new List<SystemMetric>();
                    foreach (var field in probe.Fields)
                    {
                        Metric<JsonElement> metric;
                        try
                        {
                            var value = item[field.Property];
                            if (value is null) metric = Metric<JsonElement>.Unavailable(source);
                            else if (field.KelvinTenths)
                            {
                                var celsius = Convert.ToDouble(value, CultureInfo.InvariantCulture) / 10 - 273.15;
                                metric = celsius is >= -50 and <= 200
                                    ? Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(Math.Round(celsius, 1)), source)
                                    : Metric<JsonElement>.Unavailable(source, "Implausible sensor value.");
                            }
                            else if (value is string str)
                                metric = string.IsNullOrWhiteSpace(str) ? Metric<JsonElement>.Unavailable(source) :
                                    Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(str[..Math.Min(160, str.Length)]), source);
                            else if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double)
                                metric = Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(value), source);
                            else metric = Metric<JsonElement>.Unsupported(source, "Non-scalar value.");
                        }
                        catch (Exception error) when (error is ManagementException or InvalidCastException or FormatException or OverflowException or ArgumentException)
                        { metric = Metric<JsonElement>.Unavailable(source); }
                        values.Add(new(field.Name, field.Unit, metric));
                    }
                    rows.Add(values);
                }
            }
            return new(probe.Name, source, rows.Count == 0 ? MetricStatus.Unavailable : MetricStatus.Available, rows);
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { return new(probe.Name, source, MetricStatus.Unavailable, [], "Provider or class unavailable."); }
    }
}

public static class WindowsSystemCollectors
{
    private const string Cim = @"root\cimv2";
    private static Field F(string property, string name, string? unit = null) => new(property, name, unit);
    private static Probe P(string name, string cls, params Field[] fields) => new(name, Cim, cls, fields);
    public static IReadOnlyList<ISystemCollector> Create() =>
    [
        new WmiCollector(SystemCategory.Cpu, 5,
            P("processors", "Win32_Processor", F("Name","model"), F("Manufacturer","manufacturer"), F("Architecture","architectureCode"), F("NumberOfCores","cores"), F("NumberOfLogicalProcessors","threads"), F("CurrentClockSpeed","clock","MHz"), F("MaxClockSpeed","maxClock","MHz"), F("L2CacheSize","l2Cache","KiB"), F("L3CacheSize","l3Cache","KiB")),
            new("utilization", Cim, "Win32_PerfFormattedData_PerfOS_Processor", [F("PercentProcessorTime","usage","percent")], "Name='_Total'")),
        new WmiCollector(SystemCategory.Memory, 5,
            P("memory", "Win32_OperatingSystem", F("TotalVisibleMemorySize","total","KiB"), F("FreePhysicalMemory","free","KiB")),
            P("modules", "Win32_PhysicalMemory", F("Capacity","capacity","bytes"), F("Manufacturer","manufacturer"), F("Speed","speed","MT/s"), F("ConfiguredClockSpeed","configuredSpeed","MT/s"), F("SMBIOSMemoryType","typeCode"))),
        new WmiCollector(SystemCategory.Gpu, 5,
            P("adapters", "Win32_VideoController", F("Name","model"), F("AdapterCompatibility","manufacturer"), F("DriverVersion","driverVersion"), F("AdapterRAM","legacyReportedAdapterRam","bytes")),
            P("engines", "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine", F("UtilizationPercentage","usage","percent"))),
        new WmiCollector(SystemCategory.Motherboard, 300,
            P("boards", "Win32_BaseBoard", F("Manufacturer","manufacturer"), F("Product","model"), F("Version","version")),
            P("bios", "Win32_BIOS", F("Manufacturer","manufacturer"), F("SMBIOSBIOSVersion","version"))),
        new WmiCollector(SystemCategory.Network, 5,
            P("adapters", "Win32_NetworkAdapter", F("AdapterTypeID","typeCode"), F("Speed","linkSpeed","bit/s"), F("NetConnectionStatus","connectionStatusCode")),
            P("traffic", "Win32_PerfFormattedData_Tcpip_NetworkInterface", F("BytesReceivedPersec","received","bytes/s"), F("BytesSentPersec","sent","bytes/s"), F("PacketsReceivedErrors","receiveErrors"), F("PacketsOutboundErrors","sendErrors"))),
        new WmiCollector(SystemCategory.Power, 30,
            P("batteries", "Win32_Battery", F("EstimatedChargeRemaining","charge","percent"), F("EstimatedRunTime","estimatedRuntime","minutes"), F("BatteryStatus","statusCode"), F("DesignCapacity","designCapacity","mWh"), F("FullChargeCapacity","fullChargeCapacity","mWh")),
            new("cycles", @"root\wmi", "BatteryCycleCount", [F("CycleCount","cycles")])),
        new WmiCollector(SystemCategory.Thermal, 30,
            new("acpiZones", @"root\wmi", "MSAcpi_ThermalZoneTemperature", [new("CurrentTemperature","temperature","C",true), new("CriticalTripPoint","criticalTripPoint","C",true)]),
            P("fans", "Win32_Fan", F("DesiredSpeed","desiredSpeed","rpm"), F("VariableSpeed","variableSpeed"))),
        new WmiCollector(SystemCategory.OperatingSystem, 60,
            P("windows", "Win32_OperatingSystem", F("Caption","edition"), F("Version","version"), F("BuildNumber","build"), F("OSArchitecture","architecture"), F("NumberOfProcesses","processCount")),
            P("uptime", "Win32_PerfFormattedData_PerfOS_System", F("SystemUpTime","uptime","seconds"))),
        new WmiCollector(SystemCategory.Processes, 60,
            P("totals", "Win32_PerfFormattedData_PerfOS_System", F("Processes","processes"), F("Threads","threads"), F("ProcessorQueueLength","processorQueueLength")),
            P("services", "Win32_Service", F("State","state"), F("StartMode","startMode"))),
        new WmiCollector(SystemCategory.Peripherals, 300,
            P("devices", "Win32_PnPEntity", F("PNPClass","deviceClass"), F("Status","status"), F("ConfigManagerErrorCode","errorCode"))),
        new WmiCollector(SystemCategory.Security, 300,
            new("antivirus", @"root\SecurityCenter2", "AntiVirusProduct", [F("productState","providerStateCode")]),
            new("firewall", @"root\StandardCimv2", "MSFT_NetFirewallProfile", [F("Enabled","enabled")]),
            new("bitlocker", @"root\CIMV2\Security\MicrosoftVolumeEncryption", "Win32_EncryptableVolume", [F("ProtectionStatus","protectionStatusCode")]),
            new("tpm", @"root\CIMV2\Security\MicrosoftTpm", "Win32_Tpm", [F("IsEnabled_InitialValue","enabled"), F("IsActivated_InitialValue","activated")]))
    ];
}

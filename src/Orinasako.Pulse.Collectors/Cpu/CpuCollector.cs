using Orinasako.Pulse.Contracts;
using static Orinasako.Pulse.Collectors.ProbeDefinitions;

namespace Orinasako.Pulse.Collectors;

internal static class CpuCollectorFactory
{
    public static ISystemCollector Create() => new WmiCollector(SystemCategory.Cpu, 5,
            P("processors", "Win32_Processor", F("Name","model"), F("Manufacturer","manufacturer"), F("Architecture","architectureCode"), F("NumberOfCores","cores"), F("NumberOfLogicalProcessors","threads"), F("CurrentClockSpeed","clock","MHz"), F("MaxClockSpeed","maxClock","MHz"), F("L2CacheSize","l2Cache","KiB"), F("L3CacheSize","l3Cache","KiB")),
            new("utilization", Cim, "Win32_PerfFormattedData_PerfOS_Processor", [F("PercentProcessorTime","usage","percent")], "Name='_Total'"),
            new("perLogicalProcessor", Cim, "Win32_PerfFormattedData_PerfOS_Processor", [F("PercentProcessorTime","usage","percent")], "Name<>'_Total'"));
}

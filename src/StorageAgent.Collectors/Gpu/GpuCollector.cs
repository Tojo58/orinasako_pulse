using StorageAgent.Contracts;
using static StorageAgent.Collectors.ProbeDefinitions;

namespace StorageAgent.Collectors;

internal static class GpuCollectorFactory
{
    public static ISystemCollector Create() => new WmiCollector(SystemCategory.Gpu, 5,
            P("adapters", "Win32_VideoController", F("Name","model"), F("AdapterCompatibility","manufacturer"), F("DriverVersion","driverVersion"), F("AdapterRAM","legacyReportedAdapterRam","bytes")),
            P("engines", "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine", F("UtilizationPercentage","usage","percent")));
}

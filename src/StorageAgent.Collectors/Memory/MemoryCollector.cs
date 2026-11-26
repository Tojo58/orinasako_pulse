using StorageAgent.Contracts;
using static StorageAgent.Collectors.ProbeDefinitions;

namespace StorageAgent.Collectors;

internal static class MemoryCollectorFactory
{
    public static ISystemCollector Create() => new WmiCollector(SystemCategory.Memory, 5,
            P("memory", "Win32_OperatingSystem", F("TotalVisibleMemorySize","total","KiB"), F("FreePhysicalMemory","free","KiB")),
            Inventory("modules", "Win32_PhysicalMemory", F("Capacity","capacity","bytes"), F("Manufacturer","manufacturer"), F("Speed","speed","MT/s"), F("ConfiguredClockSpeed","configuredSpeed","MT/s"), F("SMBIOSMemoryType","typeCode")));
}

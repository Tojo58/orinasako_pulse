using StorageAgent.Contracts;
using static StorageAgent.Collectors.ProbeDefinitions;

namespace StorageAgent.Collectors;

internal static class PowerCollectorFactory
{
    public static ISystemCollector Create() => new WmiCollector(SystemCategory.Power, 30,
            P("batteries", "Win32_Battery", F("EstimatedChargeRemaining","charge","percent"), F("EstimatedRunTime","estimatedRuntime","minutes"), F("BatteryStatus","statusCode"), F("DesignCapacity","designCapacity","mWh"), F("FullChargeCapacity","fullChargeCapacity","mWh")),
            new("cycles", @"root\wmi", "BatteryCycleCount", [F("CycleCount","cycles")]));
}

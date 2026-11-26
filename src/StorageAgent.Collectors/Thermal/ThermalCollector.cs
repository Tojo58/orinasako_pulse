using StorageAgent.Contracts;
using static StorageAgent.Collectors.ProbeDefinitions;

namespace StorageAgent.Collectors;

internal static class ThermalCollectorFactory
{
    public static ISystemCollector Create() => new WmiCollector(SystemCategory.Thermal, 30,
            new("acpiZones", @"root\wmi", "MSAcpi_ThermalZoneTemperature", [new("CurrentTemperature","temperature","C",true), new("CriticalTripPoint","criticalTripPoint","C",true)]),
            P("fans", "Win32_Fan", F("DesiredSpeed","desiredSpeed","rpm"), F("VariableSpeed","variableSpeed")));
}

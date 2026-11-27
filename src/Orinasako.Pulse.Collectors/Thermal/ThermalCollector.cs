using Orinasako.Pulse.Contracts;
using static Orinasako.Pulse.Collectors.ProbeDefinitions;

namespace Orinasako.Pulse.Collectors;

internal static class ThermalCollectorFactory
{
    public static ISystemCollector Create() => new WmiCollector(SystemCategory.Thermal, 30,
            new("acpiZones", @"root\wmi", "MSAcpi_ThermalZoneTemperature", [new("CurrentTemperature","temperature","C",true), new("CriticalTripPoint","criticalTripPoint","C",true)]),
            P("fans", "Win32_Fan", F("DesiredSpeed","desiredSpeed","rpm"), F("VariableSpeed","variableSpeed")));
}

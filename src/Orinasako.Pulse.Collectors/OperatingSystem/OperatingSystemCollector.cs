using Orinasako.Pulse.Contracts;
using static Orinasako.Pulse.Collectors.ProbeDefinitions;

namespace Orinasako.Pulse.Collectors;

internal static class OperatingSystemCollectorFactory
{
    public static ISystemCollector Create() => new WmiCollector(SystemCategory.OperatingSystem, 60,
            P("windows", "Win32_OperatingSystem", F("Caption","edition"), F("Version","version"), F("BuildNumber","build"), F("OSArchitecture","architecture"), F("NumberOfProcesses","processCount")),
            P("uptime", "Win32_PerfFormattedData_PerfOS_System", F("SystemUpTime","uptime","seconds")));
}

using Orinasako.Pulse.Contracts;
using static Orinasako.Pulse.Collectors.ProbeDefinitions;

namespace Orinasako.Pulse.Collectors;

internal static class ProcessesCollectorFactory
{
    public static ISystemCollector Create() => new WmiCollector(SystemCategory.Processes, 60,
            P("totals", "Win32_PerfFormattedData_PerfOS_System", F("Processes","processes"), F("Threads","threads"), F("ProcessorQueueLength","processorQueueLength")),
            P("services", "Win32_Service", F("State","state"), F("StartMode","startMode")));
}

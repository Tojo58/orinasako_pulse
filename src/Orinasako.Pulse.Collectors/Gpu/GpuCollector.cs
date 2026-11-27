using Orinasako.Pulse.Contracts;
using static Orinasako.Pulse.Collectors.ProbeDefinitions;

namespace Orinasako.Pulse.Collectors;

internal static class GpuCollectorFactory
{
    public static ISystemCollector Create() => new GpuCollector(new WmiCollector(SystemCategory.Gpu, 5,
        Inventory("adapters", "Win32_VideoController", F("Name","model"), F("AdapterCompatibility","manufacturer"), F("DriverVersion","driverVersion"))));
}

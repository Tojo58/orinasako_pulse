using Orinasako.Pulse.Contracts;
using static Orinasako.Pulse.Collectors.ProbeDefinitions;

namespace Orinasako.Pulse.Collectors;

internal static class MotherboardCollectorFactory
{
    public static ISystemCollector Create() => new WmiCollector(SystemCategory.Motherboard, 300,
            Inventory("boards", "Win32_BaseBoard", F("Manufacturer","manufacturer"), F("Product","model"), F("Version","version")),
            Inventory("bios", "Win32_BIOS", F("Manufacturer","manufacturer"), F("SMBIOSBIOSVersion","version")));
}

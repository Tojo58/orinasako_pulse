using Orinasako.Pulse.Contracts;
using static Orinasako.Pulse.Collectors.ProbeDefinitions;

namespace Orinasako.Pulse.Collectors;

internal static class PeripheralsCollectorFactory
{
    public static ISystemCollector Create() => new PrinterQueueCollector(new WmiCollector(SystemCategory.Peripherals, 30,
        Inventory("devices", "Win32_PnPEntity", F("PNPClass","deviceClass"), F("Status","status"), F("ConfigManagerErrorCode","errorCode"))));
}

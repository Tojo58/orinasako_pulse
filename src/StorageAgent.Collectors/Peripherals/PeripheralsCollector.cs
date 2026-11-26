using StorageAgent.Contracts;
using static StorageAgent.Collectors.ProbeDefinitions;

namespace StorageAgent.Collectors;

internal static class PeripheralsCollectorFactory
{
    public static ISystemCollector Create() => new PrinterQueueCollector(new WmiCollector(SystemCategory.Peripherals, 30,
        Inventory("devices", "Win32_PnPEntity", F("PNPClass","deviceClass"), F("Status","status"), F("ConfigManagerErrorCode","errorCode"))));
}

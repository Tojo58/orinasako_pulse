namespace Orinasako.Pulse.Collectors;

public static class WindowsSystemCollectors
{
    public static IReadOnlyList<ISystemCollector> Create() =>
    [
        CpuCollectorFactory.Create(),
        MemoryCollectorFactory.Create(),
        GpuCollectorFactory.Create(),
        MotherboardCollectorFactory.Create(),
        NetworkCollectorFactory.Create(),
        PowerCollectorFactory.Create(),
        ThermalCollectorFactory.Create(),
        OperatingSystemCollectorFactory.Create(),
        ProcessesCollectorFactory.Create(),
        PeripheralsCollectorFactory.Create(),
        SecurityCollectorFactory.Create()
    ];
}

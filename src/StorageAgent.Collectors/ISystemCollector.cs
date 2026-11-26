using StorageAgent.Contracts;

namespace StorageAgent.Collectors;

public interface ISystemCollector
{
    SystemCategory Category { get; }
    TimeSpan DefaultInterval { get; }
    SystemSection Collect(CancellationToken token);
}

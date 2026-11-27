using Orinasako.Pulse.Contracts;

namespace Orinasako.Pulse.Collectors;

public interface ISystemCollector
{
    SystemCategory Category { get; }
    TimeSpan DefaultInterval { get; }
    SystemSection Collect(CancellationToken token);
}

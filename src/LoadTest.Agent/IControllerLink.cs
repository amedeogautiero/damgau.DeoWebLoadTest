namespace LoadTest.Agent;

using LoadTest.Protocol;

/// <summary>Lo stream bidirezionale verso il controller. Astratto perché i test possano usare un'implementazione in memoria.</summary>
public interface IControllerLink : IAsyncDisposable
{
    IAsyncEnumerable<ControllerMsg> ReadAllAsync(CancellationToken ct);
    /// <summary>Scritture NON concorrenti: le serializza AgentSession con un solo writer.</summary>
    Task WriteAsync(AgentMsg message, CancellationToken ct);
    Task CompleteWritesAsync();
}

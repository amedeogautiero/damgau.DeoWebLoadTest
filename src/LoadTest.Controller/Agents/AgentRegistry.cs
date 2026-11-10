using System.Collections.Concurrent;

namespace LoadTest.Controller.Agents;

public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentConnection> _agents = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Una nuova connessione con lo stesso agent_id sostituisce la vecchia (riconnessione dopo un guasto di rete).</summary>
    public void AddOrReplace(AgentConnection conn)
    {
        AgentConnection? old;
        lock (_gate)
        {
            _agents.TryGetValue(conn.AgentId, out old);
            _agents[conn.AgentId] = conn;
        }
        old?.Close();
    }

    public void Remove(AgentConnection conn) =>
        _agents.TryRemove(new KeyValuePair<string, AgentConnection>(conn.AgentId, conn));

    public IReadOnlyList<AgentConnection> Snapshot() => _agents.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.AgentId, StringComparer.Ordinal).ToList();
}

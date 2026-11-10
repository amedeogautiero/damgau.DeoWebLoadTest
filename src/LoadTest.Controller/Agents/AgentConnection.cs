using System.Threading.Channels;
using Grpc.Core;
using LoadTest.Protocol;

namespace LoadTest.Controller.Agents;

/// <summary>Un agente connesso. Coda di invio limitata (contropressione sui trasferimenti), un solo writer verso lo stream.</summary>
public sealed class AgentConnection
{
    private readonly Channel<ControllerMsg> _out = Channel.CreateBounded<ControllerMsg>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Action<AgentMsg>? _handler;
    private string? _reservedBy;
    private double _cpu, _maxCpu;
    private int _active;
    private DateTimeOffset? _lastHeartbeat;

    public AgentConnection(Hello hello) { Hello = hello; }

    public Hello Hello { get; }
    public string AgentId => Hello.AgentId;
    public string Name => Hello.Name;
    public Task Closed => _closed.Task;

    public string? ReservedBy { get { lock (_gate) return _reservedBy; } }
    public double CpuPercent { get { lock (_gate) return _cpu; } }
    public int ActiveUsers { get { lock (_gate) return _active; } }
    public DateTimeOffset? LastHeartbeatUtc { get { lock (_gate) return _lastHeartbeat; } }

    public bool TryReserve(string runId)
    {
        lock (_gate)
        {
            if (_reservedBy is not null || _closed.Task.IsCompleted) return false;
            _reservedBy = runId;
            return true;
        }
    }

    public void Release(string runId)
    {
        lock (_gate) if (_reservedBy == runId) _reservedBy = null;
    }

    public void Attach(Action<AgentMsg> handler) { lock (_gate) _handler = handler; }
    public void Detach() { lock (_gate) _handler = null; }

    /// <summary>Massimo della CPU riportata dagli Heartbeat dall'ultima chiamata (per marcare l'agente saturo).</summary>
    public double TakeMaxCpu()
    {
        lock (_gate)
        {
            var v = _maxCpu;
            _maxCpu = _cpu;
            return v;
        }
    }

    /// <returns>false se la connessione è chiusa.</returns>
    public async ValueTask<bool> SendAsync(ControllerMsg message, CancellationToken ct = default)
    {
        try
        {
            await _out.Writer.WriteAsync(message, ct);
            return true;
        }
        catch (ChannelClosedException) { return false; }
    }

    internal void Dispatch(AgentMsg m)
    {
        Action<AgentMsg>? handler;
        lock (_gate)
        {
            if (m.MCase == AgentMsg.MOneofCase.Hb)
            {
                _cpu = m.Hb.CpuPercent;
                _maxCpu = Math.Max(_maxCpu, m.Hb.CpuPercent);
                _active = m.Hb.ActiveUsers;
                _lastHeartbeat = DateTimeOffset.UtcNow;
            }
            handler = _handler;
        }
        handler?.Invoke(m);
    }

    internal async Task PumpAsync(IServerStreamWriter<ControllerMsg> writer, CancellationToken ct)
    {
        await foreach (var m in _out.Reader.ReadAllAsync(ct)) await writer.WriteAsync(m);
    }

    internal void Close()
    {
        _out.Writer.TryComplete();
        _closed.TrySetResult();
    }
}

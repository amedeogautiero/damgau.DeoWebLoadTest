using LoadTest.Sdk;

namespace LoadTest.Engine.LoadTesting;

/// <summary>Implementazione di ILoadTestEvents. Un handler che lancia eccezione non ferma gli altri né la run: l'errore va a OnHandlerError.</summary>
public sealed class LoadTestEvents : ILoadTestEvents
{
    public event Action<LoadTestInfo> LoadTestStarting = delegate { };
    public event Action<LoadTestInfo> LoadTestFinished = delegate { };
    public event Action WarmupComplete = delegate { };
    public event Action<TestEventArgs> TestStarting = delegate { };
    public event Action<TestEventArgs> TestFinished = delegate { };
    public event Action<TestSelectedArgs> TestSelected = delegate { };
    public event Action<HeartbeatArgs> Heartbeat = delegate { };
    public event Action<ThresholdArgs> ThresholdExceeded = delegate { };
    public event Action<string> Aborted = delegate { };

    public Action<Exception>? OnHandlerError { get; set; }

    internal void RaiseLoadTestStarting(LoadTestInfo a) => Fire(LoadTestStarting, a);
    internal void RaiseLoadTestFinished(LoadTestInfo a) => Fire(LoadTestFinished, a);
    internal void RaiseWarmupComplete() => Fire(WarmupComplete);
    internal void RaiseTestStarting(TestEventArgs a) => Fire(TestStarting, a);
    internal void RaiseTestFinished(TestEventArgs a) => Fire(TestFinished, a);
    internal void RaiseTestSelected(TestSelectedArgs a) => Fire(TestSelected, a);
    internal void RaiseHeartbeat(HeartbeatArgs a) => Fire(Heartbeat, a);
    /// <summary>Per la chat Monitoraggio (soglie): la solleva chi valuta le soglie.</summary>
    public void RaiseThresholdExceeded(ThresholdArgs a) => Fire(ThresholdExceeded, a);
    internal void RaiseAborted(string reason) => Fire(Aborted, reason);

    private void Fire<T>(Action<T> ev, T arg)
    {
        foreach (var d in ev.GetInvocationList())
            try { ((Action<T>)d)(arg); } catch (Exception ex) { OnHandlerError?.Invoke(ex); }
    }

    private void Fire(Action ev)
    {
        foreach (var d in ev.GetInvocationList())
            try { ((Action)d)(); } catch (Exception ex) { OnHandlerError?.Invoke(ex); }
    }
}

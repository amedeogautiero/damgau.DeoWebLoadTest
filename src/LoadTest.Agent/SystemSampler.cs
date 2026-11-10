using System.Diagnostics;

namespace LoadTest.Agent;

/// <summary>
/// CPU% e RAM per l'Heartbeat. Linux: CPU dell'intera macchina da /proc/stat. Altri sistemi: CPU del solo processo agente
/// (APPROSSIMAZIONE: la CPU di macchina su Windows/macOS richiede API native, da definire con la chat Monitoraggio).
/// RAM = working set del processo agente.
/// </summary>
public sealed class SystemSampler
{
    private (long Busy, long Total)? _lastStat;
    private TimeSpan _lastCpu;
    private DateTime _lastWall;
    private bool _procInit;

    public (double CpuPercent, long RamUsedMb) Sample()
    {
        using var proc = Process.GetCurrentProcess();
        var ram = proc.WorkingSet64 / (1024 * 1024);

        if (OperatingSystem.IsLinux() && TryReadLinuxStat(out var cur))
        {
            var cpu = _lastStat is { } p && cur.Total > p.Total ? 100.0 * (cur.Busy - p.Busy) / (cur.Total - p.Total) : 0;
            _lastStat = cur;
            return (Clamp(cpu), ram);
        }

        var now = DateTime.UtcNow;
        var used = proc.TotalProcessorTime;
        double pcpu = 0;
        if (_procInit)
        {
            var wallMs = (now - _lastWall).TotalMilliseconds;
            if (wallMs > 0) pcpu = 100.0 * (used - _lastCpu).TotalMilliseconds / (wallMs * Environment.ProcessorCount);
        }
        _procInit = true; _lastWall = now; _lastCpu = used;
        return (Clamp(pcpu), ram);
    }

    private static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 100) : 0;

    private static bool TryReadLinuxStat(out (long Busy, long Total) value)
    {
        value = default;
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            return line is not null && TryParseProcStat(line, out value);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Prima riga di /proc/stat: "cpu user nice system idle iowait irq softirq steal ...". Occupato = totale − idle − iowait.</summary>
    public static bool TryParseProcStat(string line, out (long Busy, long Total) value)
    {
        value = default;
        if (!line.StartsWith("cpu ", StringComparison.Ordinal)) return false;
        var f = new List<long>();
        foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8))
        {
            if (!long.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) return false;
            f.Add(n);
        }
        if (f.Count < 4) return false;
        var total = f.Sum();
        var idle = f[3] + (f.Count > 4 ? f[4] : 0);
        value = (total - idle, total);
        return true;
    }
}

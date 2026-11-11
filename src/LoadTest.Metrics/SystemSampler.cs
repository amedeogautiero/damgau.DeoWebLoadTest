using System.Diagnostics;

namespace LoadTest.Metrics;

/// <summary>
/// Campionatore CPU/RAM del processo che ospita il motore.
/// Linux: CPU dell'intera macchina da /proc/stat. Altri sistemi: CPU del solo processo.
/// RAM = working set del processo corrente.
/// </summary>
public sealed class SystemSampler
{
    private (long Busy, long Total)? _lastStat;
    private TimeSpan _lastCpu;
    private DateTime _lastWall;
    private bool _procInit;

    public (double CpuPercent, long RamUsedMb) Sample()
    {
        using var process = Process.GetCurrentProcess();
        var ram = process.WorkingSet64 / (1024 * 1024);

        if (OperatingSystem.IsLinux() && TryReadLinuxStat(out var current))
        {
            var cpu = _lastStat is { } previous && current.Total > previous.Total
                ? 100.0 * (current.Busy - previous.Busy) / (current.Total - previous.Total)
                : 0;

            _lastStat = current;
            return (Clamp(cpu), ram);
        }

        var now = DateTime.UtcNow;
        var used = process.TotalProcessorTime;
        double processCpu = 0;

        if (_procInit)
        {
            var wallMs = (now - _lastWall).TotalMilliseconds;
            if (wallMs > 0)
                processCpu = 100.0 * (used - _lastCpu).TotalMilliseconds /
                    (wallMs * Environment.ProcessorCount);
        }

        _procInit = true;
        _lastWall = now;
        _lastCpu = used;
        return (Clamp(processCpu), ram);
    }

    private static double Clamp(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

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

    /// <summary>Prima riga di /proc/stat: "cpu user nice system idle iowait irq softirq steal ...".</summary>
    public static bool TryParseProcStat(string line, out (long Busy, long Total) value)
    {
        value = default;
        if (!line.StartsWith("cpu ", StringComparison.Ordinal))
            return false;

        var fields = new List<long>();
        foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8))
        {
            if (!long.TryParse(part, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
                return false;

            fields.Add(number);
        }

        if (fields.Count < 4)
            return false;

        var total = fields.Sum();
        var idle = fields[3] + (fields.Count > 4 ? fields[4] : 0);
        value = (total - idle, total);
        return true;
    }
}

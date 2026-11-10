using System.Runtime.InteropServices;

namespace LoadTest.Agent;

public static class PlatformInfo
{
    /// <summary>RID "portabile" (win/linux/osx + x64/arm64/arm/x86) come nell'elenco della sez. 1 del contratto.</summary>
    public static string CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.X86 => "x86",
            _ => "unknown"
        };
        return $"{os}-{arch}";
    }
}

public static class AgentIdentity
{
    public static string Resolve(AgentOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.AgentId)) return o.AgentId;
        Directory.CreateDirectory(o.WorkDirectory);
        var file = Path.Combine(o.WorkDirectory, "agent-id");
        if (File.Exists(file))
        {
            var text = File.ReadAllText(file).Trim();
            if (text.Length > 0) return text;
        }
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(file, id);
        return id;
    }
}

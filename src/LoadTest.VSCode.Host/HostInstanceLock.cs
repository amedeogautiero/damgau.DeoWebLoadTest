using System.Runtime.InteropServices;

namespace LoadTest.VSCode.Host;

/// <summary>
/// Holds an operating-system file lock for the lifetime of the Host process.
/// The lock file is deliberately shared by all users on the machine.
/// The operating system releases the exclusive file handle if the process exits unexpectedly.
/// </summary>
internal sealed class HostInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private HostInstanceLock(FileStream stream) => _stream = stream;

    public static HostInstanceLock Acquire()
    {
        var lockPath = GetLockPath();
        var directory = Path.GetDirectoryName(lockPath)!;
        Directory.CreateDirectory(directory);

        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            // On Unix-like systems the shared /tmp lock file must be openable by
            // processes running under another account. FileShare.None still
            // provides the exclusive OS-level lock.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

            return new HostInstanceLock(stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Another DeoLoadTest Host instance is already running on this machine, " +
                "or the machine-wide Host lock cannot be acquired.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"Cannot access the machine-wide DeoLoadTest Host lock at '{lockPath}'.", ex);
        }
    }

    private static string GetLockPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonData))
                throw new InvalidOperationException("The common application data directory is unavailable.");

            return Path.Combine(commonData, "DeoLoadTest", "LoadTest.VSCode.Host.lock");
        }

        // /tmp is shared by local users on Linux and macOS.
        return Path.Combine(Path.GetTempPath(), "deoloadtest-vscode-host.lock");
    }

    public void Dispose() => _stream.Dispose();
}

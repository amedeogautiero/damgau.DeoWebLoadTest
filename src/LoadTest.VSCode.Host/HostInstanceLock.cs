namespace LoadTest.VSCode.Host;

/// <summary>
/// Enforces one Host instance per machine. Windows uses a global named mutex;
/// Linux and macOS use an exclusive lock on a file in the shared temporary directory.
/// Both locks are released automatically by the operating system if the process dies.
/// </summary>
internal sealed class HostInstanceLock : IDisposable
{
    private readonly FileStream? _unixLock;
    private readonly Thread? _mutexThread;
    private readonly ManualResetEventSlim? _releaseMutex;

    private HostInstanceLock(FileStream unixLock) => _unixLock = unixLock;

    private HostInstanceLock(Thread mutexThread, ManualResetEventSlim releaseMutex)
    {
        _mutexThread = mutexThread;
        _releaseMutex = releaseMutex;
    }

    public static HostInstanceLock Acquire()
    {
        if (OperatingSystem.IsWindows())
            return AcquireWindowsMutex();

        return AcquireUnixFileLock();
    }

    private static HostInstanceLock AcquireWindowsMutex()
    {
        var acquired = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        Exception? failure = null;
        var ownsMutex = false;

        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, @"Global\DeoLoadTest.LoadTest.VSCode.Host");

            try
            {
                try
                {
                    ownsMutex = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // The previous Host terminated unexpectedly. This thread now owns the mutex.
                    ownsMutex = true;
                }

                acquired.Set();
                if (ownsMutex)
                    release.Wait();
            }
            catch (Exception ex)
            {
                failure = ex;
                acquired.Set();
            }
            finally
            {
                if (ownsMutex)
                    mutex.ReleaseMutex();
            }
        })
        {
            IsBackground = true,
            Name = "DeoLoadTest Host instance lock"
        };

        thread.Start();
        acquired.Wait();
        acquired.Dispose();

        if (failure is not null)
        {
            release.Set();
            thread.Join();
            release.Dispose();
            throw new InvalidOperationException("Unable to create the machine-wide DeoLoadTest Host mutex.", failure);
        }

        if (!ownsMutex)
        {
            release.Dispose();
            throw new InvalidOperationException("Another DeoLoadTest Host instance is already running on this machine.");
        }

        return new HostInstanceLock(thread, release);
    }

    private static HostInstanceLock AcquireUnixFileLock()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "deoloadtest-vscode-host.lock");

        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                // Permit another local account to open the shared lock file. The
                // exclusive FileShare.None handle, not file contents, represents ownership.
                File.SetUnixFileMode(lockPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            return new HostInstanceLock(stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Another DeoLoadTest Host instance is already running on this machine, or the shared Host lock is unavailable.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"Cannot access the machine-wide DeoLoadTest Host lock at '{lockPath}'.", ex);
        }
    }

    public void Dispose()
    {
        _unixLock?.Dispose();

        if (_releaseMutex is not null && _mutexThread is not null)
        {
            _releaseMutex.Set();
            _mutexThread.Join(TimeSpan.FromSeconds(2));
            _releaseMutex.Dispose();
        }
    }
}

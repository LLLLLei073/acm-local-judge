namespace Judge.Core;

// Sandbox facade: Windows Job Object session locally, rlimit/cgroup session on Linux servers.
public sealed class NativeRunner : IAsyncDisposable
{
    private readonly SandboxSession session;
    public NativeRunner(string executable, IEnumerable<string> arguments, string directory, Limits limits,
        CancellationToken token, Func<byte[], Task>? onOutput = null, int processLimit = 1)
        => session = OperatingSystem.IsWindows()
            ? new WindowsJobSession(executable, arguments, directory, limits, token, onOutput, processLimit)
            : new LinuxProcessSession(executable, arguments, directory, limits, token, onOutput, processLimit);
    public Task<RunResult> Completion => session.Completion;
    public Task Send(byte[] bytes) => session.Send(bytes);
    public Task Send(string text) => session.Send(text);
    public Task CloseInput() => session.CloseInput();
    public void Stop(string verdict, string reason) => session.Stop(verdict, reason);
    public ValueTask DisposeAsync() => session.DisposeAsync();
}

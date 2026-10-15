using System.Diagnostics;
using System.Text;

namespace Judge.Core;

// Shared judge-session plumbing: pipe capture, the 10ms limit-polling loop and verdict assembly.
// Platform confinement lives in the subclasses (Windows Job Objects / Linux rlimit + cgroups).
internal abstract class SandboxSession(Limits limits, CancellationToken token) : IAsyncDisposable
{
    protected readonly Limits limits = limits;
    protected readonly CancellationToken token = token;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly SemaphoreSlim inputGate = new(1);
    private readonly MemoryStream stdout = new(), stderr = new();
    private long outputBytes;
    private string? forced;
    private string message = "";
    private bool inputClosed;
    private Stream input = Stream.Null;
    private Task<RunResult> completion = null!;

    public Task<RunResult> Completion => completion;
    public double WallMilliseconds => clock.Elapsed.TotalMilliseconds;

    // Subclasses finish preparing the child process, then call Begin exactly once with its pipes.
    protected void Begin(Stream? stdinStream, Stream? outStream, Stream? errStream, Func<byte[], Task>? onOutput)
    {
        if (stdinStream != null) input = stdinStream;
        // Pumping on the threadpool keeps blocking pipe reads off the caller's thread on both platforms.
        var readOut = Task.Run(() => Pump(outStream, stdout, onOutput));
        var readErr = Task.Run(() => Pump(errStream, stderr, null));
        completion = Finish(readOut, readErr);
    }
    public async Task Send(byte[] bytes)
    {
        await inputGate.WaitAsync(token);
        try { if (!inputClosed) { await input.WriteAsync(bytes, token); await input.FlushAsync(token); } }
        catch (IOException) { /* Peer closed stdin; its exit/verdict determines the result. */ }
        finally { inputGate.Release(); }
    }
    public Task Send(string text) => Send(Encoding.UTF8.GetBytes(text));
    public async Task CloseInput()
    {
        await inputGate.WaitAsync();
        try { if (!inputClosed) { inputClosed = true; input.Dispose(); } }
        finally { inputGate.Release(); }
    }
    public void Stop(string verdict, string reason)
    {
        if (Interlocked.CompareExchange(ref forced, verdict, null) == null) message = reason;
        Kill();
    }
    private async Task Pump(Stream? stream, MemoryStream capture, Func<byte[], Task>? callback)
    {
        if (stream == null) return;
        var buffer = new byte[8192];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                var total = Interlocked.Add(ref outputBytes, n);
                if (total > (long)limits.OutputKiB * 1024) { Stop("OLE", "stdout 与 stderr 合计超过输出限制"); break; }
                capture.Write(buffer, 0, n);
                if (callback != null) await callback(buffer[..n]);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception ex) { Stop("Judge Error", "管道处理失败：" + ex.Message); }
    }
    private async Task<RunResult> Finish(Task readOut, Task readErr)
    {
        double cpu = 0; long memory = 0;
        while (true)
        {
            bool exited = HasExited;
            AfterPoll();
            cpu = CpuMilliseconds(); memory = PeakMemoryBytes();
            if (token.IsCancellationRequested) Stop("Cancelled", "评测已取消");
            else if (cpu > limits.CpuMs) Stop("TLE", "超过 CPU 时间限制");
            else if (!exited && clock.Elapsed.TotalMilliseconds > limits.WallMs) Stop("TLE", "超过墙钟时间限制，可能在等待输入或未 flush");
            if (exited) break;
            await Task.Delay(10);
        }
        // A parent exiting must not leave its children or pipe writers behind.
        Kill();
        await Task.WhenAll(readOut, readErr);
        clock.Stop();
        uint code = ExitCode;
        var verdict = forced ?? (code == 0 ? "AC" : "RE");
        return new(verdict, forced != null ? message : code == 0 ? "正常退出" : ExitFailureNote(code), code,
            new(cpu, clock.Elapsed.TotalMilliseconds, memory), Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()), Convert.ToBase64String(stdout.ToArray()));
    }
    public async ValueTask DisposeAsync()
    {
        if (!Completion.IsCompleted) Stop("Cancelled", "会话结束");
        try { await Completion; } finally { input.Dispose(); Cleanup(); }
    }

    protected abstract bool HasExited { get; }
    protected abstract uint ExitCode { get; }
    protected abstract string ExitFailureNote(uint code);
    protected abstract double CpuMilliseconds();
    protected abstract long PeakMemoryBytes();
    protected abstract void AfterPoll();
    protected abstract void Kill();
    protected abstract void Cleanup();
}

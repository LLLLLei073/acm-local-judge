using System.Diagnostics;
using System.Threading.Channels;
using Judge.Core;

namespace Judge.Server;

public sealed class JudgeQueue(Store store, ILogger<JudgeQueue> logger) : BackgroundService
{
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly List<Task> active = [];
    private int runningCount;
    private int queuedCount;
    public void Add(string id) { if (queue.Writer.TryWrite(id)) Interlocked.Increment(ref queuedCount); }
    public int Running => Volatile.Read(ref runningCount);
    public int Queued => Volatile.Read(ref queuedCount);
    public void Cancel(string id)
    {
        var s = store.Submission(id) ?? throw new KeyNotFoundException("提交不存在");
        if (!IsActive(s.Status)) return;
        File.WriteAllText(Path.Combine(store.SubmissionDir(id), "cancel"), "1"); store.Status(id, "Cancelled");
    }
    public static bool IsActive(string status) => status is "Queued" or "Compiling" or "Running";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
            {
                Interlocked.Decrement(ref queuedCount);
                if (store.Submission(id)?.Status == "Cancelled") continue;
                // The limit is re-read per dispatch so a settings change takes effect without a restart.
                while (Volatile.Read(ref runningCount) >= ServerSettings.Parallelism(store.Data)) await Task.Delay(100, stoppingToken);
                Interlocked.Increment(ref runningCount);
                active.RemoveAll(t => t.IsCompleted);
                active.Add(Task.Run(() => JudgeOneAsync(id, stoppingToken), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await Task.WhenAll(active);
    }

    private async Task JudgeOneAsync(string id, CancellationToken stoppingToken)
    {
        string dir = store.SubmissionDir(id);
        try
        {
            var dll = Path.Combine(store.Root, "artifacts", "worker", "Judge.Worker.dll");
            if (!File.Exists(dll)) throw new FileNotFoundException("请先通过启动脚本构建 Worker", dll);
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
            start.Environment["ACM_PARENT_PID"] = Environment.ProcessId.ToString();
            start.ArgumentList.Add(dll); start.ArgumentList.Add(Path.Combine(dir, "request.json"));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Worker 启动失败");
            using var registration = stoppingToken.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            try
            {
                var errors = process.StandardError.ReadToEndAsync(stoppingToken);
                while (await process.StandardOutput.ReadLineAsync(stoppingToken) is { } line)
                {
                    var p = Json.Read<Progress>(line);
                    if (store.Submission(id)?.Status != "Cancelled") store.Status(id, p.Status);
                }
                await process.WaitForExitAsync(stoppingToken);
                if (process.ExitCode != 0) throw new InvalidOperationException("Worker 异常退出：" + await errors);
                if (store.Submission(id)?.Status != "Cancelled") store.Status(id, store.Result(id).Status);
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { store.Status(id, "Interrupted"); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Judge worker failed for {Id}", id);
            if (store.Submission(id)?.Status != "Cancelled")
            {
                File.WriteAllText(Path.Combine(dir, "result.json"), Json.Write(new JudgeResult { Status = "Judge Error", Message = ex.Message })); store.Status(id, "Judge Error");
            }
        }
        finally { Interlocked.Decrement(ref runningCount); }
    }
}

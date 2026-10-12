using System.Diagnostics;
using System.Threading.Channels;
using Judge.Core;

namespace Judge.Server;

public sealed class JudgeQueue(Store store, ILogger<JudgeQueue> logger) : BackgroundService
{
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    public void Add(string id) => queue.Writer.TryWrite(id);
    public void Cancel(string id)
    {
        var s = store.Submission(id) ?? throw new KeyNotFoundException("提交不存在");
        if (!IsActive(s.Status)) return;
        File.WriteAllText(Path.Combine(store.SubmissionDir(id), "cancel"), "1"); store.Status(id, "Cancelled");
    }
    public static bool IsActive(string status) => status is "Queued" or "Compiling" or "Running";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try {
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (store.Submission(id)?.Status == "Cancelled") continue;
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
                try {
                var errors = process.StandardError.ReadToEndAsync(stoppingToken);
                while (await process.StandardOutput.ReadLineAsync(stoppingToken) is { } line)
                {
                    var p = Json.Read<Progress>(line);
                    if (store.Submission(id)?.Status != "Cancelled") store.Status(id, p.Status);
                }
                await process.WaitForExitAsync(stoppingToken);
                if (process.ExitCode != 0) throw new InvalidOperationException("Worker 异常退出：" + await errors);
                if (store.Submission(id)?.Status != "Cancelled") store.Status(id, store.Result(id).Status);
                } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { store.Status(id, "Interrupted"); break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Judge worker failed for {Id}", id);
                if (store.Submission(id)?.Status != "Cancelled")
                {
                    File.WriteAllText(Path.Combine(dir, "result.json"), Json.Write(new JudgeResult { Status = "Judge Error", Message = ex.Message })); store.Status(id, "Judge Error");
                }
            }
        }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

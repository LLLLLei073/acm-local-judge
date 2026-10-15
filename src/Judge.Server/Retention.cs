using Judge.Core;

namespace Judge.Server;

// Periodic disk housekeeping: prunes old immutable problem version directories and, when configured,
// terminal submission directories. In-flight snapshots live in request.json, so pruning is safe.
public sealed class Retention(Store store, ILogger<Retention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        try
        {
            do
            {
                try { Clean(); }
                catch (Exception ex) { logger.LogError(ex, "磁盘清理任务失败"); }
            }
            while (await timer.WaitForNextTickAsync(stopping));
        }
        catch (OperationCanceledException) { }
    }
    private void Clean()
    {
        int keepVersions = ServerSettings.RetainProblemVersions(store.Data);
        int keepSubmissions = ServerSettings.RetainSubmissions(store.Data);
        if (keepVersions > 0 && Directory.Exists(Path.Combine(store.Data, "problems")))
            foreach (var problem in Directory.EnumerateDirectories(Path.Combine(store.Data, "problems")))
            {
                var versions = Directory.EnumerateDirectories(problem).Order(StringComparer.Ordinal).ToList();
                foreach (var old in versions.Take(Math.Max(0, versions.Count - keepVersions)))
                    try { Directory.Delete(old, true); logger.LogInformation("清理题目历史版本 {Dir}", old); }
                    catch (Exception ex) { logger.LogWarning(ex, "无法清理 {Dir}", old); }
            }
        if (keepSubmissions > 0)
            foreach (var submission in store.AllSubmissions().Where(s => !JudgeQueue.IsActive(s.Status)).Skip(keepSubmissions).ToList())
            {
                store.DeleteSubmission(submission.Id);
                try { Directory.Delete(store.SubmissionDir(submission.Id), true); logger.LogInformation("清理历史提交 {Id}", submission.Id); }
                catch (Exception ex) { logger.LogWarning(ex, "无法清理提交目录 {Id}", submission.Id); }
            }
    }
}

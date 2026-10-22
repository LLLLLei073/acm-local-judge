using System.Collections.Concurrent;
using System.Threading.Channels;
using Judge.Core;

namespace Judge.Server;

public record CfProblem(int? ContestId, string Index, string Name, int? Rating, string[]? Tags, string? ProblemsetName)
{
    public string Key => $"{ContestId?.ToString() ?? ProblemsetName ?? "unknown"}/{Index}";
}
public record CfSubmission(long Id, int? ContestId, long CreationTimeSeconds, CfProblem Problem, string? Verdict,
    string ProgrammingLanguage, long TimeConsumedMillis, long MemoryConsumedBytes);
public record CfHistory(string Handle, string BindingVersion, DateTimeOffset UpdatedAt, List<CfSubmission> Submissions);
public record CfSyncState(string Status, int Fetched, string Message);

public sealed partial class Store
{
    public CfHistory? CfHistory(User user)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT body FROM cf_history WHERE userId=@id"; cmd.Parameters.AddWithValue("@id", user.Id);
        var history = cmd.ExecuteScalar() is string body ? Json.Read<CfHistory>(body) : null;
        return history?.BindingVersion == user.CodeforcesBindingVersion && history.Handle.Equals(user.CodeforcesHandle, StringComparison.OrdinalIgnoreCase) ? history : null;
    }
    // Re-read the account inside the transaction: an old job must never restore an unbound account,
    // or overwrite a concurrent password/privacy change with the user captured at job start.
    public bool SaveCfHistory(string userId, CfHistory history, Codeforces.CfInfo info)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT body FROM users WHERE id=@id" + (IsPostgres ? " FOR UPDATE" : ""); cmd.Parameters.AddWithValue("@id", userId);
        if (cmd.ExecuteScalar() is not string body) return false;
        var user = Json.Read<User>(body);
        if (user.CodeforcesBindingVersion != history.BindingVersion || !user.CodeforcesHandle.Equals(history.Handle, StringComparison.OrdinalIgnoreCase)) return false;
        user.CodeforcesRating = info.Rating; user.CodeforcesRank = info.Rank; user.RatingUpdatedAt = history.UpdatedAt;
        cmd.CommandText = "UPDATE users SET body=@body WHERE id=@id"; cmd.Parameters.AddWithValue("@body", Json.Write(user)); cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO cf_history(userId,body) VALUES(@id,@body) ON CONFLICT(userId) DO UPDATE SET body=excluded.body";
        cmd.Parameters["@body"].Value = Json.Write(history); cmd.ExecuteNonQuery(); tx.Commit(); return true;
    }
    public void ClearCfHistory(string userId)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM cf_history WHERE userId=@id"; cmd.Parameters.AddWithValue("@id", userId); cmd.ExecuteNonQuery();
    }
}

// Network work survives closing the profile modal, but stops with the server. Only a complete
// successful scan replaces the last snapshot; partial scans and retries never erase good data.
public sealed class CodeforcesSync(Store store, ILogger<CodeforcesSync> logger) : BackgroundService
{
    private record Job(string UserId, string Handle, string Version, DateTimeOffset RequestedAt);
    private readonly Channel<Job> queue = Channel.CreateUnbounded<Job>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<string, (Job Job, CfSyncState State)> states = new();
    private readonly object gate = new();
    public CfSyncState State(User user) => states.TryGetValue(user.Id, out var entry) && entry.Job.Version == user.CodeforcesBindingVersion && entry.Job.Handle == user.CodeforcesHandle
        ? entry.State : new("idle", 0, "");
    public CfSyncState Enqueue(User user, bool automatic = false)
    {
        if (user.CodeforcesHandle.Length == 0) throw new InvalidDataException("请先绑定 Codeforces 账号");
        lock (gate)
        {
            var state = State(user);
            if (state.Status is "queued" or "running") return state;
            if (automatic && states.TryGetValue(user.Id, out var previous) && previous.Job.Version == user.CodeforcesBindingVersion && DateTimeOffset.UtcNow - previous.Job.RequestedAt < TimeSpan.FromMinutes(5)) return state;
            var job = new Job(user.Id, user.CodeforcesHandle, user.CodeforcesBindingVersion, DateTimeOffset.UtcNow);
            state = new("queued", 0, "等待同步 Codeforces 公开记录");
            states[user.Id] = (job, state); queue.Writer.TryWrite(job); return state;
        }
    }
    private void Progress(Job job, CfSyncState state)
    {
        lock (gate) { if (states.TryGetValue(job.UserId, out var existing) && existing.Job == job) states[job.UserId] = (job, state); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var count = 0;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromMinutes(20));
                var ct = deadline.Token;
                var submissions = new Dictionary<long, CfSubmission>();
                for (var from = 1; ; from += 1000)
                {
                    var current = store.User(job.UserId);
                    if (current?.CodeforcesHandle != job.Handle || current.CodeforcesBindingVersion != job.Version) throw new InvalidDataException("绑定已变更，本次同步已停止");
                    Progress(job, new("running", count, $"正在读取第 {from} 条起的提交记录"));
                    var page = await Codeforces.RequestAsync<List<CfSubmission>>($"user.status?handle={Uri.EscapeDataString(job.Handle)}&from={from}&count=1000", true, ct);
                    if (page.Any(s => s.Id <= 0 || s.Problem == null || string.IsNullOrEmpty(s.Problem.Index))) throw new InvalidDataException("Codeforces 提交数据不完整，保留上次同步记录");
                    foreach (var submission in page) submissions[submission.Id] = submission;
                    if (page.Count > 0 && submissions.Count == count) throw new InvalidDataException("Codeforces 分页未前进，保留上次同步记录，请稍后重试");
                    count = submissions.Count;
                    if (count > 200000) throw new InvalidDataException("记录超过单次同步 20 万条上限，未替换上次记录");
                    if (page.Count < 1000) break;
                    if (from >= 200000) throw new InvalidDataException("记录超过单次同步 20 万条上限，未替换上次记录");
                }
                var info = (await Codeforces.FetchAsync([job.Handle], ct, fresh: true)).SingleOrDefault(i => i.Handle.Equals(job.Handle, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("Codeforces 账号信息缺失");
                var history = new CfHistory(job.Handle, job.Version, DateTimeOffset.UtcNow, submissions.Values.OrderByDescending(s => s.Id).ToList());
                if (!store.SaveCfHistory(job.UserId, history, info)) throw new InvalidDataException("绑定已变更，本次结果未保存");
                Progress(job, new("complete", count, $"已同步 {count} 条公开提交记录"));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "CF synchronization failed for account {UserId}", job.UserId);
                Progress(job, new("failed", count, ex is InvalidDataException ? ex.Message : ex is OperationCanceledException ? "同步超时，已有记录已保留，可重新同步" : "同步失败，已有记录已保留，请稍后重试"));
            }
        }
    }
}

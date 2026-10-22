using System.Data.Common;
using System.IO.Compression;
using Judge.Core;

namespace Judge.Server;

public sealed partial class Store
{
    public string? RemoteMetadata(string key)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM metadata WHERE key=@key"; cmd.Parameters.AddWithValue("@key", key); return cmd.ExecuteScalar() as string;
    }
    public void RemoteMetadata(string key, string value)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO metadata(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("@key", key); cmd.Parameters.AddWithValue("@value", value); cmd.ExecuteNonQuery();
    }
    public bool RemoteWorkerReady => DateTimeOffset.TryParse(RemoteMetadata("worker_seen"), out var seen) && DateTimeOffset.UtcNow - seen < TimeSpan.FromSeconds(100);
    private static byte[] Pack<T>(T value)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true))
        using (var writer = new StreamWriter(gzip)) writer.Write(Json.Write(value));
        return buffer.ToArray();
    }
    private static T Unpack<T>(object value)
    {
        using var stream = new GZipStream(new MemoryStream((byte[])value), CompressionMode.Decompress);
        using var reader = new StreamReader(stream); return Json.Read<T>(reader.ReadToEnd());
    }
    public Dictionary<string, string> SnapshotToolkit(string? submissionId = null, string? path = null)
    {
        if (IsPostgres && submissionId != null) return RemotePayload(submissionId).Toolkit;
        return Directory.GetFiles(path ?? Path.Combine(Root, "toolkit")).ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToBase64String(File.ReadAllBytes(p)));
    }
    public JudgeRequest Request(string id) => IsPostgres ? RemotePayload(id).Request
        : Json.Read<JudgeRequest>(File.ReadAllText(Path.Combine(SubmissionDir(id), "request.json")));
    private RemotePayload RemotePayload(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM remote_jobs WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id);
        return Unpack<RemotePayload>(cmd.ExecuteScalar() ?? throw new KeyNotFoundException("提交快照不存在"));
    }
    private static void InsertJob(DbConnection db, DbTransaction tx, string id, string kind, RemotePayload payload)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO remote_jobs(id,kind,payload) VALUES(@id,@kind,@payload)";
        cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@kind", kind); cmd.Parameters.AddWithValue("@payload", Pack(payload)); cmd.ExecuteNonQuery();
    }
    private Submission AddRemoteSubmission(Problem p, string source, string compiler, string? userId, string? originalToolkit, string? originalId)
    {
        var id = Guid.NewGuid().ToString("N");
        var request = new JudgeRequest(id, p, source, compiler, "toolkit", "work");
        var payload = new RemotePayload(request, SnapshotToolkit(originalId, originalToolkit));
        var s = new Submission(id, p.Id, p.Title, p.Mode, "Queued", DateTimeOffset.UtcNow, userId);
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO submissions(id,problemId,title,mode,status,createdAt,userId) VALUES(@id,@pid,@title,@mode,'Queued',@date,@uid)";
        cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@pid", p.Id); cmd.Parameters.AddWithValue("@title", p.Title);
        cmd.Parameters.AddWithValue("@mode", p.Mode.ToString()); cmd.Parameters.AddWithValue("@date", s.CreatedAt.ToString("O")); cmd.Parameters.AddWithValue("@uid", (object?)userId ?? DBNull.Value);
        cmd.ExecuteNonQuery(); InsertJob(db, tx, id, "judge", payload); tx.Commit(); return s;
    }
    public RemoteClaim? ClaimRemote()
    {
        RemoteMetadata("worker_seen", DateTimeOffset.UtcNow.ToString("O"));
        ExpireRemote();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE remote_jobs SET state='Running',lease=@lease,leaseuntil=now()+interval '90 seconds' WHERE id=(SELECT id FROM remote_jobs WHERE state='Queued' ORDER BY createdat FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING id,kind,payload";
        var lease = Guid.NewGuid().ToString("N"); cmd.Parameters.AddWithValue("@lease", lease);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), lease, reader.GetString(1), Unpack<RemotePayload>(reader.GetValue(2))) : null;
    }
    public bool HeartbeatRemote(string id, RemoteHeartbeat heartbeat)
    {
        RemoteMetadata("worker_seen", DateTimeOffset.UtcNow.ToString("O"));
        if (heartbeat.Complete && heartbeat.Output == null) throw new InvalidDataException("完成任务必须提供结果");
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT kind FROM remote_jobs WHERE id=@id AND lease=@lease AND state='Running' AND leaseuntil>now() FOR UPDATE";
        cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@lease", heartbeat.Lease);
        if (cmd.ExecuteScalar() is not string kind) return false;
        string? status = null;
        if (kind == "judge" && heartbeat.Output is { } output)
        {
            status = output.Error != null ? "Judge Error" : output.Result?.Status;
            string[] statuses = ["Queued", "Compiling", "Running", "AC", "WA", "PE", "CE", "RE", "TLE", "MLE", "OLE", "Judge Error", "Cancelled"];
            if (status == null || !statuses.Contains(status) || heartbeat.Complete && JudgeQueue.IsActive(status))
                throw new InvalidDataException("无效评测结果状态");
        }
        cmd.CommandText = "UPDATE remote_jobs SET leaseuntil=now()+interval '90 seconds',state=@state" + (heartbeat.Output != null ? ",output=@output" : "") + " WHERE id=@id";
        cmd.Parameters.AddWithValue("@state", heartbeat.Complete ? "Complete" : "Running");
        if (heartbeat.Output != null) cmd.Parameters.AddWithValue("@output", Pack(heartbeat.Output)); cmd.ExecuteNonQuery();
        if (kind == "judge")
        {
            cmd.CommandText = "UPDATE submissions SET status=@status WHERE id=@id AND status NOT IN ('Cancelled','Interrupted')";
            cmd.Parameters.AddWithValue("@status", status ?? "Running"); cmd.ExecuteNonQuery();
        }
        tx.Commit(); return true;
    }
    public void ExpireRemote()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "WITH expired AS (UPDATE remote_jobs SET state='Interrupted',lease=NULL WHERE state='Running' AND leaseuntil<=now() RETURNING id) UPDATE submissions SET status='Interrupted' WHERE id IN (SELECT id FROM expired) AND status NOT IN ('Cancelled','Interrupted')";
        cmd.ExecuteNonQuery();
    }
    public void CancelRemote(string id)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE remote_jobs SET state='Cancelled',lease=NULL WHERE id=@id AND state IN ('Queued','Running'); UPDATE submissions SET status='Cancelled' WHERE id=@id AND status IN ('Queued','Compiling','Running')";
        cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery(); tx.Commit();
    }
    private (string State, RemoteOutput? Output) RemoteOutput(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT state,output FROM remote_jobs WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) throw new KeyNotFoundException("任务不存在");
        return (reader.GetString(0), reader.IsDBNull(1) ? null : Unpack<RemoteOutput>(reader.GetValue(1)));
    }
    private JudgeResult RemoteJudgeResult(string id)
    {
        var job = RemoteOutput(id);
        return job.Output?.Result ?? new() { Status = job.Output?.Error != null ? "Judge Error" : "Queued", Message = job.Output?.Error ?? "等待 Windows 评测端连接并领取任务" };
    }
    public int RemoteCount(string state)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM remote_jobs WHERE state=@state"; cmd.Parameters.AddWithValue("@state", state); return Convert.ToInt32(cmd.ExecuteScalar());
    }
    public async Task<RemoteOutput> AuthoringRemote(string kind, Problem p, string source, List<RemoteInput>? inputs, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        using (var db = Open()) using (var tx = db.BeginTransaction())
        { InsertJob(db, tx, id, kind, new(new(id, p, source, "", "toolkit", "work"), SnapshotToolkit(), inputs)); tx.Commit(); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            while (true)
            {
                var job = RemoteOutput(id);
                if (job.Output?.Error is { } error) throw new InvalidDataException(error);
                if (job.State == "Complete") return job.Output ?? throw new InvalidDataException("评测端未返回结果");
                if (job.State is "Interrupted" or "Cancelled") throw new InvalidDataException("评测端已离线或任务取消，请重新执行");
                await Task.Delay(1500, deadline.Token);
            }
        }
        finally
        {
            CancelRemote(id);
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM remote_jobs WHERE id=@id AND kind IN ('brute','harden')";
            cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery();
        }
    }
}

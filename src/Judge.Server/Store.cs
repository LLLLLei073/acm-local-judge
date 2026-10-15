using Judge.Core;
using Microsoft.Data.Sqlite;

namespace Judge.Server;

public record Submission(string Id, string ProblemId, string Title, JudgeMode Mode, string Status, DateTimeOffset CreatedAt);
public sealed class Store
{
    public string Root { get; }
    public string Data { get; }
    private string Connection => new SqliteConnectionStringBuilder { DataSource = Path.Combine(Data, "judge.db"), Pooling = true }.ToString();
    public Store(string root, string data)
    {
        Root = root; Data = data; Directory.CreateDirectory(data);
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS problems(id TEXT PRIMARY KEY, body TEXT NOT NULL); CREATE TABLE IF NOT EXISTS submissions(id TEXT PRIMARY KEY, problemId TEXT NOT NULL, title TEXT NOT NULL, mode TEXT NOT NULL, status TEXT NOT NULL, createdAt TEXT NOT NULL);";
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE submissions SET status='Interrupted' WHERE status IN ('Queued','Compiling','Running')"; command.ExecuteNonQuery();
        if (Problems().Count == 0 && !File.Exists(Path.Combine(Data, ".seeded")))
        {
            foreach (var p in Examples.All()) Save(p, true);
            File.WriteAllText(Path.Combine(Data, ".seeded"), "1");
        }
    }
    private SqliteConnection Open() { var db = new SqliteConnection(Connection); db.Open(); return db; }
    public List<Problem> Problems()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM problems ORDER BY rowid";
        using var r = cmd.ExecuteReader(); var list = new List<Problem>(); while (r.Read()) list.Add(Json.Read<Problem>(r.GetString(0))); return list;
    }
    public Problem? Problem(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM problems WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string s ? Json.Read<Problem>(s) : null;
    }
    public Problem Save(Problem problem, bool create)
    {
        problem.Validate(); if (create) problem.Id = Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(problem.Id, "N", out _)) throw new InvalidDataException("无效题目编号");
        if (!create && Problem(problem.Id) == null) throw new KeyNotFoundException("题目不存在");
        // An immutable version directory avoids exposing partly-written data to concurrent submissions.
        var dir = Path.Combine(Data, "problems", problem.Id, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "problem.json"), Json.Write(problem));
        foreach (var t in problem.Tests) { File.WriteAllText(Path.Combine(dir, t.Name + ".in"), t.Input); File.WriteAllText(Path.Combine(dir, t.Name + ".ans"), t.Answer); }
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO problems(id,body) VALUES($id,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body";
        cmd.Parameters.AddWithValue("$id", problem.Id); cmd.Parameters.AddWithValue("$body", Json.Write(problem)); cmd.ExecuteNonQuery(); return problem;
    }
    public bool Delete(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM problems WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); return cmd.ExecuteNonQuery() > 0;
    }
    public string SubmissionDir(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new KeyNotFoundException("提交不存在");
        return Path.Combine(Data, "submissions", id);
    }
    public Submission AddSubmission(Problem problem, string source, string compiler, string? originalToolkit = null)
    {
        if (string.IsNullOrEmpty(source) || source.Length > 1048576) throw new InvalidDataException("代码不能为空或超过 1 MiB");
        if (problem.Tests.Count == 0) throw new InvalidDataException("请先添加测试数据");
        var id = Guid.NewGuid().ToString("N"); var dir = SubmissionDir(id); Directory.CreateDirectory(dir);
        var toolkit = Path.Combine(dir, "toolkit"); Directory.CreateDirectory(toolkit);
        foreach (var path in Directory.GetFiles(originalToolkit ?? Path.Combine(Root, "toolkit"))) File.Copy(path, Path.Combine(toolkit, Path.GetFileName(path)));
        File.WriteAllText(Path.Combine(dir, "source.cpp"), source);
        File.WriteAllText(Path.Combine(dir, "request.json"), Json.Write(new JudgeRequest(id, problem, source, compiler, toolkit, Path.Combine(dir, "work"))));
        File.WriteAllText(Path.Combine(dir, "result.json"), Json.Write(new JudgeResult()));
        var s = new Submission(id, problem.Id, problem.Title, problem.Mode, "Queued", DateTimeOffset.UtcNow);
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO submissions VALUES($id,$pid,$title,$mode,$status,$date)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$pid", s.ProblemId); cmd.Parameters.AddWithValue("$title", s.Title); cmd.Parameters.AddWithValue("$mode", s.Mode.ToString()); cmd.Parameters.AddWithValue("$status", s.Status); cmd.Parameters.AddWithValue("$date", s.CreatedAt.ToString("O")); cmd.ExecuteNonQuery(); return s;
    }
    public List<Submission> Submissions()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT * FROM submissions ORDER BY createdAt DESC LIMIT 200";
        using var r = cmd.ExecuteReader(); List<Submission> list = []; while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), Enum.Parse<JudgeMode>(r.GetString(3)), r.GetString(4), DateTimeOffset.Parse(r.GetString(5)))); return list;
    }
    public List<Submission> AllSubmissions()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT * FROM submissions ORDER BY createdAt DESC";
        using var r = cmd.ExecuteReader(); List<Submission> list = []; while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), Enum.Parse<JudgeMode>(r.GetString(3)), r.GetString(4), DateTimeOffset.Parse(r.GetString(5)))); return list;
    }
    public void DeleteSubmission(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM submissions WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public Submission? Submission(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT * FROM submissions WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetString(2), Enum.Parse<JudgeMode>(r.GetString(3)), r.GetString(4), DateTimeOffset.Parse(r.GetString(5))) : null;
    }
    public void Status(string id, string status)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE submissions SET status=$status WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$status", status); cmd.ExecuteNonQuery();
    }
    public JudgeResult Result(string id)
    {
        var s = Submission(id) ?? throw new KeyNotFoundException("提交不存在");
        using var file = new FileStream(Path.Combine(SubmissionDir(id), "result.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        var r = Json.Read<JudgeResult>(reader.ReadToEnd());
        if (s.Status == "Interrupted") { r.Status = "Interrupted"; r.Message = "服务重启时评测未完成，请重新评测"; }
        if (s.Status == "Cancelled") { r.Status = "Cancelled"; r.Message = "评测已取消"; }
        return r;
    }
}

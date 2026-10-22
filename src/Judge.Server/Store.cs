using Judge.Core;
using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace Judge.Server;

public record Submission(string Id, string ProblemId, string Title, JudgeMode Mode, string Status, DateTimeOffset CreatedAt, string? UserId = null);
public sealed partial class Store
{
    public string Root { get; }
    public string Data { get; }
    private readonly string? postgres;
    public bool IsPostgres => postgres is not null;
    public Store(string root, string data, string? postgresConnection = null)
    {
        postgres = Database.ValidatePostgres(postgresConnection);
        Root = root; Data = data; Directory.CreateDirectory(data);
        using var db = Open(); using var command = db.CreateCommand();
        if (IsPostgres)
        {
            // Fail before serving requests if the deployed schema is missing/incompatible.
            command.CommandText = "SELECT value FROM metadata WHERE key='schema_version'";
            if (command.ExecuteScalar() is not string version || version != "1")
                throw new InvalidDataException("云端数据库尚未初始化或版本不兼容");
            return;
        }
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS problems(id TEXT PRIMARY KEY, body TEXT NOT NULL); CREATE TABLE IF NOT EXISTS submissions(id TEXT PRIMARY KEY, problemId TEXT NOT NULL, title TEXT NOT NULL, mode TEXT NOT NULL, status TEXT NOT NULL, createdAt TEXT NOT NULL, userId TEXT); CREATE TABLE IF NOT EXISTS contests(id TEXT PRIMARY KEY, body TEXT NOT NULL); CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY, studentId TEXT NOT NULL UNIQUE COLLATE NOCASE, body TEXT NOT NULL); CREATE TABLE IF NOT EXISTS ratings(id TEXT PRIMARY KEY, userId TEXT NOT NULL, body TEXT NOT NULL);";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TABLE IF NOT EXISTS cf_history(userId TEXT PRIMARY KEY, body TEXT NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE submissions SET status='Interrupted' WHERE status IN ('Queued','Compiling','Running')"; command.ExecuteNonQuery();
        // Databases from before the user system lack the author column; add it in place (existing
        // rows keep NULL = anonymous/admin rather than being guessed).
        command.CommandText = "PRAGMA table_info(submissions)";
        var hasUserId = false;
        using (var columns = command.ExecuteReader()) { while (columns.Read()) if (columns.GetString(1) == "userId") hasUserId = true; }
        if (!hasUserId) { command.CommandText = "ALTER TABLE submissions ADD COLUMN userId TEXT"; command.ExecuteNonQuery(); }
        if (Problems().Count == 0 && !File.Exists(Path.Combine(Data, ".seeded")))
        {
            var seeded = new List<string>();
            foreach (var p in Examples.All()) { Save(p, true); seeded.Add(p.Id); }
            SaveContest(new Contest { Title = "入门示例", ProblemIds = seeded }, true);
            File.WriteAllText(Path.Combine(Data, ".seeded"), "1");
        }
    }
    private DbConnection Open() => Database.Open(Data, postgres);
    public List<Problem> Problems()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM problems ORDER BY rowid";
        using var r = cmd.ExecuteReader(); var list = new List<Problem>(); while (r.Read()) list.Add(Json.Read<Problem>(r.GetString(0))); return list;
    }
    public Problem? Problem(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM problems WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is string s ? Json.Read<Problem>(s) : null;
    }
    public Problem Save(Problem problem, bool create)
    {
        problem.Validate(); if (create) problem.Id = Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(problem.Id, "N", out _)) throw new InvalidDataException("无效题目编号");
        if (!create && Problem(problem.Id) == null) throw new KeyNotFoundException("题目不存在");
        // An immutable version directory avoids exposing partly-written data to concurrent submissions.
        if (!IsPostgres)
        {
            var dir = Path.Combine(Data, "problems", problem.Id, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "problem.json"), Json.Write(problem));
            foreach (var t in problem.Tests) { File.WriteAllText(Path.Combine(dir, t.Name + ".in"), t.Input); File.WriteAllText(Path.Combine(dir, t.Name + ".ans"), t.Answer); }
        }
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO problems(id,body) VALUES(@id,@body) ON CONFLICT(id) DO UPDATE SET body=excluded.body";
        cmd.Parameters.AddWithValue("@id", problem.Id); cmd.Parameters.AddWithValue("@body", Json.Write(problem)); cmd.ExecuteNonQuery(); return problem;
    }
    public bool Delete(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM problems WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id); var removed = cmd.ExecuteNonQuery() > 0;
        if (removed)
            // Membership lives on the contest side; drop the deleted problem from every contest body.
            foreach (var contest in Contests().Where(c => c.ProblemIds.Remove(id)))
            {
                using var update = db.CreateCommand(); update.CommandText = "UPDATE contests SET body=@body WHERE id=@id";
                update.Parameters.AddWithValue("@body", Json.Write(contest)); update.Parameters.AddWithValue("@id", contest.Id); update.ExecuteNonQuery();
            }
        return removed;
    }
    public List<Contest> Contests()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM contests ORDER BY rowid";
        using var r = cmd.ExecuteReader(); var list = new List<Contest>(); while (r.Read()) list.Add(Json.Read<Contest>(r.GetString(0))); return list;
    }
    public Contest? Contest(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM contests WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is string s ? Json.Read<Contest>(s) : null;
    }
    public Contest SaveContest(Contest contest, bool create)
    {
        var problems = Problems();
        contest.Validate(problems.Select(p => p.Id).ToHashSet());
        // 只有已评级（100–3500）的题目才能进入比赛，逼迫出题流程先完成评级。
        var unrated = problems.Where(p => contest.ProblemIds.Contains(p.Id) && p.Rating is < 100 or > 3500).ToList();
        if (unrated.Count > 0) throw new InvalidDataException("「" + string.Join("」「", unrated.Select(p => p.Title)) + "」未评级：加入比赛前必须标注 100–3500 的难度 rating");
        if (create) contest.Id = Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(contest.Id, "N", out _)) throw new InvalidDataException("无效比赛编号");
        if (!create && Contest(contest.Id) == null) throw new KeyNotFoundException("比赛不存在");
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO contests(id,body) VALUES(@id,@body) ON CONFLICT(id) DO UPDATE SET body=excluded.body";
        cmd.Parameters.AddWithValue("@id", contest.Id); cmd.Parameters.AddWithValue("@body", Json.Write(contest)); cmd.ExecuteNonQuery(); return contest;
    }
    public List<string> SubmissionIdsForProblem(string problemId)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id FROM submissions WHERE problemId=@pid ORDER BY createdAt";
        cmd.Parameters.AddWithValue("@pid", problemId);
        using var r = cmd.ExecuteReader(); var list = new List<string>(); while (r.Read()) list.Add(r.GetString(0)); return list;
    }
    public bool DeleteContest(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM contests WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id); return cmd.ExecuteNonQuery() > 0;
    }
    public List<User> Users()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM users ORDER BY rowid";
        using var r = cmd.ExecuteReader(); var list = new List<User>(); while (r.Read()) list.Add(Json.Read<User>(r.GetString(0))); return list;
    }
    public User? User(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM users WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is string s ? Json.Read<User>(s) : null;
    }
    public User? UserByStudentId(string studentId)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM users WHERE lower(studentId)=lower(@sid)"; cmd.Parameters.AddWithValue("@sid", studentId);
        return cmd.ExecuteScalar() is string s ? Json.Read<User>(s) : null;
    }
    public User SaveUser(User user, bool create)
    {
        user.Validate();
        if (create) user.Id = Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(user.Id, "N", out _)) throw new InvalidDataException("无效用户编号");
        if (!create && User(user.Id) == null) throw new KeyNotFoundException("用户不存在");
        // Uniqueness is checked case-insensitively in code; the NOCASE UNIQUE index on studentId is
        // only a backstop against concurrent registrations.
        var others = Users().Where(u => u.Id != user.Id).ToList();
        if (others.Any(u => u.StudentId.Equals(user.StudentId, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("该学号已注册");
        if (others.Any(u => u.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("该邮箱已注册");
        if (!string.IsNullOrEmpty(user.CodeforcesHandle) && others.Any(u => u.CodeforcesHandle.Equals(user.CodeforcesHandle, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("该 Codeforces 账号已被其他用户绑定");
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO users(id,studentId,body) VALUES(@id,@sid,@body) ON CONFLICT(id) DO UPDATE SET studentId=excluded.studentId, body=excluded.body";
        cmd.Parameters.AddWithValue("@id", user.Id); cmd.Parameters.AddWithValue("@sid", user.StudentId); cmd.Parameters.AddWithValue("@body", Json.Write(user));
        cmd.ExecuteNonQuery(); return user;
    }
    public bool DeleteUser(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM users WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id); return cmd.ExecuteNonQuery() > 0;
    }
    // —— 校内 rating 历史：一场已结算的 rated 比赛对应每个参与者一行 ——
    public void AddRating(string userId, RatingEntry entry)
    {
        var id = Guid.NewGuid().ToString("N");
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO ratings(id,userId,body) VALUES(@id,@uid,@body)";
        cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@uid", userId); cmd.Parameters.AddWithValue("@body", Json.Write(entry));
        cmd.ExecuteNonQuery();
    }
    public List<RatingEntry> Ratings(string userId)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM ratings WHERE userId=@uid ORDER BY rowid";
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader(); var list = new List<RatingEntry>(); while (r.Read()) list.Add(Json.Read<RatingEntry>(r.GetString(0))); return list;
    }
    public string SubmissionDir(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new KeyNotFoundException("提交不存在");
        return Path.Combine(Data, "submissions", id);
    }
    public Submission AddSubmission(Problem problem, string source, string compiler, string? userId = null, string? originalToolkit = null, string? originalSubmissionId = null)
    {
        if (string.IsNullOrEmpty(source) || source.Length > 1048576) throw new InvalidDataException("代码不能为空或超过 1 MiB");
        if (problem.Tests.Count == 0) throw new InvalidDataException("请先添加测试数据");
        if (IsPostgres) return AddRemoteSubmission(problem, source, compiler, userId, originalToolkit, originalSubmissionId);
        var id = Guid.NewGuid().ToString("N"); var dir = SubmissionDir(id); Directory.CreateDirectory(dir);
        var toolkit = Path.Combine(dir, "toolkit"); Directory.CreateDirectory(toolkit);
        foreach (var path in Directory.GetFiles(originalToolkit ?? Path.Combine(Root, "toolkit"))) File.Copy(path, Path.Combine(toolkit, Path.GetFileName(path)));
        File.WriteAllText(Path.Combine(dir, "source.cpp"), source);
        File.WriteAllText(Path.Combine(dir, "request.json"), Json.Write(new JudgeRequest(id, problem, source, compiler, toolkit, Path.Combine(dir, "work"))));
        File.WriteAllText(Path.Combine(dir, "result.json"), Json.Write(new JudgeResult()));
        var s = new Submission(id, problem.Id, problem.Title, problem.Mode, "Queued", DateTimeOffset.UtcNow, userId);
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO submissions(id,problemId,title,mode,status,createdAt,userId) VALUES(@id,@pid,@title,@mode,@status,@date,@uid)";
        cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@pid", s.ProblemId); cmd.Parameters.AddWithValue("@title", s.Title); cmd.Parameters.AddWithValue("@mode", s.Mode.ToString()); cmd.Parameters.AddWithValue("@status", s.Status); cmd.Parameters.AddWithValue("@date", s.CreatedAt.ToString("O")); cmd.Parameters.AddWithValue("@uid", (object?)userId ?? DBNull.Value); cmd.ExecuteNonQuery(); return s;
    }
    private const string SubmissionColumns = "id, problemId, title, mode, status, createdAt, userId";
    private static Submission ReadSubmission(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), Enum.Parse<JudgeMode>(r.GetString(3)), r.GetString(4), DateTimeOffset.Parse(r.GetString(5)), r.IsDBNull(6) ? null : r.GetString(6));
    public List<Submission> Submissions()
        => QuerySubmissions();
    public List<Submission> AllSubmissions()
        => QuerySubmissions(limit: null);
    public List<Submission> QuerySubmissions(string? userId = null, string? status = null, string? problemId = null, int? limit = 200)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        List<string> clauses = [];
        if (userId != null) { clauses.Add("userId=@user"); cmd.Parameters.AddWithValue("@user",userId); }
        if (!string.IsNullOrEmpty(status)) { clauses.Add("status=@status"); cmd.Parameters.AddWithValue("@status",status); }
        if (!string.IsNullOrEmpty(problemId)) { clauses.Add("problemId=@problem"); cmd.Parameters.AddWithValue("@problem",problemId); }
        cmd.CommandText = $"SELECT {SubmissionColumns} FROM submissions" + (clauses.Count>0 ? " WHERE "+string.Join(" AND ",clauses) : "") + " ORDER BY createdAt DESC";
        if (limit.HasValue) { cmd.CommandText += " LIMIT @limit"; cmd.Parameters.AddWithValue("@limit",Math.Clamp(limit.Value,1,500)); }
        using var r = cmd.ExecuteReader(); List<Submission> list = []; while (r.Read()) list.Add(ReadSubmission(r)); return list;
    }
    public void DeleteSubmission(string id)
    {
        if (IsPostgres)
        {
            using var remoteDb = Open(); using var tx = remoteDb.BeginTransaction(); using var remove = remoteDb.CreateCommand(); remove.Transaction = tx;
            remove.CommandText = "DELETE FROM submissions WHERE id=@id; DELETE FROM remote_jobs WHERE id=@id"; remove.Parameters.AddWithValue("@id", id); remove.ExecuteNonQuery(); tx.Commit(); return;
        }
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM submissions WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery();
    }
    public Submission? Submission(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT {SubmissionColumns} FROM submissions WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader(); return r.Read() ? ReadSubmission(r) : null;
    }
    public void Status(string id, string status)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE submissions SET status=@status WHERE id=@id"; cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@status", status); cmd.ExecuteNonQuery();
    }
    public JudgeResult Result(string id)
    {
        var s = Submission(id) ?? throw new KeyNotFoundException("提交不存在");
        JudgeResult r;
        if (IsPostgres) r = RemoteJudgeResult(id);
        else
        {
            using var file = new FileStream(Path.Combine(SubmissionDir(id), "result.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file); r = Json.Read<JudgeResult>(reader.ReadToEnd());
        }
        if (s.Status == "Interrupted") { r.Status = "Interrupted"; r.Message = IsPostgres ? "评测端连接中断，请重新评测" : "服务重启时评测未完成，请重新评测"; }
        if (s.Status == "Cancelled") { r.Status = "Cancelled"; r.Message = "评测已取消"; }
        return r;
    }
}

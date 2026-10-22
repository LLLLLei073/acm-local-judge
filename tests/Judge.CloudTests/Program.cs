using Judge.Core;
using Judge.Server;
using Npgsql;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

var connection = Environment.GetEnvironmentVariable("ACM_TEST_POSTGRES") ?? throw new InvalidOperationException("ACM_TEST_POSTGRES is required; this suite uses a dedicated deployment test database");
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var data = Path.Combine(root, "artifacts", "cloud-tests", Guid.NewGuid().ToString("N"));
var store = new Store(root, data, connection);
var ids = new List<string>(); string? problemId = null; string? userId = null; int count = 0;
void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); count++; }
try
{
    var problem = store.Save(Examples.All()[0] with { Title = "Cloud acceptance " + Guid.NewGuid().ToString("N") }, true); problemId = problem.Id;
    Check(store.Problem(problem.Id)?.Title == problem.Title, "PostgreSQL problem write/read");
    var user = store.SaveUser(new User { StudentId = Random.Shared.NextInt64(80000000000, 89999999999).ToString(), Name = "Cloud acceptance", Email = Guid.NewGuid().ToString("N") + "@example.test", PasswordHash = PasswordHasher.Hash("cloud-test-123456") }, true); userId = user.Id;
    Check(store.UserByStudentId(user.StudentId)?.Id == user.Id, "PostgreSQL account lookup");
    var submission = store.AddSubmission(problem, "int main(){}", "compiler", user.Id); ids.Add(submission.Id);
    Check(!Directory.Exists(store.SubmissionDir(submission.Id)), "cloud submission does not depend on local directory");
    Check(store.Request(submission.Id).Problem.Tests.Count == problem.Tests.Count, "compressed immutable snapshot retains tests");
    Check(store.SnapshotToolkit(submission.Id).ContainsKey("testlib.h"), "toolkit snapshot retained");
    var restart = new Store(root, Path.Combine(data, "restart"), connection);
    Check(restart.Submission(submission.Id)?.Status == "Queued", "server restart preserves pending work");
    Check(restart.Request(submission.Id).Source == "int main(){}", "fresh ephemeral directory can read source");
    var claim = store.ClaimRemote() ?? throw new Exception("no claim");
    // This database is newly provisioned; do not run this suite alongside live agents/users.
    Check(claim.Id == submission.Id, "atomic claim of pending job");
    Check(!store.HeartbeatRemote(claim.Id, new("wrong-lease")), "invalid lease cannot publish results");
    Check(store.HeartbeatRemote(claim.Id, new(claim.Lease, new(new JudgeResult { Status = "Running" }))), "heartbeat refreshes live lease");
    Check(store.Submission(claim.Id)?.Status == "Running", "progress updates submission status");
    Check(store.HeartbeatRemote(claim.Id, new(claim.Lease, new(new JudgeResult { Status = "AC", Message = "persistent result" }), true)), "completion accepted");
    Check(new Store(root, Path.Combine(data, "restart-again"), connection).Result(claim.Id).Message == "persistent result", "result survives loss of all local files");
    Check(!store.HeartbeatRemote(claim.Id, new(claim.Lease, new(new JudgeResult { Status = "WA" }), true)), "terminal lease cannot overwrite verdict");
    var cancel = store.AddSubmission(problem, "int main(){}", "compiler"); ids.Add(cancel.Id);
    var cancelClaim = store.ClaimRemote()!; store.CancelRemote(cancel.Id);
    Check(!store.HeartbeatRemote(cancel.Id, new(cancelClaim.Lease)), "cancel revokes active lease");
    Check(store.Result(cancel.Id).Status == "Cancelled", "cancelled status survives stored snapshot");
    var expired = store.AddSubmission(problem, "int main(){}", "compiler"); ids.Add(expired.Id); var expiredClaim = store.ClaimRemote()!;
    using (var db = new NpgsqlConnection(connection))
    {
        db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE acm.remote_jobs SET leaseuntil=now()-interval '1 second' WHERE id=@id"; cmd.Parameters.AddWithValue("id", expired.Id); cmd.ExecuteNonQuery();
    }
    store.ExpireRemote();
    Check(store.Result(expired.Id).Status == "Interrupted", "disconnected worker ends with Interrupted");
    Check(!store.HeartbeatRemote(expired.Id, new(expiredClaim.Lease)), "expired worker cannot return stale result");
    using (var db = new NpgsqlConnection(connection))
    {
        db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM auth.users";
        try { cmd.ExecuteScalar(); throw new Exception("backend role can read Supabase Auth"); }
        catch (PostgresException ex) when (ex.SqlState == "42501") { Check(true, "backend database role cannot read Supabase Auth"); }
    }
    Check(!File.Exists(Path.Combine(data, "judge.db")), "cloud mode never creates SQLite database");
    if (Environment.GetEnvironmentVariable("ACM_CLOUD_E2E") == "1")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = $"http://127.0.0.1:{port}";
        const string access = "cloud-acceptance-admin-token", worker = "cloud-acceptance-worker-token";
        Process Start(string dll, Dictionary<string,string> settings)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            start.ArgumentList.Add(Path.Combine(root, dll));
            foreach (var (key,value) in settings) start.Environment[key] = value;
            var process = Process.Start(start)!;
            process.OutputDataReceived += (_,e) => { if (e.Data != null && !e.Data.Contains("令牌")) Console.WriteLine(e.Data); };
            process.ErrorDataReceived += (_,e) => { if(e.Data != null) Console.WriteLine(e.Data); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
        }
        using var server = Start("artifacts/cloud-server/Judge.Server.dll", new() { ["ACM_ROOT"] = root, ["ACM_DATA"] = Path.Combine(data,"server"), ["ACM_POSTGRES"] = connection, ["ACM_HOST"] = "127.0.0.1", ["ACM_PORT"] = port.ToString(), ["ACM_TOKEN"] = access, ["ACM_SESSION_SECRET"] = "cloud-acceptance-session-secret", ["ACM_WORKER_TOKEN"] = worker, ["ACM_REGISTRATION_CODE"] = "cloud-acceptance-invite" });
        Process? agent = null; List<string> extraProblems = [];
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", access);
        try
        {
            for (int attempt=0;;attempt++)
            {
                try { using var health=await http.GetAsync("/healthz"); if(health.IsSuccessStatusCode) break; } catch(HttpRequestException) { }
                if(attempt>40 || server.HasExited) throw new Exception("cloud server startup failed"); await Task.Delay(500);
            }
            using (var denied=await http.PostAsync("/api/worker/claim",null)) Check(denied.StatusCode==HttpStatusCode.Unauthorized,"admin token cannot impersonate worker");
            using (var denied=await http.PostAsJsonAsync("/api/users/register",new { studentId="91234444",name="Invite test",email="invite@example.test",password="cloud-test-123" })) Check(denied.StatusCode==HttpStatusCode.Forbidden,"cloud registration requires invitation");
            agent = Start("artifacts/agent/Judge.Agent.dll", new() { ["ACM_CLOUD_URL"] = url, ["ACM_WORKER_TOKEN"] = worker });
            foreach (var template in Examples.All())
            {
                var p = store.Save(template with {Title="Cloud engine acceptance "+template.Mode},true); extraProblems.Add(p.Id);
                using var submitted=await http.PostAsJsonAsync("/api/submissions",new {problemId=p.Id,source=p.ExampleSource}); submitted.EnsureSuccessStatusCode();
                var row=(await submitted.Content.ReadFromJsonAsync<Submission>(Json.Options))!; ids.Add(row.Id);
                for(int attempt=0;JudgeQueue.IsActive(store.Submission(row.Id)!.Status);attempt++)
                { if(attempt>180) throw new Exception("remote engine timeout"); await Task.Delay(1000); }
                var result=store.Result(row.Id);
                Check(result.Status=="AC",$"remote {p.Mode} real compilation and execution ({result.Status})");
                Check(result.Cases.All(c=>c.Instances.Count>0 && c.CpuMs==c.Instances.Max(i=>i.Run.Usage.CpuMs) && c.MemoryBytes==c.Instances.Max(i=>i.Run.Usage.MemoryBytes)), $"remote {p.Mode} resource accounting");
                using var detail=await http.GetAsync($"/api/submissions/{row.Id}"); Check(detail.IsSuccessStatusCode,"HTTP detail reads remote snapshot");
            }
            using var brute=await http.PostAsJsonAsync($"/api/problems/{problem.Id}/brute",new {source=problem.ExampleSource}); Check(brute.IsSuccessStatusCode,"remote authoring brute");
            using var harden=await http.PostAsJsonAsync($"/api/problems/{problem.Id}/harden",new {tests=new[]{new {input="23 41\n"}}}); Check(harden.IsSuccessStatusCode,"remote authoring harden");
            Check(store.Problem(problem.Id)!.Tests.Last().Answer.Trim()=="64","remote harden stores generated answer");
            async Task<Submission> Submit(string source)
            {
                using var response=await http.PostAsJsonAsync("/api/submissions",new {problemId=problem.Id,source}); response.EnsureSuccessStatusCode();
                var submission=(await response.Content.ReadFromJsonAsync<Submission>(Json.Options))!; ids.Add(submission.Id); return submission;
            }
            async Task WaitFor(string id, Func<string,bool> ready)
            {
                for(int attempt=0;!ready(store.Submission(id)!.Status);attempt++)
                { if(attempt>120) throw new Exception("remote state timeout"); await Task.Delay(1000); }
            }
            var broken=await Submit("this is not C++"); await WaitFor(broken.Id,s=>!JudgeQueue.IsActive(s));
            Check(store.Result(broken.Id).Status=="CE","remote compiler error preserved");
            store.Save(problem with {Limits=new Limits {CpuMs=60000,WallMs=90000}},false);
            var looping=await Submit("#include <windows.h>\nint main(){for(;;) Sleep(10);}");
            await WaitFor(looping.Id,s=>s=="Running");
            using var cancelled=await http.PostAsync($"/api/submissions/{looping.Id}/cancel",null); cancelled.EnsureSuccessStatusCode();
            await Task.Delay(8000);
            Check(store.Result(looping.Id).Status=="Cancelled","real active agent cancellation remains terminal");
            var afterCancel=await Submit(problem.ExampleSource); await WaitFor(afterCancel.Id,s=>!JudgeQueue.IsActive(s));
            Check(store.Result(afterCancel.Id).Status=="AC","agent cleans up cancelled execution and accepts next job");
        }
        finally
        {
            if(agent != null) { if(!agent.HasExited) agent.Kill(true); await agent.WaitForExitAsync(); agent.Dispose(); }
            if(!server.HasExited) server.Kill(true); await server.WaitForExitAsync();
            foreach(var id in extraProblems) store.Delete(id);
        }
    }
    Console.WriteLine($"{count} cloud checks passed");
}
finally
{
    foreach (var id in ids) store.DeleteSubmission(id);
    if (problemId != null) store.Delete(problemId);
    if (userId != null) store.DeleteUser(userId);
}

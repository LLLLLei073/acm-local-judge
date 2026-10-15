using System.Text.Json;
using System.Text.Json.Serialization;
using Judge.Core;
using Judge.Server;

var root = Path.GetFullPath(Environment.GetEnvironmentVariable("ACM_ROOT") ?? Directory.GetCurrentDirectory());
var data = Path.GetFullPath(Environment.GetEnvironmentVariable("ACM_DATA") ?? Path.Combine(root, "data"));
Directory.CreateDirectory(data);
using var instanceLock = new FileStream(Path.Combine(data, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
int port = int.TryParse(Environment.GetEnvironmentVariable("ACM_PORT"), out var configuredPort) ? configuredPort : 27123;
if (port is < 1024 or > 65535) throw new InvalidDataException("ACM_PORT 必须是 1024–65535");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = Path.Combine(root, "web", "dist") });
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 90 * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = Packages.MaxBytes);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(new Store(root, data)); builder.Services.AddSingleton<JudgeQueue>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<JudgeQueue>());
var app = builder.Build();
app.Use(async (ctx, next) =>
{
    // Local host/origin checks stop arbitrary websites from submitting native code to the local service.
    if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 403; return; }
    if (ctx.Request.Headers.Origin is { Count: > 0 } origins && !origins.All(o => o == $"http://127.0.0.1:{port}" || o == $"http://localhost:{port}" || o == "http://127.0.0.1:5173")) { ctx.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or KeyNotFoundException)
    {
        if (ctx.Response.HasStarted) throw;
        ctx.Response.StatusCode = ex is KeyNotFoundException ? 404 : 400; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/environment", (JudgeQueue q) =>
{
    string compiler = ServerSettings.Compiler(data);
    return new { app = "acm-local-judge", os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, compiler, compilerExists = File.Exists(compiler), workerReady = File.Exists(Path.Combine(root, "artifacts/worker/Judge.Worker.dll")), standard = "C++23", memoryMetric = "Windows 提交内存峰值", trustedOnly = true, parallelism = ServerSettings.Parallelism(data), judging = q.Running, queued = q.Queued };
});
app.MapPut("/api/settings", (Dictionary<string,string> values) =>
{
    if (!values.TryGetValue("compiler", out var path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请填写现有编译器 exe 的绝对路径");
    if (!values.TryGetValue("parallelism", out var raw) || !int.TryParse(raw, out var parallelism) || parallelism is < 1 or > ServerSettings.MaxParallelism) throw new InvalidDataException("并行评测数必须是 1–8 的整数");
    ServerSettings.Save(data, path, parallelism); return Results.Ok();
});
app.MapGet("/api/problems", (Store s) => s.Problems().Select(p => new { p.Id, p.Title, p.Mode, p.Limits, testCount = p.Tests.Count }));
app.MapGet("/api/problems/{id}", (string id, Store s) => s.Problem(id) ?? throw new KeyNotFoundException("题目不存在"));
app.MapPost("/api/problems", (Problem p, Store s) => s.Save(p, true));
app.MapPut("/api/problems/{id}", (string id, Problem p, Store s) => { p.Id = id; return s.Save(p, false); });
app.MapDelete("/api/problems/{id}", (string id, Store s) => s.Delete(id) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/problems/{id}/export", (string id, Store s) => Results.File(Packages.Export(s.Problem(id) ?? throw new KeyNotFoundException("题目不存在")), "application/zip", "problem-" + id + ".zip"));
app.MapPost("/api/packages/preview", async (HttpRequest req, CancellationToken ct) =>
{
    var form = await req.ReadFormAsync(ct); var file = form.Files.GetFile("file") ?? throw new InvalidDataException("请选择 ZIP");
    if (file.Length > Packages.MaxBytes) throw new InvalidDataException("题目包超过 64 MiB");
    await using var stream = file.OpenReadStream(); return await Packages.Import(stream, ct);
});
app.MapPost("/api/problems/{id}/tests", async (string id, HttpRequest req, Store s, CancellationToken ct) =>
{
    var p = s.Problem(id) ?? throw new KeyNotFoundException("题目不存在");
    var form = await req.ReadFormAsync(ct); var files = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in form.Files)
    {
        var name = Path.GetFileName(f.FileName); if (!name.EndsWith(".in", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".ans", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("只支持 .in/.ans 文件");
        using var reader = new StreamReader(f.OpenReadStream(), new System.Text.UTF8Encoding(false,true));
        if (!files.TryAdd(name, await reader.ReadToEndAsync(ct))) throw new InvalidDataException("文件重名：" + name);
    }
    var tests = new List<TestData>();
    foreach (var f in files.Keys.Where(x => x.EndsWith(".in", StringComparison.OrdinalIgnoreCase)).Order())
    {
        string answer = f[..^3] + ".ans";
        if (!files.ContainsKey(answer) && p.Mode is JudgeMode.Standard or JudgeMode.Special or JudgeMode.Communication) throw new InvalidDataException("缺少 " + answer);
        tests.Add(new(f[..^3], files[f], files.GetValueOrDefault(answer,"")));
    }
    if (files.Keys.Any(x => x.EndsWith(".ans", StringComparison.OrdinalIgnoreCase) && !files.ContainsKey(x[..^4] + ".in"))) throw new InvalidDataException("答案文件缺少对应输入");
    foreach (var t in tests) { p.Tests.RemoveAll(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)); p.Tests.Add(t); }
    return s.Save(p, false);
});
app.MapGet("/api/submissions", (Store s) => s.Submissions());
app.MapPost("/api/submissions", (SubmitBody body, Store s, JudgeQueue q) =>
{
    var p = s.Problem(body.ProblemId) ?? throw new KeyNotFoundException("题目不存在");
    var submission = s.AddSubmission(p, body.Source, ServerSettings.Compiler(data)); q.Add(submission.Id); return submission;
});
app.MapGet("/api/submissions/{id}", (string id, Store s) => new { submission = s.Submission(id) ?? throw new KeyNotFoundException("提交不存在"), result = s.Result(id), source = File.ReadAllText(Path.Combine(s.SubmissionDir(id), "source.cpp")) });
app.MapGet("/api/submissions/{id}/logs", (string id, Store s) => s.Result(id));
app.MapPost("/api/submissions/{id}/cancel", (string id, JudgeQueue q) => { q.Cancel(id); return Results.Ok(); });
app.MapPost("/api/submissions/{id}/rejudge", (string id, Store s, JudgeQueue q) =>
{
    if (s.Submission(id) == null) throw new KeyNotFoundException("提交不存在");
    var old = Json.Read<JudgeRequest>(File.ReadAllText(Path.Combine(s.SubmissionDir(id), "request.json")));
    var submission = s.AddSubmission(old.Problem, old.Source, old.Compiler, old.ToolkitPath); q.Add(submission.Id); return submission;
});
app.MapGet("/api/submissions/{id}/events", async (string id, HttpContext ctx, Store s) =>
{
    if (s.Submission(id) == null) throw new KeyNotFoundException("提交不存在");
    ctx.Response.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
    string previous = "";
    while (!ctx.RequestAborted.IsCancellationRequested)
    {
        var submission = s.Submission(id)!; var result = s.Result(id);
        string value = JsonSerializer.Serialize(new { submission, result }, Json.Compact);
        if (value != previous) { await ctx.Response.WriteAsync("data: " + value + "\n\n", ctx.RequestAborted); await ctx.Response.Body.FlushAsync(ctx.RequestAborted); previous = value; }
        if (!JudgeQueue.IsActive(submission.Status)) break;
        await Task.Delay(300, ctx.RequestAborted);
    }
});
app.MapFallbackToFile("index.html");
await app.RunAsync();
record SubmitBody(string ProblemId, string Source);

using System.Net;
using System.Security.Cryptography;
using System.Text;
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
var host = ServerSettings.NormalizeHost(Environment.GetEnvironmentVariable("ACM_HOST") ?? "127.0.0.1");
var bindHost = host.Contains(':') ? $"[{host}]" : host;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = Path.Combine(root, "web", "dist") });
builder.WebHost.UseUrls($"http://{bindHost}:{port}");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 90 * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = Packages.MaxBytes);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(new Store(root, data)); builder.Services.AddSingleton<JudgeQueue>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<JudgeQueue>()); builder.Services.AddHostedService<Retention>();
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(data, "logs")));
var app = builder.Build();
var token = ServerSettings.Token(data);
Console.WriteLine($"ACM Local Judge: http://{bindHost}:{port}");
Console.WriteLine($"API 访问令牌：{token}");
Console.WriteLine("远程访问需在登录框输入此令牌；本机回环访问免认证。");
// A reverse proxy on the same host is the expected TLS terminus; trust it for client IPs only.
app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto, KnownProxies = { IPAddress.Loopback, IPAddress.IPv6Loopback } });
app.Use(async (ctx, next) =>
{
    // Host/origin allowlist replaces the old loopback-only wall; token auth guards remote API use.
    var allowed = ServerSettings.AllowedHosts(data);
    if (!allowed.Contains(ServerSettings.NormalizeHost(ctx.Request.Host.Host), StringComparer.Ordinal)) { ctx.Response.StatusCode = 403; return; }
    if (ctx.Request.Headers.Origin is { Count: > 0 } origins)
        foreach (var origin in origins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !allowed.Contains(ServerSettings.NormalizeHost(uri.Host), StringComparer.Ordinal)) { ctx.Response.StatusCode = 403; return; }
    var remote = ctx.Connection.RemoteIpAddress;
    bool local = remote != null && IPAddress.IsLoopback(remote);
    bool authRequired = Environment.GetEnvironmentVariable("ACM_AUTH") == "required" || !local;
    var path = ctx.Request.Path;
    if (authRequired && path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth"))
    {
        string header = ctx.Request.Headers.Authorization.ToString(); // absent header converts to null via StringValues; ToString() is always safe
        string? provided = header.StartsWith("Bearer ") ? header[7..].Trim() : ctx.Request.Cookies[ServerSettings.CookieName];
        if (provided == null || !FixedTimeEquals(provided, token))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "需要访问令牌" }, ctx.RequestAborted);
            return;
        }
    }
    if (path.Value == "/api/submissions" && ctx.Request.Method == HttpMethods.Post && ServerSettings.RateLimit(data) is > 0 and var limit && !SubmitLimiter.TryAccept(remote?.ToString() ?? "?", limit))
    {
        ctx.Response.StatusCode = 429;
        await ctx.Response.WriteAsJsonAsync(new { error = "提交过于频繁，请稍后再试" }, ctx.RequestAborted);
        return;
    }
    try { await next(); }
    catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or KeyNotFoundException)
    {
        if (ctx.Response.HasStarted) throw;
        ctx.Response.StatusCode = ex is KeyNotFoundException ? 404 : 400; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
static bool FixedTimeEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/healthz", (JudgeQueue q) => Results.Ok(new { status = "ok", judging = q.Running, queued = q.Queued }));
app.MapPost("/api/auth", (AuthBody body, HttpContext ctx) =>
{
    if (body.Token == null || !FixedTimeEquals(body.Token, token)) { ctx.Response.StatusCode = 401; return Results.Json(new { error = "令牌不正确" }, statusCode: 401); }
    ctx.Response.Cookies.Append(ServerSettings.CookieName, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(30) });
    return Results.Ok();
});
app.MapGet("/api/environment", (JudgeQueue q) =>
{
    string compiler = ServerSettings.Compiler(data);
    return new { app = "acm-local-judge", os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, compiler, compilerExists = File.Exists(compiler), workerReady = File.Exists(Path.Combine(root, "artifacts/worker/Judge.Worker.dll")), standard = "C++23", memoryMetric = OperatingSystem.IsWindows() ? "Windows 提交内存峰值" : "Linux 提交内存峰值（RSS）", trustedOnly = true, parallelism = ServerSettings.Parallelism(data), judging = q.Running, queued = q.Queued };
});
app.MapPut("/api/settings", (Dictionary<string,string> values) =>
{
    if (!values.TryGetValue("compiler", out var path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) || OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请填写现有编译器的绝对路径");
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
record AuthBody(string? Token);

// Fixed-window per-IP limiter for submission floods; entries age out lazily.
internal static class SubmitLimiter
{
    private static readonly Dictionary<string, (DateTime Window, int Count)> windows = [];
    public static bool TryAccept(string key, int limitPerMinute)
    {
        lock (windows)
        {
            var now = DateTime.UtcNow;
            if (windows.Count > 10_000) foreach (var k in windows.Where(w => now - w.Value.Window >= TimeSpan.FromMinutes(1)).Select(w => w.Key).ToList()) windows.Remove(k);
            if (!windows.TryGetValue(key, out var entry) || now - entry.Window >= TimeSpan.FromMinutes(1)) { windows[key] = (now, 1); return true; }
            if (entry.Count >= limitPerMinute) return false;
            windows[key] = (entry.Window, entry.Count + 1);
            return true;
        }
    }
}

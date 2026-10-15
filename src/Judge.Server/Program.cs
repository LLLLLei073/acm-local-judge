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
Console.WriteLine("远程访问需在登录框输入此令牌；管理端 /admin 始终需要令牌。");
// Served for /admin until the visitor presents a valid token; the admin console itself never
// reaches an unauthenticated browser.
const string AdminLoginPage = """
<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>ACM 管理端 · 登录</title><style>
body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:#0e1220;font-family:system-ui,-apple-system,"Segoe UI",sans-serif;color:#e8eaf2}
.card{width:340px;padding:36px 32px;border-radius:14px;background:#171c2e;box-shadow:0 18px 50px rgba(0,0,0,.45)}
h1{font-size:19px;margin:0 0 6px}.sub{font-size:12.5px;color:#8b93ad;margin:0 0 22px;line-height:1.6}
input{width:100%;box-sizing:border-box;padding:11px 13px;border-radius:9px;border:1px solid #2c3350;background:#0e1220;color:#e8eaf2;font-size:14px;margin-bottom:14px}
button{width:100%;padding:11px;border:0;border-radius:9px;background:#3267cd;color:#fff;font-size:14px;cursor:pointer}
button:hover{background:#3d75de}.err{color:#ff7a7a;font-size:13px;min-height:18px;margin:0 0 8px}
.hint{font-size:12px;color:#5d6480;margin-top:18px;line-height:1.6}
</style></head><body><div class="card"><h1>ACM 管理端</h1><p class="sub">管理控制台需要访问令牌。</p>
<p class="err" id="msg"></p><input id="t" type="password" autocomplete="off" placeholder="访问令牌" autofocus>
<button onclick="go()">登 录</button>
<p class="hint">令牌在服务启动日志中打印，也保存在 data/settings.json。登录后本浏览器 30 天内免重复输入。</p></div>
<script>function go(){var t=document.getElementById('t').value;fetch('/api/auth',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({token:t})}).then(function(r){if(r.ok){location.href='/admin';}else{document.getElementById('msg').textContent='令牌不正确，请重试';}}).catch(function(){document.getElementById('msg').textContent='网络错误';});}
document.getElementById('t').addEventListener('keydown',function(e){if(e.key==='Enter')go();});</script></body></html>
""";
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
    var path = ctx.Request.Path;
    bool admin = IsAdminRequest(ctx.Request);
    string header = ctx.Request.Headers.Authorization.ToString();
    string? provided = header.StartsWith("Bearer ") ? header[7..].Trim() : ctx.Request.Cookies[ServerSettings.CookieName];
    bool tokenOk = provided != null && FixedTimeEquals(provided, token);
    // The admin console is a separate, always-authenticated surface: both /admin itself and every
    // management API demand the token, loopback included. Contestant endpoints follow the OJ switch.
    if (path.Value is "/admin" or "/admin/")
    {
        if (ctx.Request.Method != HttpMethods.Get) { ctx.Response.StatusCode = 405; return; }
        var adminPage = Path.Combine(root, "web", "dist", "admin.html");
        if (!tokenOk) { ctx.Response.ContentType = "text/html; charset=utf-8"; await ctx.Response.WriteAsync(AdminLoginPage, ctx.RequestAborted); return; }
        if (!File.Exists(adminPage)) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("管理端前端尚未构建：请在 web 目录执行 pnpm build", ctx.RequestAborted); return; }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(adminPage, ctx.RequestAborted);
        return;
    }
    if (path == "/admin.html") { ctx.Response.Redirect("/admin"); return; }
    bool authRequired = admin || Environment.GetEnvironmentVariable("ACM_AUTH") == "required" || !local && !ServerSettings.OjOpen(data);
    if (authRequired && path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth") && !tokenOk)
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { error = "需要访问令牌" }, ctx.RequestAborted);
        return;
    }
    ctx.Items["acmAdmin"] = tokenOk;
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

// Judge-tool management surface: always token-guarded, even on an opened OJ.
static bool IsAdminRequest(HttpRequest request)
{
    var path = request.Path.Value ?? "";
    var method = request.Method;
    if (path == "/api/problems") return method != HttpMethods.Get;
    if (path.StartsWith("/api/problems/"))
    {
        if (method is "POST" or "PUT" or "DELETE") return true;
        if (path.EndsWith("/export", StringComparison.Ordinal)) return true;
    }
    if (path.StartsWith("/api/packages")) return true;
    if (path.EndsWith("/rejudge", StringComparison.Ordinal)) return true;
    return path == "/api/settings";
}
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/healthz", (JudgeQueue q) => Results.Ok(new { status = "ok", judging = q.Running, queued = q.Queued }));
app.MapPost("/api/auth", (AuthBody body, HttpContext ctx) =>
{
    if (body.Token == null || !FixedTimeEquals(body.Token, token)) { ctx.Response.StatusCode = 401; return Results.Json(new { error = "令牌不正确" }, statusCode: 401); }
    ctx.Response.Cookies.Append(ServerSettings.CookieName, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(30) });
    return Results.Ok();
});
app.MapGet("/api/environment", (JudgeQueue q, HttpContext ctx) =>
{
    string compiler = ServerSettings.Compiler(data);
    bool admin = ctx.Items["acmAdmin"] is true;
    return new { app = "acm-local-judge", os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, compiler = admin ? compiler : "", compilerExists = admin && File.Exists(compiler), workerReady = File.Exists(Path.Combine(root, "artifacts/worker/Judge.Worker.dll")), standard = "C++23", memoryMetric = OperatingSystem.IsWindows() ? "Windows 提交内存峰值" : "Linux 提交内存峰值（RSS）", trustedOnly = true, parallelism = ServerSettings.Parallelism(data), judging = q.Running, queued = q.Queued };
});
app.MapPut("/api/settings", (Dictionary<string,string> values) =>
{
    if (!values.TryGetValue("compiler", out var path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) || OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请填写现有编译器的绝对路径");
    if (!values.TryGetValue("parallelism", out var raw) || !int.TryParse(raw, out var parallelism) || parallelism is < 1 or > ServerSettings.MaxParallelism) throw new InvalidDataException("并行评测数必须是 1–8 的整数");
    ServerSettings.Save(data, path, parallelism); return Results.Ok();
});
app.MapGet("/api/problems", (Store s) => s.Problems().Select(p => new { p.Id, p.Title, p.Mode, p.Limits, testCount = p.Tests.Count }));
app.MapGet("/api/problems/{id}", (string id, Store s, HttpContext ctx) =>
{
    var p = s.Problem(id) ?? throw new KeyNotFoundException("题目不存在");
    return ctx.Items["acmAdmin"] is true ? p : PublicViews.Problem(p);
});
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

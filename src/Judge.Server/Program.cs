using System.Diagnostics;
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
var postgresConnection = Environment.GetEnvironmentVariable("ACM_POSTGRES");
bool cloud = !string.IsNullOrWhiteSpace(postgresConnection);
if (cloud)
    foreach (var key in new[] { "ACM_TOKEN", "ACM_SESSION_SECRET", "ACM_WORKER_TOKEN", "ACM_REGISTRATION_CODE" })
        if ((Environment.GetEnvironmentVariable(key)?.Length ?? 0) < 16) throw new InvalidDataException($"云端部署必须设置至少 16 位的 {key}");
int port = int.TryParse(Environment.GetEnvironmentVariable("ACM_PORT") ?? Environment.GetEnvironmentVariable("PORT"), out var configuredPort) ? configuredPort : 27123;
if (port is < 1024 or > 65535) throw new InvalidDataException("ACM_PORT 必须是 1024–65535");
var host = ServerSettings.NormalizeHost(Environment.GetEnvironmentVariable("ACM_HOST") ?? "127.0.0.1");
var bindHost = host.Contains(':') ? $"[{host}]" : host;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = Path.Combine(root, "web", "dist") });
builder.WebHost.UseUrls($"http://{bindHost}:{port}");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 90 * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = Packages.MaxBytes);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var configuredStore = new Store(root, data, postgresConnection);
if (cloud) ServerSettings.ConfigureCloud(configuredStore);
builder.Services.AddSingleton(configuredStore); builder.Services.AddSingleton<JudgeQueue>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<JudgeQueue>()); builder.Services.AddHostedService<Retention>();
builder.Services.AddSingleton<CodeforcesSync>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<CodeforcesSync>());
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(data, "logs")));
var app = builder.Build();
var token = ServerSettings.Token(data);
var sessionSecret = ServerSettings.SessionSecret(data);
var store = app.Services.GetRequiredService<Store>();
// Hashing a decoy password once at startup keeps login timing flat whether or not a student id exists.
var dummyHash = PasswordHasher.Hash("acm-decoy-password");
Console.WriteLine($"ACM Local Judge: http://{bindHost}:{port}");
if (!cloud) Console.WriteLine($"API 访问令牌：{token}");
Console.WriteLine("管理端 /admin 与管理接口使用此令牌；选手在 OJ 页面注册登录后使用。");
// Served for /admin until the visitor presents a valid token; the admin console itself never
// reaches an unauthenticated browser.
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
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api/worker"))
    {
        var workerToken = Environment.GetEnvironmentVariable("ACM_WORKER_TOKEN");
        var authorization = ctx.Request.Headers.Authorization.ToString();
        if (!cloud || string.IsNullOrEmpty(workerToken) || !authorization.StartsWith("Bearer ") || !FixedTimeEquals(authorization[7..], workerToken))
        { ctx.Response.StatusCode = 401; return; }
        await next(); return;
    }
    bool admin = IsAdminRequest(ctx.Request);
    string header = ctx.Request.Headers.Authorization.ToString();
    string? provided = header.StartsWith("Bearer ") ? header[7..].Trim() : ctx.Request.Cookies[ServerSettings.CookieName];
    bool tokenOk = provided != null && FixedTimeEquals(provided, token);
    // Contestant identity: verify the signed session cookie, then load the account fresh so
    // deletions and profile changes apply on the very next request.
    User? user = null;
    if (ctx.Request.Cookies.TryGetValue(UserSessions.CookieName, out var session) && UserSessions.Resolve(sessionSecret, session) is { } userId)
        user = store.User(userId);
    ctx.Items["acmUser"] = user;
    // The admin console is a separate, always-token-guarded surface: both /admin itself and every
    // management API demand the token, loopback included. The OJ requires a signed-in user instead.
    if (path.Value is "/admin" or "/admin/")
    {
        if (ctx.Request.Method != HttpMethods.Get) { ctx.Response.StatusCode = 405; return; }
        var adminPage = Path.Combine(root, "web", "dist", "admin.html");
        if (!tokenOk) { ctx.Response.ContentType = "text/html; charset=utf-8"; await ctx.Response.WriteAsync(AdminGate.Html, ctx.RequestAborted); return; }
        if (!File.Exists(adminPage)) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("管理端前端尚未构建：请在 web 目录执行 pnpm build", ctx.RequestAborted); return; }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(adminPage, ctx.RequestAborted);
        return;
    }
    if (path == "/admin.html") { ctx.Response.Redirect("/admin"); return; }
    // Register/login/logout and the anonymous me-probe are open; everything else under /api needs a
    // user session (or the admin token, which also grants contestant access).
    bool open = path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/users/register") || path.StartsWithSegments("/api/users/login") || path.StartsWithSegments("/api/users/logout") || path.Value == "/api/users/me" && HttpMethods.IsGet(ctx.Request.Method);
    if (path.StartsWithSegments("/api") && admin && !tokenOk)
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { error = "需要访问令牌" }, ctx.RequestAborted);
        return;
    }
    if (path.StartsWithSegments("/api") && !admin && !open && !tokenOk && user == null)
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { error = "请先登录" }, ctx.RequestAborted);
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
    if (path.StartsWith("/api/contests")) return method != "GET";
    if (path == "/api/settings") return true;
    // User administration (list / delete accounts) is token-only; self-service user paths are not.
    if (path == "/api/users") return true;
    if (path.StartsWith("/api/users/"))
    {
        var rest = path["/api/users/".Length..];
        if (rest == "me" || rest.StartsWith("me/", StringComparison.Ordinal) || rest is "register" or "login" or "logout") return false;
        return true;
    }
    return false;
}
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/healthz", (JudgeQueue q) => Results.Ok(new { app = "acm-local-judge", status = "ok", judging = q.Running, queued = q.Queued }));
app.MapPost("/api/auth", (AuthBody body, HttpContext ctx) =>
{
    if (body.Token == null || !FixedTimeEquals(body.Token, token)) { ctx.Response.StatusCode = 401; return Results.Json(new { error = "令牌不正确" }, statusCode: 401); }
    ctx.Response.Cookies.Append(ServerSettings.CookieName, token, new CookieOptions { HttpOnly = true, Secure = cloud, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(30) });
    return Results.Ok();
});
// —— 用户系统：注册 / 登录 / 个人设置 / Codeforces 绑定 / 校内排行榜 ——
static User CurrentUser(HttpContext ctx) => ctx.Items["acmUser"] as User ?? throw new InvalidDataException("请先登录");
void IssueSession(HttpContext ctx, string secret, User user) => ctx.Response.Cookies.Append(UserSessions.CookieName, UserSessions.Issue(secret, user.Id), new CookieOptions { HttpOnly = true, Secure = cloud, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(UserSessions.LifetimeDays) });
app.MapPost("/api/users/register", (RegisterBody body, Store s, HttpContext ctx) =>
{
    if (cloud && !FixedTimeEquals(body.InviteCode ?? "", Environment.GetEnvironmentVariable("ACM_REGISTRATION_CODE")!))
        return Results.Json(new { error = "请输入管理员提供的注册邀请码" }, statusCode: 403);
    var user = new User { StudentId = (body.StudentId ?? "").Trim(), Name = (body.Name ?? "").Trim(), Email = (body.Email ?? "").Trim().ToLowerInvariant() };
    User.ValidatePassword(body.Password ?? "");
    user.PasswordHash = PasswordHasher.Hash(body.Password ?? "");
    s.SaveUser(user, true);
    IssueSession(ctx, sessionSecret, user);
    return Results.Ok(new { user = PublicViews.User(user) });
});
app.MapPost("/api/users/login", (LoginBody body, Store s, HttpContext ctx) =>
{
    var user = s.UserByStudentId((body.StudentId ?? "").Trim());
    if (user == null) PasswordHasher.Verify(body.Password ?? "", dummyHash); // 抹平“学号是否存在”的时序差
    if (user == null || !PasswordHasher.Verify(body.Password ?? "", user.PasswordHash)) return Results.Json(new { error = "学号或密码不正确" }, statusCode: 401);
    IssueSession(ctx, sessionSecret, user);
    return Results.Ok(new { user = PublicViews.User(user) });
});
app.MapPost("/api/users/logout", (HttpContext ctx) => { ctx.Response.Cookies.Delete(UserSessions.CookieName); return Results.Ok(); });
app.MapGet("/api/users/me", (HttpContext ctx) => new { user = PublicViews.User(ctx.Items["acmUser"] as User) });
app.MapPut("/api/users/me", (ProfileBody body, Store s, HttpContext ctx) =>
{
    var user = CurrentUser(ctx);
    if (body.Name != null) user.Name = body.Name.Trim();
    if (body.Email != null) user.Email = body.Email.Trim().ToLowerInvariant();
    if (body.Anonymous.HasValue) user.Anonymous = body.Anonymous.Value;
    s.SaveUser(user, false);
    return new { user = PublicViews.User(user) };
});
app.MapPost("/api/users/me/password", (PasswordBody body, Store s, HttpContext ctx) =>
{
    var user = CurrentUser(ctx);
    if (!PasswordHasher.Verify(body.CurrentPassword ?? "", user.PasswordHash)) throw new InvalidDataException("当前密码不正确");
    User.ValidatePassword(body.NewPassword ?? "");
    user.PasswordHash = PasswordHasher.Hash(body.NewPassword ?? "");
    s.SaveUser(user, false);
    return Results.Ok();
});
app.MapPost("/api/users/me/codeforces", (CfBody body, Store s, HttpContext ctx) =>
{
    var user = CurrentUser(ctx);
    var handle = (body.Handle ?? "").Trim();
    if (!User.IsValidCodeforcesHandle(handle)) throw new InvalidDataException("Codeforces handle 应为 1–24 位字母、数字或下划线");
    if (user.CodeforcesHandle.Equals(handle, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("你已绑定该 Codeforces 账号");
    if (s.Users().Any(u => u.Id != user.Id && u.CodeforcesHandle.Equals(handle, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("该 Codeforces 账号已被其他用户绑定");
    return new { code = Codeforces.IssueChallenge(user.Id, handle), minutes = Codeforces.ChallengeMinutes };
});
app.MapGet("/api/users/me/codeforces/challenge", (HttpContext ctx) => new { challenge = Codeforces.PendingChallenge(CurrentUser(ctx).Id) });
app.MapPost("/api/users/me/codeforces/verify", async (CfVerifyBody body, Store s, CodeforcesSync sync, HttpContext ctx, CancellationToken ct) =>
{
    var user = CurrentUser(ctx);
    var (handle, expected) = Codeforces.TakeChallenge(user.Id);
    if (!string.Equals((body.Code ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("验证码不正确");
    var info = (await Codeforces.FetchAsync([handle], ct, fresh: true)).FirstOrDefault(i => i.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("Codeforces 上未找到该 handle");
    if (Codeforces.TakeChallenge(user.Id) != (handle, expected)) throw new InvalidDataException("验证码已更新，请使用当前验证码");
    if (!string.Equals((info.LastName ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { status = "pending", retryAfterSeconds = 10, message = "CF 接口尚未返回此验证码。请确认已保存 Last name；若已保存，请保持验证码不变，等待自动重试。", challenge = Codeforces.PendingChallenge(user.Id) }, statusCode: 202);
    user = s.User(user.Id) ?? throw new InvalidDataException("账号不存在");
    user.CodeforcesHandle = info.Handle; user.CodeforcesRating = info.Rating; user.CodeforcesRank = info.Rank; user.RatingUpdatedAt = DateTimeOffset.UtcNow;
    user.CodeforcesBindingVersion = Guid.NewGuid().ToString("N");
    s.SaveUser(user, false);
    Codeforces.ClearChallenge(user.Id);
    sync.Enqueue(user);
    return Results.Ok(new { status = "verified", user = PublicViews.User(user) });
});
app.MapDelete("/api/users/me/codeforces", (Store s, HttpContext ctx) =>
{
    var user = CurrentUser(ctx);
    user.CodeforcesHandle = ""; user.CodeforcesRating = null; user.CodeforcesRank = ""; user.RatingUpdatedAt = null;
    user.CodeforcesBindingVersion = Guid.NewGuid().ToString("N");
    s.SaveUser(user, false);
    s.ClearCfHistory(user.Id); Codeforces.ClearChallenge(user.Id);
    return new { user = PublicViews.User(user) };
});
app.MapPost("/api/users/me/codeforces/sync", (CodeforcesSync sync, HttpContext ctx) => Results.Accepted(value: sync.Enqueue(CurrentUser(ctx))));
app.MapGet("/api/users/me/codeforces/history", (Store s, CodeforcesSync sync, HttpContext ctx, int? page, string? view) =>
{
    var user = CurrentUser(ctx);
    var history = s.CfHistory(user);
    var submissions = history?.Submissions ?? [];
    var solved = submissions.Where(s => s.Verdict == "OK").DistinctBy(s => s.Problem.Key).ToList();
    var rows = view == "solved" ? solved : submissions;
    var pageCount = Math.Max(1, (rows.Count + 49) / 50);
    var currentPage = Math.Clamp(page ?? 1, 1, pageCount);
    ctx.Response.Headers.CacheControl = "no-store";
    return new { handle = user.CodeforcesHandle, updatedAt = history?.UpdatedAt, sync = sync.State(user), submissionCount = submissions.Count, solvedCount = solved.Count,
        page = currentPage, pageCount, items = rows.Skip((currentPage - 1) * 50).Take(50), user = PublicViews.User(user) };
});
app.MapGet("/api/leaderboard", (Store s, CodeforcesSync sync) =>
{
    var users = s.Users();
    // Never hold the leaderboard request open while CF is unreachable. The background job updates
    // rating and history together and protects the current account against stale network responses.
    var stale = users.Where(u => u.CodeforcesHandle != "" && (u.RatingUpdatedAt == null || DateTimeOffset.UtcNow - u.RatingUpdatedAt > TimeSpan.FromHours(1))).ToList();
    foreach (var user in stale) sync.Enqueue(user, automatic: true);
    var stats = new Dictionary<string, (HashSet<string> Problems, int Submissions, DateTimeOffset? LastAc)>();
    foreach (var sub in s.AllSubmissions())
    {
        if (sub.UserId == null) continue;
        if (!stats.TryGetValue(sub.UserId, out var stat)) { stat.Problems = []; stat.Submissions = 0; stat.LastAc = null; }
        stat.Submissions++;
        if (sub.Status == "AC") { stat.Problems.Add(sub.ProblemId); if (stat.LastAc == null || sub.CreatedAt > stat.LastAc) stat.LastAc = sub.CreatedAt; }
        stats[sub.UserId] = stat;
    }
    // Anonymous entries keep their rank but lose name / student id / CF handle everywhere.
    (string? Name, string? StudentId, string Handle, int Solved, int Submissions, DateTimeOffset? LastAc) Fields(User u)
    {
        (HashSet<string> Problems, int Submissions, DateTimeOffset? LastAc) stat = stats.TryGetValue(u.Id, out var value) ? value : (new HashSet<string>(), 0, null);
        return (u.Anonymous ? null : u.Name, u.Anonymous ? null : u.StudentId, u.Anonymous ? "" : u.CodeforcesHandle, stat.Problems.Count, stat.Submissions, stat.LastAc);
    }
    var cf = users.Where(u => u.CodeforcesHandle != "")
        .OrderByDescending(u => u.CodeforcesRating ?? -1).ThenBy(u => u.Name)
        .Select((u, i) => new { rank = i + 1, userId = u.Id, f = Fields(u), cfRating = u.CodeforcesRating, cfRank = u.CodeforcesRank, ratingUpdatedAt = u.RatingUpdatedAt })
        .Select(x => new { x.rank, x.userId, x.cfRating, x.cfRank, x.ratingUpdatedAt, solvedCount = x.f.Solved, submissionCount = x.f.Submissions, lastAcAt = x.f.LastAc, name = x.f.Name ?? "", studentId = x.f.StudentId ?? "", anonymous = x.f.Name == null, cfHandle = x.f.Handle }).ToList();
    var solved = users.Select(u =>
        {
            (HashSet<string> Problems, int Submissions, DateTimeOffset? LastAc) stat = stats.TryGetValue(u.Id, out var value) ? value : (new HashSet<string>(), 0, null);
            return (User: u, Stat: stat);
        })
        .OrderByDescending(x => x.Stat.Problems.Count).ThenBy(x => x.Stat.Submissions).ThenBy(x => x.Stat.LastAc ?? DateTimeOffset.MaxValue)
        .Select((x, i) => new { rank = i + 1, x.User.Id, f = Fields(x.User), cfRating = x.User.CodeforcesRating, cfRank = x.User.CodeforcesRank, ratingUpdatedAt = x.User.RatingUpdatedAt })
        .Select(x => new { x.rank, userId = x.Id, x.cfRating, x.cfRank, x.ratingUpdatedAt, solvedCount = x.f.Solved, submissionCount = x.f.Submissions, lastAcAt = x.f.LastAc, name = x.f.Name ?? "", studentId = x.f.StudentId ?? "", anonymous = x.f.Name == null, cfHandle = x.f.Handle }).ToList();
    // 校内 rating 榜：只在 rated 比赛结算后上榜；匿名选手保留名次但隐藏身份。
    var site = users.Where(u => u.SiteRating != null)
        .OrderByDescending(u => u.SiteRating).ThenBy(u => u.Name)
        .Select((u, i) => new { rank = i + 1, userId = u.Id, f = Fields(u), siteRating = u.SiteRating, contestsPlayed = s.Ratings(u.Id).Count })
        .Select(x => new { x.rank, x.userId, x.siteRating, x.contestsPlayed, name = x.f.Name ?? "", studentId = x.f.StudentId ?? "", anonymous = x.f.Name == null }).ToList();
    return new { cf, solved, site };
});
app.MapGet("/api/users", (Store s) => s.Users().Select(u => new { u.Id, u.StudentId, u.Name, u.Email, cfHandle = u.CodeforcesHandle, cfRating = u.CodeforcesRating, cfRank = u.CodeforcesRank, siteRating = u.SiteRating, u.Anonymous, u.CreatedAt }));
app.MapDelete("/api/users/{id}", (string id, Store s) => s.DeleteUser(id) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/environment", (JudgeQueue q, HttpContext ctx) =>
{
    string compiler = ServerSettings.Compiler(data);
    bool admin = ctx.Items["acmAdmin"] is true;
    return new { app = "acm-local-judge", os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, compiler = admin ? compiler : "", compilerExists = admin && !cloud && File.Exists(compiler), workerReady = cloud ? store.RemoteWorkerReady : File.Exists(Path.Combine(root, "artifacts/worker/Judge.Worker.dll")), remoteWorker = cloud, standard = "C++23", memoryMetric = cloud ? "由评测端记录（Windows 提交内存峰值）" : OperatingSystem.IsWindows() ? "Windows 提交内存峰值" : "Linux RSS 峰值", trustedOnly = true, parallelism = cloud ? 1 : ServerSettings.Parallelism(data), rateLimit = admin ? ServerSettings.RateLimit(data) : 0, judging = q.Running, queued = q.Queued };
});
app.MapPut("/api/settings", (Dictionary<string,string> values) =>
{
    if (cloud) throw new InvalidDataException("云端编译器和并行能力由本机评测端配置，请修改 start-agent.ps1 启动参数");
    if (!values.TryGetValue("compiler", out var path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) || OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请填写现有编译器的绝对路径");
    if (!values.TryGetValue("parallelism", out var raw) || !int.TryParse(raw, out var parallelism) || parallelism is < 1 or > ServerSettings.MaxParallelism) throw new InvalidDataException("并行评测数必须是 1–8 的整数");
    ServerSettings.Save(data, path, parallelism); return Results.Ok();
});
app.MapGet("/api/problems", (Store s, HttpContext ctx) =>
{
    bool admin = ctx.Items["acmAdmin"] is true;
    var contests = s.Contests();
    var now = DateTimeOffset.UtcNow;
    string ContestOf(string problemId) => contests.FirstOrDefault(c => c.ProblemIds.Contains(problemId))?.Title ?? "";
    // CF-style gate: a problem whose contest has not started yet is invisible to contestants.
    bool HiddenByUpcomingContest(string problemId) => contests.Any(c => c.ProblemIds.Contains(problemId) && c.Phase(now) == "upcoming");
    var solvedBy = new Dictionary<string, HashSet<string>>();
    foreach (var sub in s.AllSubmissions())
        if (sub.UserId != null && sub.Status == "AC")
        {
            if (!solvedBy.TryGetValue(sub.ProblemId, out var set)) solvedBy[sub.ProblemId] = set = [];
            set.Add(sub.UserId);
        }
    return s.Problems().Where(p => admin || p.Published).Where(p => admin || !HiddenByUpcomingContest(p.Id))
        .Select(p => new { p.Id, p.Title, p.Mode, p.Limits, testCount = p.Tests.Count, contestTitle = ContestOf(p.Id), published = p.Published, p.Rating, solvedBy = solvedBy.TryGetValue(p.Id, out var set) ? set.Count : 0 });
});
app.MapGet("/api/contests", (Store s, HttpContext ctx) =>
{
    bool admin = ctx.Items["acmAdmin"] is true;
    var now = DateTimeOffset.UtcNow;
    var published = admin ? null : s.Problems().Where(p => p.Published).Select(p => p.Id).ToHashSet();
    return s.Contests().Select(c =>
    {
        var ids = published == null ? c.ProblemIds : c.ProblemIds.Where(published.Contains).ToList();
        if (!admin && c.Phase(now) == "upcoming") ids = []; // 题目开赛后才公布
        return new { c.Id, c.Title, count = ids.Count, problemIds = ids, startsAt = c.StartsAt, endsAt = c.EndsAt, rated = c.Rated, settled = c.Settled, phase = c.Phase(now) };
    });
});
// 比赛排行榜（ICPC 赛制）：进行中实时更新；已结束的 rated 比赛在首次访问时结算校内 rating。
app.MapGet("/api/contests/{id}/standings", (string id, Store s, HttpContext ctx) =>
{
    var contest = s.Contest(id) ?? throw new KeyNotFoundException("比赛不存在");
    if (ctx.Items["acmAdmin"] is not true && contest.Phase(DateTimeOffset.UtcNow) == "upcoming") throw new InvalidDataException("比赛尚未开始，排行榜将在开赛后开放");
    Standings.SettleIfDue(contest, s);
    contest = s.Contest(id) ?? throw new KeyNotFoundException("比赛不存在");
    var response = Standings.Build(contest, s.Problems(), s.Users(), s.AllSubmissions());
    // 已结算的比赛附带每个选手本场的 rating 变化（从 rating 历史回查）。
    var deltas = contest.Rated && contest.Settled
        ? response.Rows.ToDictionary(r => r.UserId, r => s.Ratings(r.UserId).LastOrDefault(e => e.ContestId == contest.Id))
        : [];
    return new { response.ContestId, response.Title, response.Phase, response.Rated, response.Settled, response.StartsAt, response.EndsAt, response.Problems, rows = response.Rows.Select(r => new { r.Rank, r.UserId, r.Name, r.StudentId, r.Anonymous, r.Solved, r.Penalty, r.LastAcAt, r.Cells, delta = deltas.TryGetValue(r.UserId, out var e) ? e?.Delta : null }) };
});
app.MapPost("/api/contests", (Contest c, Store s) => s.SaveContest(c, true));
app.MapPut("/api/contests/{id}", (string id, Contest c, Store s) => { c.Id = id; return s.SaveContest(c, false); });
app.MapDelete("/api/contests/{id}", (string id, Store s) => s.DeleteContest(id) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/problems/{id}", (string id, Store s, HttpContext ctx) =>
{
    var p = s.Problem(id) ?? throw new KeyNotFoundException("题目不存在");
    if (ctx.Items["acmAdmin"] is not true && (!p.Published || s.Contests().Any(c => c.ProblemIds.Contains(id) && c.Phase(DateTimeOffset.UtcNow) == "upcoming"))) throw new KeyNotFoundException("题目不存在");
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
// 强化测试数据：AI（或管理员）给出极端输入，服务端用沙箱编译标准答案逐条运行生成答案后入库。
app.MapPost("/api/problems/{id}/harden", async (string id, HardenBody body, Store s, CancellationToken ct) =>
{
    var p = s.Problem(id) ?? throw new KeyNotFoundException("题目不存在");
    var additions = s.IsPostgres
        ? (await s.AuthoringRemote("harden", p, "", (body.Tests ?? []).Select(t => new RemoteInput(t.Input, t.Answer)).ToList(), ct)).Tests ?? throw new InvalidDataException("评测端未返回测试数据")
        : await AuthoringJudge.Harden(p, body.Tests ?? [], ServerSettings.Compiler(data), Path.Combine(root, "toolkit"), ct);
    p.Tests.AddRange(additions);
    return s.Save(p, false);
});
// Candidate programs run through the real checker, interactor, communication or manager protocol.
app.MapPost("/api/problems/{id}/brute", async (string id, BruteBody body, Store s, CancellationToken ct) =>
{
    var p = s.Problem(id) ?? throw new KeyNotFoundException("题目不存在");
    var result = s.IsPostgres
        ? (await s.AuthoringRemote("brute", p, body.Source ?? "", null, ct)).Result ?? throw new InvalidDataException("评测端未返回结果")
        : await AuthoringJudge.Brute(p, body.Source ?? "", ServerSettings.Compiler(data), Path.Combine(root, "toolkit"), ct);
    return Results.Ok(new { results = result.Cases.Select(c => new { name = c.Name, verdict = c.Verdict, cpuMs = Math.Round(c.CpuMs), message = c.Message }), stopped = result.Status != "AC" });
});
// 整题重测：与单提交 rejudge 用旧快照不同，这里用当前题目对象（含刚强化的测试点）重建快照，
// 该题全部历史提交按新数据重新入队，作者归属保持不变。
app.MapPost("/api/problems/{id}/rejudge-all", (string id, Store s, JudgeQueue q) =>
{
    var p = s.Problem(id) ?? throw new KeyNotFoundException("题目不存在");
    int count = 0;
    foreach (var sid in s.SubmissionIdsForProblem(id))
    {
        var old = s.Submission(sid);
        if (old == null) continue;
        var request = s.Request(sid);
        var submission = s.AddSubmission(p, request.Source, request.Compiler, old.UserId, request.ToolkitPath, sid);
        q.Add(submission.Id); count++;
    }
    return Results.Ok(new { requeued = count });
});
app.MapWorkspaceEndpoints();
app.MapGet("/api/submissions", (Store s, HttpContext ctx) =>
{
    // 可选筛选：mine=1 只看自己、status=判定、problemId=题目 —— 提交记录页的 CF 式过滤器。
    var query = ctx.Request.Query;
    bool mine = query["mine"] == "1";
    var me = ctx.Items["acmUser"] as User;
    int take = int.TryParse(query["take"], out var requested) ? Math.Clamp(requested, 1, 500) : 200;
    var rows = mine && me == null ? [] : s.QuerySubmissions(mine ? me?.Id : null,query["status"],query["problemId"],take);
    var names = s.Users().Select(u => (u.Id, u.Name)).ToDictionary(x => x.Id, x => x.Name);
    return rows.Select(x => new { x.Id, x.ProblemId, x.Title, x.Mode, x.Status, x.CreatedAt, x.UserId, userName = x.UserId != null && names.TryGetValue(x.UserId, out var name) ? name : "" });
});
app.MapPost("/api/submissions", (SubmitBody body, Store s, JudgeQueue q, HttpContext ctx) =>
{
    var p = s.Problem(body.ProblemId) ?? throw new KeyNotFoundException("题目不存在");
    if (ctx.Items["acmAdmin"] is not true && (!p.Published || s.Contests().Any(c => c.ProblemIds.Contains(p.Id) && c.Phase(DateTimeOffset.UtcNow) == "upcoming"))) throw new KeyNotFoundException("题目不存在");
    bool verification = ctx.Items["acmAdmin"] is true && body.Purpose == "verify";
    var submission = s.AddSubmission(p, body.Source, ServerSettings.Compiler(data), verification ? null : (ctx.Items["acmUser"] as User)?.Id); q.Add(submission.Id); return submission;
});
app.MapGet("/api/submissions/{id}", (string id, Store s, HttpContext ctx) =>
{
    var submission = s.Submission(id) ?? throw new KeyNotFoundException("提交不存在");
    var name = submission.UserId == null ? "" : s.Users().FirstOrDefault(u => u.Id == submission.UserId)?.Name ?? "";
    // 源码只对作者本人与管理员可见（CF 语义：练习期可看自己的，别人的只有结果行）。
    bool canViewSource = ctx.Items["acmAdmin"] is true || submission.UserId != null && submission.UserId == (ctx.Items["acmUser"] as User)?.Id;
    return new { submission, result = s.Result(id), context = SubmissionViews.Context(s,id), source = canViewSource ? s.Request(id).Source : "", userName = name, canViewSource };
});
app.MapPost("/api/submissions/{id}/cancel", (string id, JudgeQueue q, Store s, HttpContext ctx) =>
{
    var submission=s.Submission(id) ?? throw new KeyNotFoundException("提交不存在");
    if(ctx.Items["acmAdmin"] is not true && (submission.UserId==null || submission.UserId!=(ctx.Items["acmUser"] as User)?.Id)) return Results.Json(new {error="只能取消自己的提交"},statusCode:403);
    q.Cancel(id); return Results.Ok();
});
app.MapPost("/api/submissions/{id}/rejudge", (string id, Store s, JudgeQueue q) =>
{
    var oldSubmission = s.Submission(id) ?? throw new KeyNotFoundException("提交不存在");
    var old = s.Request(id);
    // Rejudging keeps the original author so per-user stats stay consistent.
    var submission = s.AddSubmission(old.Problem, old.Source, old.Compiler, oldSubmission.UserId, old.ToolkitPath, id); q.Add(submission.Id); return submission;
});
app.MapGet("/api/submissions/{id}/events", async (string id, HttpContext ctx, Store s) =>
{
    if (s.Submission(id) == null) throw new KeyNotFoundException("提交不存在");
    var context=SubmissionViews.Context(s,id);
    ctx.Response.ContentType = "text/event-stream"; ctx.Response.Headers.CacheControl = "no-cache";
    string previous = "";
    while (!ctx.RequestAborted.IsCancellationRequested)
    {
        var submission = s.Submission(id)!; var result = s.Result(id);
        string value = JsonSerializer.Serialize(new { submission, result, context }, Json.Compact);
        if (value != previous) { await ctx.Response.WriteAsync("data: " + value + "\n\n", ctx.RequestAborted); await ctx.Response.Body.FlushAsync(ctx.RequestAborted); previous = value; }
        if (!JudgeQueue.IsActive(submission.Status)) break;
        await Task.Delay(s.IsPostgres ? 2000 : 300, ctx.RequestAborted);
    }
});
app.MapPost("/api/worker/claim", (Store s) => s.ClaimRemote() is { } claim ? Results.Ok(claim) : Results.NoContent());
app.MapPost("/api/worker/{id}/heartbeat", (string id, RemoteHeartbeat body, Store s) => s.HeartbeatRemote(id, body) ? Results.Ok() : Results.Conflict());
app.MapFallbackToFile("index.html");
await app.RunAsync();
record SubmitBody(string ProblemId, string Source, string? Purpose = null);
record HardenBody(List<TestInput>? Tests);
record BruteBody(string? Source);
record AuthBody(string? Token);
record RegisterBody(string? StudentId, string? Name, string? Email, string? Password, string? InviteCode = null);
record LoginBody(string? StudentId, string? Password);
record ProfileBody(string? Name, string? Email, bool? Anonymous);
record PasswordBody(string? CurrentPassword, string? NewPassword);
record CfBody(string? Handle);
record CfVerifyBody(string? Code);

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

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
var sessionSecret = ServerSettings.SessionSecret(data);
var store = app.Services.GetRequiredService<Store>();
// Hashing a decoy password once at startup keeps login timing flat whether or not a student id exists.
var dummyHash = PasswordHasher.Hash("acm-decoy-password");
Console.WriteLine($"ACM Local Judge: http://{bindHost}:{port}");
Console.WriteLine($"API 访问令牌：{token}");
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
    ctx.Response.Cookies.Append(ServerSettings.CookieName, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(30) });
    return Results.Ok();
});
// —— 用户系统：注册 / 登录 / 个人设置 / Codeforces 绑定 / 校内排行榜 ——
static User CurrentUser(HttpContext ctx) => ctx.Items["acmUser"] as User ?? throw new InvalidDataException("请先登录");
static void IssueSession(HttpContext ctx, string secret, User user) => ctx.Response.Cookies.Append(UserSessions.CookieName, UserSessions.Issue(secret, user.Id), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(UserSessions.LifetimeDays) });
app.MapPost("/api/users/register", (RegisterBody body, Store s, HttpContext ctx) =>
{
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
app.MapPost("/api/users/me/codeforces/verify", async (CfVerifyBody body, Store s, HttpContext ctx, CancellationToken ct) =>
{
    var user = CurrentUser(ctx);
    var (handle, expected) = Codeforces.TakeChallenge(user.Id);
    if (!string.Equals((body.Code ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("验证码不正确");
    var info = (await Codeforces.FetchAsync([handle], ct)).FirstOrDefault(i => i.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("Codeforces 上未找到该 handle");
    if (!string.Equals((info.LastName ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("验证失败：请确认已把 Codeforces 资料的 Last name 修改为验证码并保存后重试");
    user.CodeforcesHandle = info.Handle; user.CodeforcesRating = info.Rating; user.CodeforcesRank = info.Rank; user.RatingUpdatedAt = DateTimeOffset.UtcNow;
    s.SaveUser(user, false);
    Codeforces.ClearChallenge(user.Id);
    return new { user = PublicViews.User(user) };
});
app.MapDelete("/api/users/me/codeforces", (Store s, HttpContext ctx) =>
{
    var user = CurrentUser(ctx);
    user.CodeforcesHandle = ""; user.CodeforcesRating = null; user.CodeforcesRank = ""; user.RatingUpdatedAt = null;
    s.SaveUser(user, false);
    return new { user = PublicViews.User(user) };
});
app.MapGet("/api/leaderboard", async (Store s, CancellationToken ct) =>
{
    var users = s.Users();
    // Refresh bindings older than an hour in one batched CF call; an unreachable CF API keeps the
    // cached values (with their timestamps) instead of failing the whole board.
    var stale = users.Where(u => u.CodeforcesHandle != "" && (u.RatingUpdatedAt == null || DateTimeOffset.UtcNow - u.RatingUpdatedAt > TimeSpan.FromHours(1))).ToList();
    if (stale.Count > 0)
    {
        try
        {
            var infos = await Codeforces.FetchAsync(stale.Select(u => u.CodeforcesHandle), ct);
            foreach (var user in stale)
            {
                var info = infos.FirstOrDefault(i => i.Handle.Equals(user.CodeforcesHandle, StringComparison.OrdinalIgnoreCase));
                if (info == null) continue;
                user.CodeforcesRating = info.Rating; user.CodeforcesRank = info.Rank; user.RatingUpdatedAt = DateTimeOffset.UtcNow;
                s.SaveUser(user, false);
            }
        }
        catch (InvalidDataException) { /* CF 不可达时沿用缓存 */ }
    }
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
    return new { app = "acm-local-judge", os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, compiler = admin ? compiler : "", compilerExists = admin && File.Exists(compiler), workerReady = File.Exists(Path.Combine(root, "artifacts/worker/Judge.Worker.dll")), standard = "C++23", memoryMetric = OperatingSystem.IsWindows() ? "Windows 提交内存峰值" : "Linux RSS 峰值", trustedOnly = true, parallelism = ServerSettings.Parallelism(data), rateLimit = admin ? ServerSettings.RateLimit(data) : 0, judging = q.Running, queued = q.Queued };
});
app.MapPut("/api/settings", (Dictionary<string,string> values) =>
{
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
    var inputs = (body.Tests ?? []).Select(t => (t.Input ?? "").TrimEnd() + "\n").Where(x => x.Trim().Length > 0).ToList();
    if (inputs.Count == 0) throw new InvalidDataException("请提供要强化测试的输入数据");
    if (inputs.Count > 20) throw new InvalidDataException("单次最多强化 20 个测试点");
    if (inputs.Any(x => x.Length > 1024 * 1024)) throw new InvalidDataException("单个输入不能超过 1 MiB");
    if (string.IsNullOrWhiteSpace(p.ExampleSource)) throw new InvalidDataException("请先在「编辑题目 → 标准答案」中填写标准答案源码");
    var dir = Path.Combine(Path.GetTempPath(), "acm-harden-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "sol.exe" : "sol");
        var compileLog = await CompileSource(ServerSettings.Compiler(data), p.ExampleSource, dir, exe, ct);
        if (compileLog != null) throw new InvalidDataException("标准答案源码编译失败：" + Truncate(compileLog, 800));
        var used = p.Tests.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < inputs.Count; i++)
        {
            var run = await RunSandboxed(exe, dir, inputs[i], p.Limits, ct);
            if (run.Verdict == "TLE") throw new InvalidDataException($"第 {i + 1} 条输入：标准答案运行超时，数据可能超出题目约束");
            if (run.Verdict == "OLE") throw new InvalidDataException($"第 {i + 1} 条输入：答案超过题目输出上限");
            if (run.Verdict != "AC") throw new InvalidDataException($"第 {i + 1} 条输入：标准答案运行异常（{run.Verdict}），数据可能违反输入约束");
            int n = p.Tests.Count;
            string name;
            do { n++; name = "h" + n.ToString("00"); } while (!used.Add(name));
            // Windows 管道文本模式会把 \n 转成 \r\n；答案统一归一化为 LF，与手写测试数据一致。
            p.Tests.Add(new TestData(name, inputs[i], run.Stdout.Replace("\r\n", "\n")));
        }
        return s.Save(p, false);
    }
    finally { try { Directory.Delete(dir, true); } catch { /* 临时目录失败时留给系统清理 */ } }
});
// 暴力对拍：编译管理员粘贴的暴力程序，按题目限额逐测试点运行，遇到首个非 AC 即停，用于确认强化数据的强度。
app.MapPost("/api/problems/{id}/brute", async (string id, BruteBody body, Store s, CancellationToken ct) =>
{
    var p = s.Problem(id) ?? throw new KeyNotFoundException("题目不存在");
    if (string.IsNullOrWhiteSpace(body.Source)) throw new InvalidDataException("请粘贴暴力程序源码");
    if (p.Tests.Count == 0) throw new InvalidDataException("该题目还没有测试点");
    var dir = Path.Combine(Path.GetTempPath(), "acm-brute-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "brute.exe" : "brute");
        var compileLog = await CompileSource(ServerSettings.Compiler(data), body.Source, dir, exe, ct);
        if (compileLog != null) throw new InvalidDataException("暴力程序编译失败：" + Truncate(compileLog, 800));
        var results = new List<object>(p.Tests.Count);
        foreach (var test in p.Tests)
        {
            var r = await RunSandboxed(exe, dir, test.Input, p.Limits, ct);
            // NativeRunner 只给沙箱级判定（正常退出=AC）；答案比对在这里按 judge 的 token 语义完成。
            var verdict = r.Verdict != "AC" ? r.Verdict : Tokens(r.Stdout).SequenceEqual(Tokens(test.Answer)) ? "AC" : "WA";
            results.Add(new { name = test.Name, verdict, cpuMs = Math.Round(r.Usage.CpuMs) });
            if (verdict != "AC") return Results.Ok(new { results, stopped = true });
        }
        return Results.Ok(new { results, stopped = false });
    }
    finally { try { Directory.Delete(dir, true); } catch { /* 临时目录失败时留给系统清理 */ } }
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
        var requestFile = Path.Combine(s.SubmissionDir(sid), "request.json");
        if (old == null || !File.Exists(requestFile)) continue;
        var request = Json.Read<JudgeRequest>(File.ReadAllText(requestFile));
        var submission = s.AddSubmission(p, request.Source, request.Compiler, old.UserId, request.ToolkitPath);
        q.Add(submission.Id); count++;
    }
    return Results.Ok(new { requeued = count });
});
static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

// 用沙箱编译一份 C++ 源码；成功返回 null，失败返回编译日志。
static async Task<string?> CompileSource(string compiler, string source, string workDir, string outputExe, CancellationToken ct)
{
    var src = Path.Combine(workDir, "sol.cpp");
    await File.WriteAllTextAsync(src, source, new UTF8Encoding(false), ct);
    string[] args = ["-std=c++23", "-O2", "-pipe", src, "-o", outputExe];
    await using var runner = new NativeRunner(compiler, args, workDir, new() { CpuMs = 60000, WallMs = 60000, MemoryMiB = 1024, OutputKiB = 1024 }, ct, processLimit: 32);
    await runner.CloseInput();
    var r = await runner.Completion;
    return r.Verdict == "AC" ? null : r.Stdout + r.Stderr;
}
// 在题目限额下用沙箱运行已编译程序。
static async Task<RunResult> RunSandboxed(string exe, string workDir, string input, Limits limits, CancellationToken ct)
{
    await using var runner = new NativeRunner(exe, [], workDir, limits, ct);
    var feed = Task.Run(async () => { await runner.Send(input); await runner.CloseInput(); });
    var r = await runner.Completion; await feed;
    return r;
}
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
    return new { submission, result = s.Result(id), context = SubmissionViews.Context(s,id), source = canViewSource ? File.ReadAllText(Path.Combine(s.SubmissionDir(id), "source.cpp")) : "", userName = name, canViewSource };
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
    var old = Json.Read<JudgeRequest>(File.ReadAllText(Path.Combine(s.SubmissionDir(id), "request.json")));
    // Rejudging keeps the original author so per-user stats stay consistent.
    var submission = s.AddSubmission(old.Problem, old.Source, old.Compiler, oldSubmission.UserId, old.ToolkitPath); q.Add(submission.Id); return submission;
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
        await Task.Delay(300, ctx.RequestAborted);
    }
});
app.MapFallbackToFile("index.html");
await app.RunAsync();
record SubmitBody(string ProblemId, string Source, string? Purpose = null);
record HardenBody(List<TestInput>? Tests);
record TestInput(string Input);
record BruteBody(string? Source);
record AuthBody(string? Token);
record RegisterBody(string? StudentId, string? Name, string? Email, string? Password);
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

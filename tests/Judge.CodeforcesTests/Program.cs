using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Judge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var evidence = Path.Combine(root, "artifacts", "codeforces-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(evidence);
List<string> checks = [];
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
var apiPort = Port(); var serverPort = Port();
var lastName = "old-profile"; var failPage = false; var rejudge = false; var throttle = false; var failInfo = false;
TaskCompletionSource? hold = null;
var requests = new ConcurrentQueue<(string Path, DateTimeOffset At, string Cache)>();
var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls($"http://127.0.0.1:{apiPort}"); builder.Logging.ClearProviders();
await using var fixture = builder.Build();
fixture.MapGet("/user.info", (HttpContext ctx) =>
{
    requests.Enqueue((ctx.Request.Path + ctx.Request.QueryString, DateTimeOffset.UtcNow, ctx.Request.Headers.CacheControl.ToString()));
    return failInfo ? Results.Json(new { status="FAILED", comment="handle: User not found" }) : Results.Json(new { status="OK", result=new[] { new { handle="cf_test", rating=2100, rank="master", lastName } } });
});
fixture.MapGet("/user.status", async (HttpContext ctx) =>
{
    requests.Enqueue((ctx.Request.Path + ctx.Request.QueryString, DateTimeOffset.UtcNow, ctx.Request.Headers.CacheControl.ToString()));
    if (hold != null) await hold.Task;
    if (throttle) { throttle=false; return Results.Json(new { status="FAILED", comment="Call limit exceeded" }); }
    var from = int.Parse(ctx.Request.Query["from"]!);
    if (failPage && from>1000) return Results.Json(new { status="FAILED", comment="fixture page failure" });
    var rows = Enumerable.Range(1,1003).Select(n => new { id=2000-n, contestId=100, creationTimeSeconds=1700000000+n,
        problem=new {contestId=100, index=n%2==0?"A":"B", name="fixture problem", rating=800, tags=new[]{"math"}},
        verdict=rejudge?"WRONG_ANSWER":n%3==0?"OK":"WRONG_ANSWER", programmingLanguage="GNU C++23", timeConsumedMillis=15, memoryConsumedBytes=1024 }).ToList();
    // Repeat a record across page boundaries, as CF can do when a new submission shifts offsets.
    if (from>1000) rows[1000]=rows[999];
    return Results.Json(new {status="OK",result=rows.Skip(from-1).Take(1000)});
});
await fixture.StartAsync();
Process? server=null;
using var client = new HttpClient(new HttpClientHandler { CookieContainer=new() }) { BaseAddress=new Uri($"http://127.0.0.1:{serverPort}"), Timeout=TimeSpan.FromSeconds(70) };
using var second = new HttpClient(new HttpClientHandler { CookieContainer=new() }) { BaseAddress=client.BaseAddress };
using var anonymous = new HttpClient { BaseAddress=client.BaseAddress };
async Task Start()
{
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory=root, UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
    start.ArgumentList.Add(Path.Combine(root,"src/Judge.Server/bin/Release/net10.0/Judge.Server.dll"));
    start.Environment["ACM_ROOT"]=root; start.Environment["ACM_DATA"]=Path.Combine(evidence,"data"); start.Environment["ACM_PORT"]=serverPort.ToString(); start.Environment["ACM_CF_API"]=$"http://127.0.0.1:{apiPort}"; start.Environment["ACM_TOKEN"]="cf-test-token";
    server=Process.Start(start)!; server.OutputDataReceived+=(_,e)=>{if(e.Data!=null)File.AppendAllText(Path.Combine(evidence,"server.log"),e.Data+"\n");}; server.ErrorDataReceived+=(_,e)=>{}; server.BeginOutputReadLine(); server.BeginErrorReadLine();
    for(var i=0;i<100;i++){ try {if((await client.GetAsync("/healthz")).IsSuccessStatusCode)return;}catch(HttpRequestException){} await Task.Delay(100); }
    throw new Exception("Server did not start");
}
async Task<JsonNode> Read(HttpResponseMessage response) { using(response){ var body=await response.Content.ReadAsStringAsync(); Check(response.IsSuccessStatusCode,"HTTP "+response.StatusCode); return JsonNode.Parse(body)!; } }
async Task<JsonNode> History() => await Read(await client.GetAsync("/api/users/me/codeforces/history"));
async Task<JsonNode> Finish()
{
    for(var i=0;i<160;i++) { var h=await client.GetFromJsonAsync<JsonNode>("/api/users/me/codeforces/history"); var state=h!["sync"]!["status"]!.GetValue<string>(); if(state is "complete" or "failed")return h; await Task.Delay(250); }
    throw new Exception("CF job did not finish");
}
try
{
    await Start();
    Check((await anonymous.GetAsync("/api/users/me/codeforces/history")).StatusCode==HttpStatusCode.Unauthorized,"history requires a user session");
    await Read(await client.PostAsJsonAsync("/api/users/register",new {studentId="908001",name="CF测试",email="cf1@example.test",password="test-password"}));
    await Read(await second.PostAsJsonAsync("/api/users/register",new {studentId="908002",name="CF测试乙",email="cf2@example.test",password="test-password"}));
    Check((await client.PostAsync("/api/users/me/codeforces/sync",null)).StatusCode==HttpStatusCode.BadRequest,"unbound sync rejected");
    var challenge=await Read(await client.PostAsJsonAsync("/api/users/me/codeforces",new {handle="cf_test"})); var code=challenge["code"]!.GetValue<string>();
    var again=await Read(await client.PostAsJsonAsync("/api/users/me/codeforces",new {handle="CF_TEST"}));
    Check(again["code"]!.GetValue<string>()==code,"repeated challenge preserves code while valid");
    var restored=await Read(await client.GetAsync("/api/users/me/codeforces/challenge"));
    Check(restored["challenge"]!["code"]!.GetValue<string>()==code&&restored["challenge"]!["expiresAt"]!=null,"challenge can be restored with absolute expiry");
    Check((await client.PostAsJsonAsync("/api/users/me/codeforces/verify",new{code="WRONG"})).StatusCode==HttpStatusCode.BadRequest,"incorrect code never verifies");
    using(var pending=await client.PostAsJsonAsync("/api/users/me/codeforces/verify",new{code}))
    {var body=await pending.Content.ReadFromJsonAsync<JsonNode>();Check(pending.StatusCode==HttpStatusCode.Accepted&&body!["status"]!.GetValue<string>()=="pending","stale CF profile returns retryable pending instead of false failure");}
    Check((await History())["handle"]!.GetValue<string>()=="","stale profile never binds an account");
    lastName=code; throttle=true;
    var verified=await Read(await client.PostAsJsonAsync("/api/users/me/codeforces/verify",new{code}));
    Check(verified["user"]!["cfHandle"]!.GetValue<string>()=="cf_test","updated Last name verifies account");
    var done=await Finish();
    Check(done["sync"]!["status"]!.GetValue<string>()=="complete","binding automatically synchronizes through a rate-limit retry");
    Check(done["submissionCount"]!.GetValue<int>()==1002&&done["solvedCount"]!.GetValue<int>()==2,"complete history deduplicates submission ids and solved problems");
    Check(done["items"]!.AsArray().Count==50&&done["pageCount"]!.GetValue<int>()==21,"history response is paginated");
    var tail=await Read(await client.GetAsync("/api/users/me/codeforces/history?page=21"));
    Check(tail["items"]!.AsArray().Count==2,"last history page includes old submissions");
    var solved=await Read(await client.GetAsync("/api/users/me/codeforces/history?view=solved"));
    Check(solved["items"]!.AsArray().Count==2&&solved["items"]!.AsArray().All(s=>s!["verdict"]!.GetValue<string>()=="OK"),"solved view contains only unique accepted problems");
    var isolated=await Read(await second.GetAsync("/api/users/me/codeforces/history"));
    Check(isolated["submissionCount"]!.GetValue<int>()==0,"other account cannot see bound user's CF history");
    var savedAt=done["updatedAt"]!.GetValue<string>(); failPage=true;
    await Read(await client.PostAsync("/api/users/me/codeforces/sync",null)); done=await Finish();
    Check(done["sync"]!["status"]!.GetValue<string>()=="failed"&&done["submissionCount"]!.GetValue<int>()==1002&&done["updatedAt"]!.GetValue<string>()==savedAt,"later page failure keeps entire previous snapshot and timestamp");
    failPage=false; failInfo=true;
    await Read(await client.PostAsync("/api/users/me/codeforces/sync",null)); done=await Finish();
    Check(done["sync"]!["status"]!.GetValue<string>()=="failed"&&done["updatedAt"]!.GetValue<string>()==savedAt,"profile failure after history fetch is atomic");
    failInfo=false; rejudge=true;
    await Read(await client.PostAsync("/api/users/me/codeforces/sync",null)); done=await Finish();
    Check(done["sync"]!["status"]!.GetValue<string>()=="complete"&&done["solvedCount"]!.GetValue<int>()==0,"resync refreshes old rejudged verdicts");
    server!.Kill(true); await server.WaitForExitAsync(); server.Dispose(); await Start();
    Check((await History())["submissionCount"]!.GetValue<int>()==1002,"history and login survive server restart");
    hold=new(TaskCreationOptions.RunContinuationsAsynchronously);
    var before=requests.Count;
    await Read(await client.PostAsync("/api/users/me/codeforces/sync",null));
    await Read(await client.PostAsync("/api/users/me/codeforces/sync",null));
    for(var i=0;i<100&&requests.Count==before;i++)await Task.Delay(100);
    Check(requests.Count==before+1,"duplicate sync clicks coalesce to one active job");
    await Read(await client.DeleteAsync("/api/users/me/codeforces")); hold.SetResult(); hold=null;
    await Task.Delay(2600);
    done=await History();
    Check(done["handle"]!.GetValue<string>()==""&&done["submissionCount"]!.GetValue<int>()==0,"unbind during synchronization cannot restore account or history");
    var calls=requests.ToArray();
    Check(calls.Zip(calls.Skip(1),(a,b)=>(b.At-a.At).TotalMilliseconds).All(ms=>ms>=1950),"all CF requests share the two-second rate limit");
    Check(calls.All(r=>r.Path.Contains("_=")&&r.Cache.Contains("no-cache")),"verification and synchronization request fresh uncached responses");
    File.WriteAllText(Path.Combine(evidence,"report.json"),Json.Write(new {checks,requestCount=calls.Length}));
    Console.WriteLine($"PASS {checks.Count} checks; evidence {evidence}");
}
finally
{
    hold?.TrySetResult();
    if(server is {HasExited:false}){server.Kill(true);await server.WaitForExitAsync();} server?.Dispose();
    await fixture.StopAsync();
}

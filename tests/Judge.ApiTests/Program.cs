using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Judge.Core;
using Judge.Server;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string evidence = Path.Combine(root, "artifacts", "api-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(evidence);
List<string> checks = [];
void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
foreach (var p in Examples.All())
{
    using var zip = new MemoryStream(Packages.Export(p)); var read = await Packages.Import(zip, default);
    Assert(read with { Id=p.Id, Published=p.Published } == p || Json.Write(read with { Id=p.Id, Published=p.Published }) == Json.Write(p), p.Mode + " ZIP round trip");
    Assert(!read.Published, p.Mode + " ZIP import resets publishing state to draft");
    File.WriteAllBytes(Path.Combine(evidence, p.Mode + ".zip"), Packages.Export(p));
}
var samplePrivacy=FlowExample.Create();
samplePrivacy.Flow!.Steps[3].Expected=new(){Source="literal",Text="HIDDEN_EXPECTATION_SENTINEL"};
samplePrivacy.Tests.Add(new("hidden","HIDDEN_INPUT_SENTINEL","HIDDEN_ANSWER_SENTINEL"));
var publicSample=JsonNode.Parse(Json.Write(PublicViews.Problem(samplePrivacy)))!;
Assert(publicSample["communicationSample"]!["steps"]!.AsArray().Count==5,"public communication sample retains ordered routes");
Assert(!publicSample.ToJsonString().Contains("HIDDEN_"),"public communication sample strips expectations and hidden test contents");
Assert(publicSample["flow"]==null&&publicSample["communicationSample"]!["steps"]![0]!["content"]!.GetValue<string>()=="3\n","public sample renders the first test without exposing executable flow configuration");
foreach(var invalid in new[] { Examples.All()[0] with {Tests=[new("CON","","")]}, Examples.All()[0] with {Limits=null!} })
{
    bool rejected=false;try{invalid.Validate();}catch(InvalidDataException){rejected=true;}Assert(rejected,"reject invalid problem fields");
}
foreach (var attack in new[] { "../escape", "/root", "C:/outside", "nested/../../escape", "tests\\..\\escape" })
{
    using var ms = new MemoryStream(); using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) { using var w = new StreamWriter(z.CreateEntry(attack).Open()); w.Write("bad"); }
    ms.Position=0; bool rejected=false; try { await Packages.Import(ms,default); } catch (InvalidDataException) { rejected=true; } Assert(rejected,"reject ZIP " + attack);
}
using(var ms = new MemoryStream())
{
    using(var z=new ZipArchive(ms,ZipArchiveMode.Create,true)) { using var output=z.CreateEntry("huge.txt",CompressionLevel.SmallestSize).Open(); byte[] block=new byte[1024*1024]; for(int i=0;i<65;i++)output.Write(block); }
    ms.Position=0; bool rejected=false; try { await Packages.Import(ms,default); } catch(InvalidDataException){rejected=true;} Assert(rejected,"reject ZIP decompression >64 MiB");
}
var serverDll=Path.Combine(root,"artifacts/server/Judge.Server.dll");
const int port=27124;
Process? server=null; Process? authServer=null; Process? ojServer=null;
// Codeforces API 打桩：user.info 按请求的 handles 返回可配置的 lastName（挑战码）/ rating。
string cfLastName=""; int cfRating=2100; int cfPort=0;
var cfCts=new CancellationTokenSource();
Task cfTask=Task.CompletedTask;
async Task<Process> Start(int serverPort=port, Dictionary<string,string>? extraEnv=null)
{
    var si=new ProcessStartInfo("dotnet"){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    si.ArgumentList.Add(serverDll);si.Environment["ACM_ROOT"]=root;si.Environment["ACM_DATA"]=evidence;si.Environment["ACM_PORT"]=serverPort.ToString();si.Environment["ACM_TOKEN"]="main-test-token";
    if(extraEnv!=null) foreach(var (k,v) in extraEnv) si.Environment[k]=v;
    var process=Process.Start(si)!;
    if (serverPort==port) server=process;
    process.OutputDataReceived+=(_,e)=> { if(e.Data!=null) File.AppendAllText(Path.Combine(evidence,$"server-{serverPort}.log"),e.Data+"\n"); };
    process.ErrorDataReceived+=(_,e)=> { if(e.Data!=null) File.AppendAllText(Path.Combine(evidence,$"server-error-{serverPort}.log"),e.Data+"\n"); };
    process.BeginOutputReadLine();process.BeginErrorReadLine();
    using var probe=new HttpClient(new HttpClientHandler{UseProxy=false});
    for(int i=0;i<100;i++) { if(process.HasExited)throw new Exception("Server exited");try{if((await probe.GetAsync($"http://127.0.0.1:{serverPort}/healthz")).IsSuccessStatusCode)return process;}catch(HttpRequestException){} await Task.Delay(100); }
    throw new Exception("Server not ready");
}
using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(120)};
async Task<JsonNode> Request(string method,string path,object? body=null)
{
    using var req=new HttpRequestMessage(new HttpMethod(method),"/api"+path);
    if(body!=null) req.Content=new StringContent(Json.Write(body),Encoding.UTF8,"application/json");
    using var response=await client.SendAsync(req);string text=await response.Content.ReadAsStringAsync();
    if(!response.IsSuccessStatusCode)throw new Exception($"{path}: {response.StatusCode} {text}");
    return string.IsNullOrWhiteSpace(text)?new JsonObject():JsonNode.Parse(text)!;
}
async Task<JsonNode> Wait(string id,string? until=null)
{
    for(int i=0;i<600;i++) { var r=await Request("GET","/submissions/"+id);string status=r["submission"]!["status"]!.GetValue<string>();if(until==status||!JudgeQueue.IsActive(status))return r;await Task.Delay(100); }
    throw new Exception("Submission timed out");
}
try
{
    await Start();
    // 管理端双重验证：管理 API 即使回环也要令牌；/admin 页面未登录只见登录门，登录后才下发控制台
    using(var noAuth=new HttpRequestMessage(HttpMethod.Post,"/api/problems"){Content=new StringContent("{}",Encoding.UTF8,"application/json")})
        Assert((await client.SendAsync(noAuth)).StatusCode==HttpStatusCode.Unauthorized,"management API requires token even on loopback");
    using(var gate=await client.GetAsync("/admin"))
    {
        string gateHtml=await gate.Content.ReadAsStringAsync();
        Assert(gate.StatusCode==HttpStatusCode.OK&&gateHtml.Contains("/api/auth")&&!gateHtml.Contains("id=\"app\""),"/admin serves a login gate before authentication");
    }
    using(var loginOk=await client.PostAsync("/api/auth",new StringContent(Json.Write(new{token="main-test-token"}),Encoding.UTF8,"application/json")))
        Assert(loginOk.StatusCode==HttpStatusCode.OK,"admin login with startup token");
    using(var adminPage=await client.GetAsync("/admin"))
    {
        string page=await adminPage.Content.ReadAsStringAsync();
        Assert(adminPage.StatusCode==HttpStatusCode.OK&&page.Contains("id=\"app\""),"admin console served after login");
    }
    using(var rawClient=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=client.BaseAddress})
    using(var rawRedirect=await rawClient.GetAsync("/admin.html"))
        Assert(rawRedirect.StatusCode==HttpStatusCode.Found,"raw admin.html redirects into the gated route");
    Assert((await Request("GET","/problems")).AsArray().Count==5,"five seeded examples");
    var flowProblem = FlowExample.Create();
    flowProblem.Id = (await Request("POST", "/problems", flowProblem))["id"]!.GetValue<string>();
    Assert((await Request("GET", "/problems/" + flowProblem.Id))["flow"]!["steps"]!.AsArray().Count == 5, "API persists visual flow");
    string flowSubmission = (await Request("POST", "/submissions", new { problemId = flowProblem.Id, source = flowProblem.ExampleSource }))["id"]!.GetValue<string>();
    Assert((await Wait(flowSubmission))["result"]!["status"]!.GetValue<string>() == "AC", "Worker executes visual flow");
    flowProblem.Flow!.Steps[3].Expected = new() { Text = "999" };
    flowProblem.Limits=flowProblem.Limits with {CpuMs=4000};
    flowProblem.Tests.Add(new("03","1\n2\n","3\n"));
    await Request("PUT", "/problems/" + flowProblem.Id, flowProblem);
    var historicalFlow=await Request("GET","/submissions/"+flowSubmission);
    Assert(historicalFlow["context"]!["limits"]!["cpuMs"]!.GetValue<int>()==2000&&historicalFlow["context"]!["testCount"]!.GetValue<int>()==2,"historical result metadata uses original limits and test count after problem edits");
    string flowRepeat = (await Request("POST", "/submissions/" + flowSubmission + "/rejudge"))["id"]!.GetValue<string>();
    Assert((await Wait(flowRepeat))["result"]!["status"]!.GetValue<string>() == "AC", "rejudge freezes flow and expected values");
    Assert((await Request("GET","/submissions/"+flowRepeat))["context"]!["limits"]!["cpuMs"]!.GetValue<int>()==2000,"rejudge result metadata is also bound to the original snapshot");
    flowProblem.Flow.Steps[0].To = 99;
    using (var invalidFlow = await client.PostAsJsonAsync("/api/problems", flowProblem, Json.Options))
        Assert(invalidFlow.StatusCode == HttpStatusCode.BadRequest, "API rejects missing flow member");
    var p=Examples.All()[0] with {Id="",Title="API acceptance",Tests=[new("01","20 22\n","42\n")]};
    var created=await Request("POST","/problems",p);p.Id=created["id"]!.GetValue<string>();
    Assert(p.Id.Length==32,"create problem");
    Assert(created["rating"]!.GetValue<int>()==Examples.All()[0].Rating,"problem detail carries rating");
    // 评级门禁：发布必须带 rating（100–3500）；加入比赛的所有题目必须已评级。
    using(var unratedPublish=await client.PostAsJsonAsync("/api/problems",Examples.All()[0] with {Id="",Title="Unrated publish",Published=true,Rating=0},Json.Options))
        Assert(unratedPublish.StatusCode==HttpStatusCode.BadRequest,"publishing requires a rating");
    using(var badRating=await client.PostAsJsonAsync("/api/problems",Examples.All()[0] with {Id="",Title="Bad rating",Published=true,Rating=4000},Json.Options))
        Assert(badRating.StatusCode==HttpStatusCode.BadRequest,"rating must stay within 100-3500");
    var unrated=Examples.All()[0] with {Id="",Title="Unrated draft",Published=false,Rating=0,Tests=[new("01","1\n","1\n")]};
    unrated.Id=(await Request("POST","/problems",unrated))["id"]!.GetValue<string>();
    using(var unratedContest=new HttpRequestMessage(HttpMethod.Post,"/api/contests"){Content=new StringContent(Json.Write(new{title="评级门禁赛",problemIds=new[]{unrated.Id}}),Encoding.UTF8,"application/json")})
    {
        var reject=await client.SendAsync(unratedContest);
        Assert(reject.StatusCode==HttpStatusCode.BadRequest&&(await reject.Content.ReadAsStringAsync()).Contains("未评级"),"contest rejects unrated problems");
    }
    // 强化数据：服务端编译标准答案逐条运行生成答案；无标准答案源码时拒绝。
    var hardened=await Request("POST","/problems/"+p.Id+"/harden",new{tests=new[]{new{input="3 4"},new{input="100000 -99999"}}});
    Assert(hardened["tests"]!.AsArray().Count==3,"harden appends stress tests");
    Assert(hardened["tests"]![1]!["answer"]!.GetValue<string>()=="7\n","harden computes answers with the standard solution");
    var noSol=Examples.All()[0] with {Id="",Title="No sol",Published=false,Rating=0,ExampleSource="",Tests=[new("01","1\n","1\n")]};
    noSol.Id=(await Request("POST","/problems",noSol))["id"]!.GetValue<string>();
    using(var hardenNoSol=new HttpRequestMessage(HttpMethod.Post,"/api/problems/"+noSol.Id+"/harden"){Content=new StringContent(Json.Write(new{tests=new[]{new{input="1\n"}}}),Encoding.UTF8,"application/json")})
        Assert((await client.SendAsync(hardenNoSol)).StatusCode==HttpStatusCode.BadRequest,"harden requires a standard answer source");
    // 暴力对拍：错误程序首个测试点即被卡；标准答案全 AC；空源码拒绝。
    var bruteWrong=await Request("POST","/problems/"+p.Id+"/brute",new{source=Examples.Wrong});
    Assert(bruteWrong["stopped"]!.GetValue<bool>()&&bruteWrong["results"]![0]!["verdict"]!.GetValue<string>()=="WA","brute flags a wrong program on the first test");
    var bruteGood=await Request("POST","/problems/"+p.Id+"/brute",new{source=Examples.Sum});
    Assert(bruteGood["stopped"]!.GetValue<bool>()==false&&bruteGood["results"]!.AsArray().Count==3,"brute passes the standard solution on all tests");
    using(var bruteEmpty=new HttpRequestMessage(HttpMethod.Post,"/api/problems/"+p.Id+"/brute"){Content=new StringContent(Json.Write(new{source=""}),Encoding.UTF8,"application/json")})
        Assert((await client.SendAsync(bruteEmpty)).StatusCode==HttpStatusCode.BadRequest,"brute requires a source");
    var submission=await Request("POST","/submissions",new{problemId=p.Id,source=Examples.Sum});string id=submission["id"]!.GetValue<string>();
    using(var response=await client.GetAsync("/api/submissions/"+id+"/events",HttpCompletionOption.ResponseHeadersRead))
    {
        string stream=await response.Content.ReadAsStringAsync();File.WriteAllText(Path.Combine(evidence,"events.txt"),stream);Assert(stream.Contains("data:")&&stream.Contains("\"status\":\"AC\""),"SSE reaches terminal AC");
    }
    Assert((await Wait(id))["result"]!["status"]!.GetValue<string>()=="AC","real worker submission AC");
    // 整题重测：历史提交用含强化数据的当前快照重判（cases 覆盖 3 个测试点）。
    var requeued=(await Request("POST","/problems/"+p.Id+"/rejudge-all"))["requeued"]!.GetValue<int>();
    Assert(requeued>=1,"rejudge-all requeues the problem's submissions");
    var newest=(await Request("GET","/submissions")).AsArray().First(n=>n!["problemId"]!.GetValue<string>()==p.Id)!;
    Assert((await Wait(newest["id"]!.GetValue<string>()))["result"]!["cases"]!.AsArray().Count==3,"rejudge-all rebuilds the snapshot with hardened tests");
    var reveal=Examples.All()[0] with {Id="",Title="WA reveal",Tests=[new("01","1\n","42\n")]};
    reveal.Id=(await Request("POST","/problems",reveal))["id"]!.GetValue<string>();
    string wrong=(await Request("POST","/submissions",new{problemId=reveal.Id,source="#include <iostream>\nint main(){std::cout<<41<<std::endl;}"}))["id"]!.GetValue<string>();
    var wrongResult=(await Wait(wrong))["result"]!;
    Assert(wrongResult["status"]!.GetValue<string>()=="WA"&&!wrongResult["cases"]![0]!["message"]!.GetValue<string>().Contains("42"),"WA hides expected tokens by default");
    reveal.RevealExpected=true;await Request("PUT","/problems/"+reveal.Id,reveal);
    // The old submission's snapshot predates the flag, so a fresh submission picks up RevealExpected.
    string rerun=(await Request("POST","/submissions",new{problemId=reveal.Id,source="#include <iostream>\nint main(){std::cout<<41<<std::endl;}"}))["id"]!.GetValue<string>();
    Assert((await Wait(rerun))["result"]!["cases"]![0]!["message"]!.GetValue<string>().Contains("42"),"RevealExpected opts into showing expected tokens");
    // 题库/比赛：分组、计数、联动与权限（权限断言用无 Cookie 的裸客户端模拟未登录访客）
    using var bareClient=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=client.BaseAddress};
    using(var anonContests=await bareClient.GetAsync("/api/contests"))
        Assert(anonContests.StatusCode==HttpStatusCode.Unauthorized,"contest list requires a signed-in user under the account system");
    using(var anonCreate=new HttpRequestMessage(HttpMethod.Post,"/api/contests"){Content=new StringContent(Json.Write(new{title="x",problemIds=new string[0]}),Encoding.UTF8,"application/json")})
        Assert((await bareClient.SendAsync(anonCreate)).StatusCode==HttpStatusCode.Unauthorized,"contest management requires the token");
    string contestId=(await Request("POST","/contests",new{title="API 杯邀请赛",problemIds=new[]{reveal.Id,p.Id}}))["id"]!.GetValue<string>();
    Assert(contestId.Length==32,"create contest");
    var rows=(await Request("GET","/problems")).AsArray();
    Assert(rows.First(n=>n!["id"]!.GetValue<string>()==reveal.Id)!["contestTitle"]!.GetValue<string>()=="API 杯邀请赛","problem rows carry contest title");
    Assert(rows.First(n=>n!["id"]!.GetValue<string>()==reveal.Id)!["published"]!.GetValue<bool>(),"problem rows carry the published flag");
    Assert(rows.First(n=>n!["id"]!.GetValue<string>()==reveal.Id)!["rating"]!.GetValue<int>()==Examples.All()[0].Rating,"problem rows carry rating");
    Assert((await Request("GET","/contests")).AsArray().First(n=>n!["id"]!.GetValue<string>()==contestId)!["count"]!.GetValue<int>()==2,"contest count matches membership");
    using(var badContest=new HttpRequestMessage(HttpMethod.Post,"/api/contests"){Content=new StringContent(Json.Write(new{title="坏比赛",problemIds=new string[]{"ffffffffffffffffffffffffffffffff"}}),Encoding.UTF8,"application/json")})
        Assert((await client.SendAsync(badContest)).StatusCode==HttpStatusCode.BadRequest,"contest rejects unknown problem ids");
    await Request("PUT","/contests/"+contestId,new{title="API 杯决赛",problemIds=new[]{reveal.Id}});
    Assert((await Request("GET","/contests")).AsArray().First(n=>n!["id"]!.GetValue<string>()==contestId)!["title"]!.GetValue<string>()=="API 杯决赛","rename contest and drop membership");
    Assert((await Request("GET","/problems")).AsArray().First(n=>n!["id"]!.GetValue<string>()==p.Id)!["contestTitle"]!.GetValue<string>()=="","removing a problem clears its contest title");
    await Request("DELETE","/contests/"+contestId);
    Assert((await Request("GET","/problems")).AsArray().First(n=>n!["id"]!.GetValue<string>()==reveal.Id)!["contestTitle"]!.GetValue<string>()=="","deleting a contest clears membership");
    p.Tests[0]=p.Tests[0] with {Answer="999"};await Request("PUT","/problems/"+p.Id,p);
    string repeat=(await Request("POST","/submissions/"+id+"/rejudge"))["id"]!.GetValue<string>();
    Assert((await Wait(repeat))["result"]!["status"]!.GetValue<string>()=="AC","rejudge uses immutable snapshot");
    byte[] exported=await client.GetByteArrayAsync("/api/problems/"+p.Id+"/export");
    using(var form=new MultipartFormDataContent())
    {
        form.Add(new ByteArrayContent(exported),"file","problem.zip");var r=await client.PostAsync("/api/packages/preview",form);r.EnsureSuccessStatusCode();var imported=Json.Read<Problem>(await r.Content.ReadAsStringAsync());
        string importedId=(await Request("POST","/problems",imported))["id"]!.GetValue<string>();Assert(importedId!=p.Id,"import always creates new problem");
    // 并行评测：设置校验、真并发、并发上限
    string compilerPath=ServerSettings.Compiler(evidence);
    using(var bad=new HttpRequestMessage(HttpMethod.Put,"/api/settings"){Content=new StringContent(Json.Write(new Dictionary<string,string>{["compiler"]=compilerPath,["parallelism"]="9"}),Encoding.UTF8,"application/json")})
        Assert((await client.SendAsync(bad)).StatusCode==HttpStatusCode.BadRequest,"settings reject parallelism out of range");
    await Request("PUT","/settings",new Dictionary<string,string>{["compiler"]=compilerPath,["parallelism"]="3"});
    Assert((await Request("GET","/environment"))["parallelism"]!.GetValue<int>()==3,"environment reports parallelism");
    var sleeper=Examples.All()[0] with {Id="",Title="Parallel check",Limits=new(){CpuMs=5000,WallMs=20000},Tests=[new("01","1\n","42\n")]};
    sleeper.Id=(await Request("POST","/problems",sleeper))["id"]!.GetValue<string>();
    string sleeperSource="#include <thread>\n#include <chrono>\n#include <iostream>\nint main(){std::this_thread::sleep_for(std::chrono::milliseconds(1500));std::cout<<42<<std::endl;}";
    static bool Dispatched(string status)=>status is "Compiling" or "Running";
    string first=(await Request("POST","/submissions",new{problemId=sleeper.Id,source=sleeperSource}))["id"]!.GetValue<string>();
    string second=(await Request("POST","/submissions",new{problemId=sleeper.Id,source=sleeperSource}))["id"]!.GetValue<string>();
    bool overlap=false;
    for(int i=0;i<200;i++)
    {
        string st1=(await Request("GET","/submissions/"+first))["submission"]!["status"]!.GetValue<string>();
        string st2=(await Request("GET","/submissions/"+second))["submission"]!["status"]!.GetValue<string>();
        if(Dispatched(st1)&&Dispatched(st2)){overlap=true;break;}
        if(!JudgeQueue.IsActive(st1)&&!JudgeQueue.IsActive(st2))break;
        await Task.Delay(100);
    }
    Assert(overlap,"two submissions run concurrently");
    Assert((await Wait(first))["result"]!["status"]!.GetValue<string>()=="AC","parallel sleeper A returns AC");
    Assert((await Wait(second))["result"]!["status"]!.GetValue<string>()=="AC","parallel sleeper B returns AC");
    await Request("PUT","/settings",new Dictionary<string,string>{["compiler"]=compilerPath,["parallelism"]="1"});
    string solo=(await Request("POST","/submissions",new{problemId=sleeper.Id,source=sleeperSource}))["id"]!.GetValue<string>();
    string follower=(await Request("POST","/submissions",new{problemId=sleeper.Id,source=sleeperSource}))["id"]!.GetValue<string>();
    bool violated=false;bool followerStarted=false;
    for(int i=0;i<300;i++)
    {
        string st1=(await Request("GET","/submissions/"+solo))["submission"]!["status"]!.GetValue<string>();
        string st2=(await Request("GET","/submissions/"+follower))["submission"]!["status"]!.GetValue<string>();
        if(Dispatched(st1)&&st2!="Queued")violated=true;
        if(Dispatched(st2)){followerStarted=true;break;}
        if(!JudgeQueue.IsActive(st1)&&!JudgeQueue.IsActive(st2))break;
        await Task.Delay(100);
    }
    Assert(!violated&&followerStarted,"parallelism 1 keeps later submission queued");
    Assert((await Wait(solo))["result"]!["status"]!.GetValue<string>()=="AC","serial queue A returns AC");
    Assert((await Wait(follower))["result"]!["status"]!.GetValue<string>()=="AC","serial queue B returns AC");
    }
    p.Limits=new(){CpuMs=30000,WallMs=40000};await Request("PUT","/problems/"+p.Id,p);
    string hang="#include <thread>\n#include <chrono>\nint main(){std::this_thread::sleep_for(std::chrono::milliseconds(30000));}";
    string cancel=(await Request("POST","/submissions",new{problemId=p.Id,source=hang}))["id"]!.GetValue<string>();
    Assert((await Wait(cancel,"Running"))["submission"]!["status"]!.GetValue<string>()=="Running","cancel fixture reaches running");
    await Request("POST","/submissions/"+cancel+"/cancel");Assert((await Wait(cancel))["result"]!["status"]!.GetValue<string>()=="Cancelled","cancel running submission");
    var cancelResult=new JudgeResult();
    for(int i=0;i<100;i++){cancelResult=Json.Read<JudgeResult>(File.ReadAllText(Path.Combine(evidence,"submissions",cancel,"result.json")));if(cancelResult.Status=="Cancelled")break;await Task.Delay(100);}
    Assert(cancelResult.Status=="Cancelled","worker observes cancellation");
    string interrupted=(await Request("POST","/submissions",new{problemId=p.Id,source=hang}))["id"]!.GetValue<string>();await Wait(interrupted,"Running");
    // Kill only the server itself: the Worker must notice parent death and release its Jobs.
    server!.Kill();await server.WaitForExitAsync();await Task.Delay(1000);
    Assert(!Process.GetProcessesByName("solution").Any(p => { try { return p.MainModule?.FileName.StartsWith(evidence,StringComparison.OrdinalIgnoreCase)==true; } finally { p.Dispose(); } }),"server death leaves no contestant processes");
    await Start();
    Assert((await Request("GET","/submissions/"+interrupted))["result"]!["status"]!.GetValue<string>()=="Interrupted","restart marks interrupted submission");
    using(var req=new HttpRequestMessage(HttpMethod.Post,"/api/submissions")){req.Headers.Add("Origin","https://example.org");req.Content=new StringContent("{}",Encoding.UTF8,"application/json");Assert((await client.SendAsync(req)).StatusCode==HttpStatusCode.Forbidden,"reject foreign browser origin");}
    // 远程访问安全面：独立实例 + Host 白名单 + 提交限流 + 用户账号体系（隔离 data 目录，避开 server.lock）
    int PickPort() { var l=new TcpListener(IPAddress.Loopback,0); l.Start(); int p=((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    cfPort=PickPort();
    cfTask=Task.Run(async () =>
    {
        using var listener=new TcpListener(IPAddress.Loopback,cfPort);
        listener.Start();
        while(!cfCts.IsCancellationRequested)
        {
            TcpClient connection;
            try { connection=await listener.AcceptTcpClientAsync(cfCts.Token); } catch(OperationCanceledException) { break; }
            try
            {
                using(connection)
                using(var stream=connection.GetStream())
                {
                    var buffer=new byte[8192];
                    int read=await stream.ReadAsync(buffer,cfCts.Token);
                    string head=Encoding.ASCII.GetString(buffer,0,read);
                    string query=head.Contains("/user.info")?head.Split(' ')[1].Split("?handles=")[^1].Split('&')[0]:"";
                    var names=Uri.UnescapeDataString(query).Split(';',StringSplitOptions.RemoveEmptyEntries);
                    var body=Encoding.UTF8.GetBytes("{\"status\":\"OK\",\"result\":["+string.Join(",",names.Select(h=>"{\"handle\":\""+h+"\",\"rating\":"+cfRating+",\"rank\":\"specialist\",\"firstName\":null,\"lastName\":\""+cfLastName+"\"}"))+"]}");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n"),cfCts.Token);
                    await stream.WriteAsync(body,cfCts.Token);
                }
            }
            catch(OperationCanceledException) { break; }
            catch(Exception) { /* 单个连接失败不影响打桩服务 */ }
        }
    });
    authServer=await Start(27125,new Dictionary<string,string>{["ACM_RATE_LIMIT"]="2",["ACM_ALLOWED_HOSTS"]="judge.example.com",["ACM_TOKEN"]="unit-test-token",["ACM_DATA"]=Path.Combine(evidence,"auth-data"),["ACM_CF_API"]="http://127.0.0.1:"+cfPort});
    using var authClient=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri("http://127.0.0.1:27125"),Timeout=TimeSpan.FromSeconds(30)};
    async Task<HttpResponseMessage> Send(string method,string path,string? host=null,string? origin=null,string? bearer=null)
    {
        var req=new HttpRequestMessage(new HttpMethod(method),"http://127.0.0.1:27125"+path);
        if(host!=null)req.Headers.Host=host; if(origin!=null)req.Headers.Add("Origin",origin); if(bearer!=null)req.Headers.Add("Authorization","Bearer "+bearer);
        return await authClient.SendAsync(req);
    }
    Assert((await Send("GET","/api/problems")).StatusCode==HttpStatusCode.Unauthorized,"remote request without token is rejected");
    using(var wrongAuth=new HttpRequestMessage(HttpMethod.Post,"http://127.0.0.1:27125/api/auth"){Content=new StringContent(Json.Write(new{token="bad"}),Encoding.UTF8,"application/json")})
        Assert((await authClient.SendAsync(wrongAuth)).StatusCode==HttpStatusCode.Unauthorized,"wrong token rejected");
    using(var okAuth=new HttpRequestMessage(HttpMethod.Post,"http://127.0.0.1:27125/api/auth"){Content=new StringContent(Json.Write(new{token="unit-test-token"}),Encoding.UTF8,"application/json")})
        Assert((await authClient.SendAsync(okAuth)).StatusCode==HttpStatusCode.OK,"correct token sets session cookie");
    Assert((await Send("GET","/api/problems")).StatusCode==HttpStatusCode.OK,"session cookie grants API access");
    Assert((await Send("GET","/api/problems",bearer:"unit-test-token")).StatusCode==HttpStatusCode.OK,"bearer token grants access");
    Assert((await Send("GET","/api/problems",host:"judge.example.com")).StatusCode==HttpStatusCode.OK,"allowlisted host accepted");
    Assert((await Send("GET","/api/problems",host:"evil.example.net")).StatusCode==HttpStatusCode.Forbidden,"unknown host rejected");
    Assert((await Send("GET","/api/problems",origin:"https://judge.example.com")).StatusCode==HttpStatusCode.OK,"allowlisted origin accepted");
    Assert((await Send("GET","/api/problems",origin:"https://evil.example.net")).StatusCode==HttpStatusCode.Forbidden,"foreign origin rejected");
    // —— 用户账号体系：注册 / 唯一性 / 登录 / 会话 / 匿名开关 / CF 挑战绑定 / 双榜 / 管理端 ——
    using var userClient=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri("http://127.0.0.1:27125"),Timeout=TimeSpan.FromSeconds(30)};
    async Task<HttpResponseMessage> SendUser(string method,string path,object? body=null)
    {
        var req=new HttpRequestMessage(new HttpMethod(method),"http://127.0.0.1:27125"+path);
        if(body!=null)req.Content=new StringContent(Json.Write(body),Encoding.UTF8,"application/json");
        return await userClient.SendAsync(req);
    }
    using(var anonMe=await SendUser("GET","/api/users/me"))
        Assert(JsonNode.Parse(await anonMe.Content.ReadAsStringAsync())!["user"]==null,"anonymous me probe returns a null user");
    Assert((await SendUser("POST","/api/users/register",new{studentId="230001",name="张三",email="zhang@stu.edu.cn",password="123"})).StatusCode==HttpStatusCode.BadRequest,"weak password rejected");
    Assert((await SendUser("POST","/api/users/register",new{studentId="23a001",name="甲",email="a@stu.edu.cn",password="pass123456"})).StatusCode==HttpStatusCode.BadRequest,"non-numeric student id rejected");
    Assert((await SendUser("POST","/api/users/register",new{studentId="23",name="乙",email="b@stu.edu.cn",password="pass123456"})).StatusCode==HttpStatusCode.BadRequest,"too-short student id rejected");
    string zhangId="";
    using(var reg=await SendUser("POST","/api/users/register",new{studentId="230001",name="张三",email="zhang@stu.edu.cn",password="pass123456"}))
    {
        Assert(reg.StatusCode==HttpStatusCode.OK,"registration succeeds and signs in");
        zhangId=JsonNode.Parse(await reg.Content.ReadAsStringAsync())!["user"]!["id"]!.GetValue<string>();
    }
    Assert((await SendUser("POST","/api/users/register",new{studentId="230001",name="张三二",email="other@stu.edu.cn",password="pass123456"})).StatusCode==HttpStatusCode.BadRequest,"duplicate student id rejected");
    Assert((await SendUser("POST","/api/users/register",new{studentId="230009",name="李四",email="zhang@stu.edu.cn",password="pass123456"})).StatusCode==HttpStatusCode.BadRequest,"duplicate email rejected");
    Assert((await SendUser("POST","/api/users/login",new{studentId="230001",password="wrong-pass"})).StatusCode==HttpStatusCode.Unauthorized,"wrong password rejected");
    Assert((await SendUser("POST","/api/users/login",new{studentId="999999",password="pass123456"})).StatusCode==HttpStatusCode.Unauthorized,"unknown student id rejected");
    using(var me=await SendUser("GET","/api/users/me"))
        Assert(JsonNode.Parse(await me.Content.ReadAsStringAsync())!["user"]!["name"]!.GetValue<string>()=="张三","session cookie resolves the account");
    using(var anonymize=await SendUser("PUT","/api/users/me",new{name="张三",email="zhang@stu.edu.cn",anonymous=true}))
        Assert(JsonNode.Parse(await anonymize.Content.ReadAsStringAsync())!["user"]!["anonymous"]!.GetValue<bool>(),"anonymity toggle stored");
    // Codeforces 挑战绑定：错误验证码被拒；把 CF 资料 lastName 改成验证码后通过并拉到 rating
    string code="";
    using(var challenge=await SendUser("POST","/api/users/me/codeforces",new{handle="cf_zhang"}))
    {
        challenge.EnsureSuccessStatusCode();
        code=JsonNode.Parse(await challenge.Content.ReadAsStringAsync())!["code"]!.GetValue<string>();
    }
    Assert(code.Length==6,"challenge code issued");
    Assert((await SendUser("POST","/api/users/me/codeforces/verify",new{code="WRONG1"})).StatusCode==HttpStatusCode.BadRequest,"wrong challenge code rejected");
    cfLastName=code; // 模拟用户把 CF 资料 Last name 改成验证码
    using(var verify=await SendUser("POST","/api/users/me/codeforces/verify",new{code}))
    {
        Assert(verify.StatusCode==HttpStatusCode.OK,"challenge verification binds the handle");
        var bound=JsonNode.Parse(await verify.Content.ReadAsStringAsync())!["user"]!;
        Assert(bound["cfHandle"]!.GetValue<string>()=="cf_zhang"&&bound["cfRating"]!.GetValue<int>()==2100&&bound["cfRank"]!.GetValue<string>()=="specialist","binding stores handle, rating and rank");
    }
    using(var board=await SendUser("GET","/api/leaderboard"))
    {
        var data=JsonNode.Parse(await board.Content.ReadAsStringAsync())!;
        var cfRow=data["cf"]!.AsArray().Single(n=>n!["userId"]!.GetValue<string>()==zhangId);
        Assert(cfRow["rank"]!.GetValue<int>()==1&&cfRow["anonymous"]!.GetValue<bool>()&&cfRow["name"]!.GetValue<string>()==""&&cfRow["cfHandle"]!.GetValue<string>()=="","anonymous CF entry keeps rank and hides identity");
    }
    await SendUser("POST","/api/users/logout");
    string liId="";
    using(var regB=await SendUser("POST","/api/users/register",new{studentId="230002",name="李四",email="li@stu.edu.cn",password="pass123456"}))
    {
        regB.EnsureSuccessStatusCode();
        liId=JsonNode.Parse(await regB.Content.ReadAsStringAsync())!["user"]!["id"]!.GetValue<string>();
    }
    Assert((await SendUser("POST","/api/users/me/codeforces",new{handle="CF_ZHANG"})).StatusCode==HttpStatusCode.BadRequest,"second binding of the same handle rejected");
    using(var boardB=await SendUser("GET","/api/leaderboard"))
        Assert(JsonNode.Parse(await boardB.Content.ReadAsStringAsync())!["solved"]!.AsArray().Count==2,"solved board lists every account");
    await SendUser("POST","/api/users/logout");
    using(var relogin=await SendUser("POST","/api/users/login",new{studentId="230001",password="pass123456"})) relogin.EnsureSuccessStatusCode();
    using(var unmask=await SendUser("PUT","/api/users/me",new{anonymous=false}))
        Assert(JsonNode.Parse(await unmask.Content.ReadAsStringAsync())!["user"]!["anonymous"]!.GetValue<bool>()==false,"anonymity can be turned off");
    using(var boardC=await SendUser("GET","/api/leaderboard"))
    {
        var dataC=JsonNode.Parse(await boardC.Content.ReadAsStringAsync())!;
        var row=dataC["cf"]!.AsArray().First(n=>n!["userId"]!.GetValue<string>()==zhangId);
        Assert(row["name"]!.GetValue<string>()=="张三"&&row["cfHandle"]!.GetValue<string>()=="cf_zhang","revealed entry shows name and handle");
    }
    Assert((await SendUser("GET","/api/users")).StatusCode==HttpStatusCode.Unauthorized,"user list rejects plain users");
    using(var adminList=await Send("GET","/api/users",bearer:"unit-test-token"))
        Assert(JsonNode.Parse(await adminList.Content.ReadAsStringAsync())!.AsArray().Count==2,"token lists registered users");
    using(var del=await Send("DELETE","/api/users/"+liId,bearer:"unit-test-token"))
        Assert(del.StatusCode==HttpStatusCode.NoContent,"token deletes a user");
    Assert((await SendUser("POST","/api/users/login",new{studentId="230002",password="pass123456"})).StatusCode==HttpStatusCode.Unauthorized,"deleted account cannot sign in");
    await SendUser("POST","/api/users/logout");
    using(var afterLogout=await SendUser("GET","/api/users/me"))
        Assert(JsonNode.Parse(await afterLogout.Content.ReadAsStringAsync())!["user"]==null,"logout clears the session");
    async Task<HttpResponseMessage> SubmitAuth()
    {
        var req=new HttpRequestMessage(HttpMethod.Post,"http://127.0.0.1:27125/api/submissions");
        req.Headers.Host="judge.example.com";
        req.Content=new StringContent(Json.Write(new{problemId="does-not-exist",source="int main(){}"}),Encoding.UTF8,"application/json");
        return await authClient.SendAsync(req);
    }
    Assert((await SubmitAuth()).StatusCode==HttpStatusCode.NotFound,"rate limit lets the first submissions through");
    Assert((await SubmitAuth()).StatusCode==HttpStatusCode.NotFound,"rate limit lets the second submission through");
    Assert((await SubmitAuth()).StatusCode==HttpStatusCode.TooManyRequests,"rate limit blocks the third submission");
    authServer.Kill();await authServer.WaitForExitAsync();authServer.Dispose();authServer=null;
    // 选手 OJ 端：全站强制登录 + 发布可见性 —— 未发布题目对选手完全隐形，发布后立即出现在题库与比赛里。
    // 绑定 0.0.0.0 并经本机局域网 IP 连接，让请求真正走"远程选手"路径（回环会被视为本机管理员）。
    var lanIp=(await Dns.GetHostEntryAsync(Dns.GetHostName())).AddressList.FirstOrDefault(a=>a.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a)) ?? throw new Exception("no LAN IPv4 for remote simulation");
    ojServer=await Start(27126,new Dictionary<string,string>{["ACM_HOST"]="0.0.0.0",["ACM_ALLOWED_HOSTS"]="oj.example.edu,"+lanIp.ToString(),["ACM_TOKEN"]="oj-test-token",["ACM_DATA"]=Path.Combine(evidence,"oj-data")});
    using var ojClient=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://{lanIp}:27126"),Timeout=TimeSpan.FromSeconds(60)};
    async Task<HttpResponseMessage> SendOj(string method,string path,string? host="oj.example.edu",string? bearer=null,object? body=null)
    {
        var req=new HttpRequestMessage(new HttpMethod(method),$"{ojClient.BaseAddress.ToString().TrimEnd('/')}{path}");
        if(host!=null)req.Headers.Host=host; if(bearer!=null)req.Headers.Add("Authorization","Bearer "+bearer);
        if(body!=null)req.Content=new StringContent(Json.Write(body),Encoding.UTF8,"application/json");
        return await ojClient.SendAsync(req);
    }
    var ojProblem=Examples.All()[0] with {Id="",Title="OJ public view",Published=false,Checker="int main(){return 0;}",Tests=[new("01","20 22\n","42\n"),new("02","40 44\n","84\n")]};
    using(var okOj=await SendOj("POST","/api/problems",bearer:"oj-test-token",body:ojProblem))
    {
        okOj.EnsureSuccessStatusCode();
        ojProblem.Id=JsonNode.Parse(await okOj.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
    }
    using(var anonList=await SendOj("GET","/api/problems"))
        Assert(anonList.StatusCode==HttpStatusCode.Unauthorized,"OJ rejects anonymous problem lists");
    using(var register=await SendOj("POST","/api/users/register",body:new{studentId="240001",name="赵六",email="zhao@stu.edu.cn",password="pass123456"}))
        Assert(register.StatusCode==HttpStatusCode.OK,"remote registration signs the contestant in");
    using(var draftList=await SendOj("GET","/api/problems"))
        Assert(draftList.StatusCode==HttpStatusCode.OK&&!JsonNode.Parse(await draftList.Content.ReadAsStringAsync())!.AsArray().Any(n=>n!["id"]!.GetValue<string>()==ojProblem.Id),"unpublished problems stay out of the contestant list");
    using(var hiddenDetail=await SendOj("GET","/api/problems/"+ojProblem.Id))
        Assert(hiddenDetail.StatusCode==HttpStatusCode.NotFound,"unpublished problem detail is invisible to contestants");
    using(var hiddenSubmit=await SendOj("POST","/api/submissions",body:new{problemId=ojProblem.Id,source=Examples.Sum}))
        Assert(hiddenSubmit.StatusCode==HttpStatusCode.NotFound,"unpublished problems reject contestant submissions");
    string ojContest="";
    using(var ojContestCreate=await SendOj("POST","/api/contests",bearer:"oj-test-token",body:new{title="OJ 公开赛",problemIds=new[]{ojProblem.Id}}))
    {
        ojContestCreate.EnsureSuccessStatusCode();
        ojContest=JsonNode.Parse(await ojContestCreate.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
    }
    using(var draftContest=await SendOj("GET","/api/contests"))
    {
        var row=JsonNode.Parse(await draftContest.Content.ReadAsStringAsync())!.AsArray().First(n=>n!["id"]!.GetValue<string>()==ojContest)!;
        Assert(row["count"]!.GetValue<int>()==0&&row["problemIds"]!.AsArray().Count==0,"contest membership hides unpublished problems");
    }
    ojProblem.Published=true;
    using(var publish=await SendOj("PUT","/api/problems/"+ojProblem.Id,bearer:"oj-test-token",body:ojProblem)) publish.EnsureSuccessStatusCode();
    using(var userList=await SendOj("GET","/api/problems"))
        Assert(userList.StatusCode==HttpStatusCode.OK&&JsonNode.Parse(await userList.Content.ReadAsStringAsync())!.AsArray().Any(n=>n!["id"]!.GetValue<string>()==ojProblem.Id),"publishing makes the problem visible to contestants");
    using(var liveContest=await SendOj("GET","/api/contests"))
    {
        var row=JsonNode.Parse(await liveContest.Content.ReadAsStringAsync())!.AsArray().First(n=>n!["id"]!.GetValue<string>()==ojContest)!;
        Assert(row["count"]!.GetValue<int>()==1,"publishing puts the problem back into its contest");
    }
    // —— 比赛时间窗（AtCoder 式）：未开赛的题目对选手完全隐形，开赛即自动公布 ——
    string futureContest="";
    using(var fc=await SendOj("POST","/api/contests",bearer:"oj-test-token",body:new{title="未来赛",problemIds=new[]{ojProblem.Id},startsAt=DateTimeOffset.UtcNow.AddHours(1).ToString("O"),endsAt=DateTimeOffset.UtcNow.AddHours(2).ToString("O")}))
    {
        fc.EnsureSuccessStatusCode();
        futureContest=JsonNode.Parse(await fc.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
    }
    using(var hiddenList=await SendOj("GET","/api/problems"))
        Assert(!JsonNode.Parse(await hiddenList.Content.ReadAsStringAsync())!.AsArray().Any(n=>n!["id"]!.GetValue<string>()==ojProblem.Id),"upcoming contest hides its problems");
    using(var hiddenDetail=await SendOj("GET","/api/problems/"+ojProblem.Id))
        Assert(hiddenDetail.StatusCode==HttpStatusCode.NotFound,"upcoming contest problem detail is invisible");
    using(var hiddenSubmit=await SendOj("POST","/api/submissions",body:new{problemId=ojProblem.Id,source=Examples.Sum}))
        Assert(hiddenSubmit.StatusCode==HttpStatusCode.NotFound,"upcoming contest rejects submissions");
    using(var upcomingRow=await SendOj("GET","/api/contests"))
    {
        var row=JsonNode.Parse(await upcomingRow.Content.ReadAsStringAsync())!.AsArray().First(n=>n!["id"]!.GetValue<string>()==futureContest)!;
        Assert(row["phase"]!.GetValue<string>()=="upcoming"&&row["count"]!.GetValue<int>()==0,"upcoming contest reports phase and hides roster");
    }
    using(var earlyBoard=await SendOj("GET","/api/contests/"+futureContest+"/standings"))
        Assert(earlyBoard.StatusCode==HttpStatusCode.BadRequest,"standings stay closed before the contest starts");
    await SendOj("PUT","/api/contests/"+futureContest,bearer:"oj-test-token",body:new{title="未来赛",problemIds=new[]{ojProblem.Id},startsAt=DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),endsAt=DateTimeOffset.UtcNow.AddMinutes(30).ToString("O"),rated=false});
    using(var startedList=await SendOj("GET","/api/problems"))
        Assert(JsonNode.Parse(await startedList.Content.ReadAsStringAsync())!.AsArray().Any(n=>n!["id"]!.GetValue<string>()==ojProblem.Id),"a started contest reveals its problems");
    using(var startedBoard=await SendOj("GET","/api/contests/"+futureContest+"/standings"))
        Assert(startedBoard.StatusCode==HttpStatusCode.OK&&JsonNode.Parse(await startedBoard.Content.ReadAsStringAsync())!["phase"]!.GetValue<string>()=="running","a started contest opens live standings");
    string ojDetail="";
    using(var userProblem=await SendOj("GET","/api/problems/"+ojProblem.Id))
    {
        Assert(userProblem.StatusCode==HttpStatusCode.OK,"signed-in contestant sees problem detail");
        ojDetail=await userProblem.Content.ReadAsStringAsync();
    }
    var userView=JsonNode.Parse(ojDetail)!;
    Assert(userView["checker"]!.GetValue<string>()==""&&userView["wrongSource"]==null,"public view strips judge tools; wrong source no longer exists");
    Assert(userView["exampleSource"]!.GetValue<string>()!=ojProblem.ExampleSource&&userView["exampleSource"]!.GetValue<string>().Contains("在这里实现你的解法"),"contestant editor receives a scaffold instead of the reference solution");
    Assert(userView["tests"]!.AsArray().Count==2&&userView["tests"]![0]!["input"]!.GetValue<string>()=="20 22\n"&&userView["tests"]![1]!["input"]!.GetValue<string>()==""&&userView["tests"]![1]!["answer"]!.GetValue<string>()=="","public view keeps only the first test as sample");
    using(var adminView=await SendOj("GET","/api/problems/"+ojProblem.Id,bearer:"oj-test-token"))
        Assert(JsonNode.Parse(await adminView.Content.ReadAsStringAsync())!["checker"]!.GetValue<string>()=="int main(){return 0;}","admin view keeps judge tools");
    using(var userEnv=await SendOj("GET","/api/environment"))
        Assert(JsonNode.Parse(await userEnv.Content.ReadAsStringAsync())!["compiler"]!.GetValue<string>()=="","public view hides compiler path");
    using(var ojSubmit=await SendOj("POST","/api/submissions",body:new{problemId=ojProblem.Id,source=Examples.Sum}))
    {
        ojSubmit.EnsureSuccessStatusCode();
        string ojSubmission=JsonNode.Parse(await ojSubmit.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        Assert(ojSubmission.Length==32,"OJ accepts contestant submissions with attribution");
        using(var userRejudge=await SendOj("POST","/api/submissions/"+ojSubmission+"/rejudge"))
            Assert(userRejudge.StatusCode==HttpStatusCode.Unauthorized,"rejudge rejects plain users");
    }
    using(var userPut=await SendOj("PUT","/api/problems/"+ojProblem.Id,body:ojProblem))
        Assert(userPut.StatusCode==HttpStatusCode.Unauthorized,"management endpoints reject plain users");
    using(var userExport=await SendOj("GET","/api/problems/"+ojProblem.Id+"/export"))
        Assert(userExport.StatusCode==HttpStatusCode.Unauthorized,"export rejects plain users");
    ojServer.Kill();await ojServer.WaitForExitAsync();ojServer.Dispose();ojServer=null;
    // 主实例：注册用户的提交归属与校内解题榜计数
    using var memberClient=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=client.BaseAddress,Timeout=TimeSpan.FromSeconds(60)};
    using(var memberReg=await memberClient.PostAsync("/api/users/register",new StringContent(Json.Write(new{studentId="230100",name="王五",email="wang@stu.edu.cn",password="pass123456"}),Encoding.UTF8,"application/json")))
        memberReg.EnsureSuccessStatusCode();
    var memberProblem=Examples.All()[0] with {Id="",Title="Member AC",Tests=[new("01","20 22\n","42\n")]};
    memberProblem.Id=(await Request("POST","/problems",memberProblem))["id"]!.GetValue<string>();
    using(var memberSubmit=new HttpRequestMessage(HttpMethod.Post,"/api/submissions"){Content=new StringContent(Json.Write(new{problemId=memberProblem.Id,source=Examples.Sum}),Encoding.UTF8,"application/json")})
    using(var memberResponse=await memberClient.SendAsync(memberSubmit))
    {
        memberResponse.EnsureSuccessStatusCode();
        string memberSubmission=JsonNode.Parse(await memberResponse.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        Assert((await Wait(memberSubmission))["result"]!["status"]!.GetValue<string>()=="AC","member submission reaches AC");
        var row=(await Request("GET","/submissions")).AsArray().First(n=>n!["id"]!.GetValue<string>()==memberSubmission)!;
        Assert(row["userName"]!.GetValue<string>()=="王五","submission history carries the author name");
        var board=(await Request("GET","/leaderboard"))["solved"]!.AsArray().First(n=>n!["name"]!.GetValue<string>()=="王五");
        Assert(board!["solvedCount"]!.GetValue<int>()==1&&board["submissionCount"]!.GetValue<int>()==1,"solved leaderboard counts the member's distinct AC");
    }
    // —— ICPC 排行榜 + 校内 rating 结算 + 源码可见性 + 提交筛选 ——
    var sp=Examples.All()[0] with {Id="",Title="Standings P",Published=true,Rating=800,Tests=[new("01","20 22\n","42\n")]};
    sp.Id=(await Request("POST","/problems",sp))["id"]!.GetValue<string>();
    string ratedContest=(await Request("POST","/contests",new{title="结算赛",problemIds=new[]{sp.Id},startsAt=DateTimeOffset.UtcNow.AddSeconds(-30).ToString("O"),endsAt=DateTimeOffset.UtcNow.AddMinutes(3).ToString("O"),rated=true}))["id"]!.GetValue<string>();
    using var peerClient=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=client.BaseAddress,Timeout=TimeSpan.FromSeconds(60)};
    using(var peerReg=await peerClient.PostAsync("/api/users/register",new StringContent(Json.Write(new{studentId="230101",name="赵七",email="qi@stu.edu.cn",password="pass123456"}),Encoding.UTF8,"application/json"))) peerReg.EnsureSuccessStatusCode();
    async Task<string> SubmitAs(HttpClient who,string source)
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,"/api/submissions"){Content=new StringContent(Json.Write(new{problemId=sp.Id,source}),Encoding.UTF8,"application/json")};
        using var response=await who.SendAsync(req); response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
    }
    string wangAc=await SubmitAs(memberClient,Examples.Sum);
    string peerWa=await SubmitAs(peerClient,"#include <iostream>\nint main(){std::cout<<41<<std::endl;}");
    string peerAc=await SubmitAs(peerClient,Examples.Sum);
    Assert((await Wait(wangAc))["result"]!["status"]!.GetValue<string>()=="AC","standings fixture: first contestant AC");
    Assert((await Wait(peerWa))["result"]!["status"]!.GetValue<string>()=="WA","standings fixture: second contestant WA first");
    Assert((await Wait(peerAc))["result"]!["status"]!.GetValue<string>()=="AC","standings fixture: second contestant then AC");
    var runningBoard=await Request("GET","/contests/"+ratedContest+"/standings");
    Assert(runningBoard["phase"]!.GetValue<string>()=="running"&&runningBoard["settled"]!.GetValue<bool>()==false,"standings stay unsettled while the contest runs");
    var runningRows=runningBoard["rows"]!.AsArray();
    Assert(runningRows.Count==2,"standings list every participant");
    Assert(runningRows[0]!["rank"]!.GetValue<int>()==1&&runningRows[0]["solved"]!.GetValue<int>()==1&&runningRows[0]["penalty"]!.GetValue<int>()==0,"clean solve ranks first with zero penalty");
    Assert(runningRows[1]!["rank"]!.GetValue<int>()==2&&runningRows[1]["penalty"]!.GetValue<int>()==20,"one wrong submission adds the 20 minute penalty");
    Assert(runningRows[1]["cells"]!.AsArray()[0]!["attempts"]!.GetValue<int>()==2,"standings count every attempt on the problem");
    // 提前收卷：把结束时间改到现在，让下一次访问排行榜触发结算。
    await Request("PUT","/contests/"+ratedContest,new{title="结算赛",problemIds=new[]{sp.Id},startsAt=DateTimeOffset.UtcNow.AddSeconds(-30).ToString("O"),endsAt=DateTimeOffset.UtcNow.ToString("O"),rated=true});
    var settledBoard=await Request("GET","/contests/"+ratedContest+"/standings");
    Assert(settledBoard["settled"]!.GetValue<bool>()==true,"finished rated contest auto-settles");
    var wangRow=settledBoard["rows"]!.AsArray().First(r=>r!["penalty"]!.GetValue<int>()==0)!;
    var peerRow=settledBoard["rows"]!.AsArray().First(r=>r!["penalty"]!.GetValue<int>()==20)!;
    Assert(wangRow["delta"]!.GetValue<int>()==200&&peerRow["delta"]!.GetValue<int>()==-200,"two-player Elo settles +200/-200 from the 1500 seed");
    using(var wangMe=await memberClient.GetAsync("/api/users/me"))
        Assert(JsonNode.Parse(await wangMe.Content.ReadAsStringAsync())!["user"]!["siteRating"]!.GetValue<int>()==1700,"the settled rating lands on the account");
    var siteBoard=(await Request("GET","/leaderboard"))["site"]!.AsArray();
    Assert(siteBoard.Count==2&&siteBoard[0]!["siteRating"]!.GetValue<int>()==1700,"the site rating leaderboard lists settled players in order");
    int ratingBefore=JsonNode.Parse(await (await memberClient.GetAsync("/api/users/me")).Content.ReadAsStringAsync())!["user"]!["siteRating"]!.GetValue<int>();
    await Request("GET","/contests/"+ratedContest+"/standings");
    int ratingAfter=JsonNode.Parse(await (await memberClient.GetAsync("/api/users/me")).Content.ReadAsStringAsync())!["user"]!["siteRating"]!.GetValue<int>();
    Assert(ratingBefore==ratingAfter,"settle is idempotent on repeated standings views");
    using(var peerViewsWang=await peerClient.GetAsync("/api/submissions/"+wangAc))
    {
        var view=JsonNode.Parse(await peerViewsWang.Content.ReadAsStringAsync())!;
        Assert(peerViewsWang.StatusCode==HttpStatusCode.OK&&view["source"]!.GetValue<string>()==""&&view["canViewSource"]!.GetValue<bool>()==false,"submission source is author-only");
    }
    using(var ownView=await memberClient.GetAsync("/api/submissions/"+wangAc))
        Assert(JsonNode.Parse(await ownView.Content.ReadAsStringAsync())!["canViewSource"]!.GetValue<bool>()==true,"the author still sees their own source");
    using(var foreignCancel=await peerClient.PostAsync("/api/submissions/"+wangAc+"/cancel",null))
        Assert(foreignCancel.StatusCode==HttpStatusCode.Forbidden,"a contestant cannot cancel another author's submission");
    using(var verifyRequest=new HttpRequestMessage(HttpMethod.Post,"/api/submissions"))
    {
        verifyRequest.Headers.Authorization=new("Bearer","main-test-token");
        verifyRequest.Content=new StringContent(Json.Write(new{problemId=sp.Id,source=Examples.Sum,purpose="verify"}),Encoding.UTF8,"application/json");
        using var verifyResponse=await memberClient.SendAsync(verifyRequest); verifyResponse.EnsureSuccessStatusCode();
        var verification=JsonNode.Parse(await verifyResponse.Content.ReadAsStringAsync())!;
        Assert(verification["userId"]==null,"admin verification is independent of a simultaneously signed-in contestant");
        Assert((await Wait(verification["id"]!.GetValue<string>()))["result"]!["status"]!.GetValue<string>()=="AC","admin verification still executes the complete problem");
    }
    using(var adminView=await client.GetAsync("/api/submissions/"+wangAc))
        Assert(JsonNode.Parse(await adminView.Content.ReadAsStringAsync())!["canViewSource"]!.GetValue<bool>()==true,"the token holder sees any source");
    using(var mineList=await peerClient.GetAsync("/api/submissions?mine=1"))
    {
        var rowsMine=JsonNode.Parse(await mineList.Content.ReadAsStringAsync())!.AsArray();
        Assert(rowsMine.Count>=2&&rowsMine.All(r=>r!["userName"]!.GetValue<string>()=="赵七"),"the mine filter scopes to the signed-in user");
    }
    using(var acList=await client.GetAsync("/api/submissions?status=AC&problemId="+sp.Id))
        Assert(JsonNode.Parse(await acList.Content.ReadAsStringAsync())!.AsArray().All(r=>r!["status"]!.GetValue<string>()=="AC"),"status and problem filters combine");
    // Real persisted histories larger than the default page must be filtered before limiting.
    var fixtureStore = new Store(root,evidence);
    for(int i=0;i<205;i++)
    {
        var filler=fixtureStore.AddSubmission(sp,"int main(){}",ServerSettings.Compiler(evidence));
        fixtureStore.Status(filler.Id,"WA");
    }
    using(var olderMine=await peerClient.GetAsync("/api/submissions?mine=1"))
        Assert(JsonNode.Parse(await olderMine.Content.ReadAsStringAsync())!.AsArray().Count==2,"mine filtering precedes the global 200-row window");
    using(var trainingResponse=await peerClient.GetAsync("/api/training"))
    {
        trainingResponse.EnsureSuccessStatusCode(); var training=JsonNode.Parse(await trainingResponse.Content.ReadAsStringAsync())!;
        Assert(training["submissionCount"]!.GetValue<int>()==2&&training["solvedProblemIds"]!.AsArray().Any(n=>n!.GetValue<string>()==sp.Id),"training overview uses complete personal history beyond the page window");
        Assert(training["activity"]!.AsArray().Count==7&&training["recent"]!.AsArray().Count==2,"training activity and recent attempts are scoped to the current user");
        Assert(!training.ToJsonString().Contains("source")&&!training.ToJsonString().Contains("answer"),"training response contains no solution source or answer data");
    }
    using(var anonymousTraining=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=client.BaseAddress})
        Assert((await anonymousTraining.GetAsync("/api/training")).StatusCode==HttpStatusCode.Unauthorized,"training overview requires authentication");
    Assert((await Request("GET","/submissions")).AsArray().Count>=4,"history persisted after restart");
    await Request("DELETE","/problems/"+p.Id);Assert((await Request("GET","/submissions/"+id))["result"]!["status"]!.GetValue<string>()=="AC","deleting problem preserves submissions");
    Console.WriteLine($"ALL {checks.Count} API/PACKAGE CHECKS PASSED; {evidence}");
    File.WriteAllText(Path.Combine(evidence,"report.json"),Json.Write(new{passed=checks.Count,checks}));
}
finally { cfCts.Cancel(); try { cfTask.Wait(2000); } catch { /* 打桩服务随取消退出 */ } cfCts.Dispose(); if(ojServer is {HasExited:false}) {ojServer.Kill(true);await ojServer.WaitForExitAsync();} ojServer?.Dispose(); if(authServer is {HasExited:false}) {authServer.Kill(true);await authServer.WaitForExitAsync();} authServer?.Dispose(); if(server is {HasExited:false}) {server.Kill(true);await server.WaitForExitAsync();}server?.Dispose(); }

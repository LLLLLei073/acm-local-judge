using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
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
    Assert(read with { Id=p.Id } == p || Json.Write(read with { Id=p.Id }) == Json.Write(p), p.Mode + " ZIP round trip");
    File.WriteAllBytes(Path.Combine(evidence, p.Mode + ".zip"), Packages.Export(p));
}
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
Process? server=null;
async Task Start()
{
    var si=new ProcessStartInfo("dotnet"){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    si.ArgumentList.Add(serverDll);si.Environment["ACM_ROOT"]=root;si.Environment["ACM_DATA"]=evidence;si.Environment["ACM_PORT"]=port.ToString();
    server=Process.Start(si)!;
    server.OutputDataReceived+=(_,e)=> { if(e.Data!=null) File.AppendAllText(Path.Combine(evidence,"server.log"),e.Data+"\n"); };
    server.ErrorDataReceived+=(_,e)=> { if(e.Data!=null) File.AppendAllText(Path.Combine(evidence,"server-error.log"),e.Data+"\n"); };
    server.BeginOutputReadLine();server.BeginErrorReadLine();
    using var probe=new HttpClient(new HttpClientHandler{UseProxy=false});
    for(int i=0;i<100;i++) { if(server.HasExited)throw new Exception("Server exited");try{if((await probe.GetAsync($"http://127.0.0.1:{port}/api/environment")).IsSuccessStatusCode)return;}catch(HttpRequestException){} await Task.Delay(100); }
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
    Assert((await Request("GET","/problems")).AsArray().Count==5,"five seeded examples");
    var flowProblem = FlowExample.Create();
    flowProblem.Id = (await Request("POST", "/problems", flowProblem))["id"]!.GetValue<string>();
    Assert((await Request("GET", "/problems/" + flowProblem.Id))["flow"]!["steps"]!.AsArray().Count == 5, "API persists visual flow");
    string flowSubmission = (await Request("POST", "/submissions", new { problemId = flowProblem.Id, source = flowProblem.ExampleSource }))["id"]!.GetValue<string>();
    Assert((await Wait(flowSubmission))["result"]!["status"]!.GetValue<string>() == "AC", "Worker executes visual flow");
    flowProblem.Flow!.Steps[3].Expected = new() { Text = "999" };
    await Request("PUT", "/problems/" + flowProblem.Id, flowProblem);
    string flowRepeat = (await Request("POST", "/submissions/" + flowSubmission + "/rejudge"))["id"]!.GetValue<string>();
    Assert((await Wait(flowRepeat))["result"]!["status"]!.GetValue<string>() == "AC", "rejudge freezes flow and expected values");
    flowProblem.Flow.Steps[0].To = 99;
    using (var invalidFlow = await client.PostAsJsonAsync("/api/problems", flowProblem, Json.Options))
        Assert(invalidFlow.StatusCode == HttpStatusCode.BadRequest, "API rejects missing flow member");
    var p=Examples.All()[0] with {Id="",Title="API acceptance",Tests=[new("01","20 22\n","42\n")]};
    var created=await Request("POST","/problems",p);p.Id=created["id"]!.GetValue<string>();
    Assert(p.Id.Length==32,"create problem");
    using(var form=new MultipartFormDataContent())
    {
        form.Add(new StringContent("4 5\n"),"files","02.in");form.Add(new StringContent("9\n"),"files","02.ans");
        var response=await client.PostAsync("/api/problems/"+p.Id+"/tests",form);response.EnsureSuccessStatusCode();p=Json.Read<Problem>(await response.Content.ReadAsStringAsync());
        Assert(p.Tests.Count==2,"upload paired tests");
    }
    using(var form=new MultipartFormDataContent())
    {
        form.Add(new StringContent("missing"),"files","03.in");var r=await client.PostAsync("/api/problems/"+p.Id+"/tests",form);Assert(r.StatusCode==HttpStatusCode.BadRequest,"reject unmatched upload");
    }
    var submission=await Request("POST","/submissions",new{problemId=p.Id,source=Examples.Sum});string id=submission["id"]!.GetValue<string>();
    using(var response=await client.GetAsync("/api/submissions/"+id+"/events",HttpCompletionOption.ResponseHeadersRead))
    {
        string stream=await response.Content.ReadAsStringAsync();File.WriteAllText(Path.Combine(evidence,"events.txt"),stream);Assert(stream.Contains("data:")&&stream.Contains("\"status\":\"AC\""),"SSE reaches terminal AC");
    }
    Assert((await Wait(id))["result"]!["status"]!.GetValue<string>()=="AC","real worker submission AC");
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
    string hang="#include <windows.h>\nint main(){Sleep(30000);}";
    string cancel=(await Request("POST","/submissions",new{problemId=p.Id,source=hang}))["id"]!.GetValue<string>();
    Assert((await Wait(cancel,"Running"))["submission"]!["status"]!.GetValue<string>()=="Running","cancel fixture reaches running");
    await Request("POST","/submissions/"+cancel+"/cancel");Assert((await Wait(cancel))["result"]!["status"]!.GetValue<string>()=="Cancelled","cancel running submission");
    await Task.Delay(600);
    var cancelResult=Json.Read<JudgeResult>(File.ReadAllText(Path.Combine(evidence,"submissions",cancel,"result.json")));Assert(cancelResult.Status=="Cancelled","worker observes cancellation");
    string interrupted=(await Request("POST","/submissions",new{problemId=p.Id,source=hang}))["id"]!.GetValue<string>();await Wait(interrupted,"Running");
    // Kill only the server itself: the Worker must notice parent death and release its Jobs.
    server!.Kill();await server.WaitForExitAsync();await Task.Delay(1000);
    Assert(!Process.GetProcessesByName("solution").Any(p => { try { return p.MainModule?.FileName.StartsWith(evidence,StringComparison.OrdinalIgnoreCase)==true; } finally { p.Dispose(); } }),"server death leaves no contestant processes");
    await Start();
    Assert((await Request("GET","/submissions/"+interrupted))["result"]!["status"]!.GetValue<string>()=="Interrupted","restart marks interrupted submission");
    using(var req=new HttpRequestMessage(HttpMethod.Post,"/api/submissions")){req.Headers.Add("Origin","https://example.org");req.Content=new StringContent("{}",Encoding.UTF8,"application/json");Assert((await client.SendAsync(req)).StatusCode==HttpStatusCode.Forbidden,"reject foreign browser origin");}
    Assert((await Request("GET","/submissions")).AsArray().Count>=4,"history persisted after restart");
    await Request("DELETE","/problems/"+p.Id);Assert((await Request("GET","/submissions/"+id))["result"]!["status"]!.GetValue<string>()=="AC","deleting problem preserves submissions");
    Console.WriteLine($"ALL {checks.Count} API/PACKAGE CHECKS PASSED; {evidence}");
    File.WriteAllText(Path.Combine(evidence,"report.json"),Json.Write(new{passed=checks.Count,checks}));
}
finally { if(server is {HasExited:false}) {server.Kill(true);await server.WaitForExitAsync();}server?.Dispose(); }

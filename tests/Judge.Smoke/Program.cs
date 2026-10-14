using Judge.Core;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string work = Path.Combine(root, "artifacts", "smoke-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(work);
int passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); passed++; }
foreach (var (a,b,ok) in new[] { (" 2\t3\n", "2 3", true), ("02", "2", false), ("2.0", "2", false), ("Yes", "yes", false), ("2 3", "2", false), ("", " \r\n", true), ("2\u00a03", "2 3", false) })
    Assert((Engine.Compare(a,b).Verdict == "AC") == ok, "token: " + a.Replace('\n',' '));
string compiler = Environment.GetEnvironmentVariable("ACM_COMPILER") ?? @"D:\mingw64\bin\g++.exe";
async Task<JudgeResult> Run(string name, Problem p, string source, CancellationToken ct = default)
{
    var r = await new Engine(new(name, p, source, compiler, Path.Combine(root, "toolkit"), Path.Combine(work, name))).Run(ct);
    Console.WriteLine($"{name}: {r.Status} {r.Message}");
    File.WriteAllText(Path.Combine(work, name + ".json"), Json.Write(r));
    if (r.Status is "CE" or "Judge Error") Console.WriteLine(r.CompileLog);
    return r;
}
foreach (var p in Examples.All())
{
    var r = await Run(p.Mode + "-good", p, p.ExampleSource);
    Assert(r.Status == "AC", p.Mode + " 正确程序 AC");
    var w = await Run(p.Mode + "-bad", p, p.WrongSource);
    Assert(w.Status == (p.Mode == JudgeMode.Interactive ? "TLE" : "WA"), p.Mode + " 错误程序");
    foreach (var c in r.Cases) Assert(c.CpuMs == c.Instances.Max(i=>i.Run.Usage.CpuMs) && c.MemoryBytes == c.Instances.Max(i=>i.Run.Usage.MemoryBytes), p.Mode + " 资源最大值");
}
var standard = Examples.All()[0] with { Tests = [new("01", "2 3\n", "5\n")] };
Assert((await Run("compile-error", standard, "not C++")).Status == "CE", "编译错误 CE");
Assert((await Run("runtime-error", standard, "int main(){return 7;}")).Status == "RE", "异常退出 RE");
Assert((await Run("cpu-limit", standard with { Limits = new() { CpuMs = 100, WallMs = 1000 } }, "int main(){for(;;){asm volatile(\"\");}}")).Status == "TLE", "CPU 超限 TLE");
Assert((await Run("wall-limit", standard with { Limits = new() { CpuMs = 100, WallMs = 300 } }, "#include <windows.h>\nint main(){Sleep(10000);}")).Status == "TLE", "墙钟超限 TLE");
Assert((await Run("output-limit", standard with { Limits = new() { OutputKiB = 1 } }, "#include <iostream>\nint main(){for(;;)std::cout<<\"0123456789abcdef\";}")).Status == "OLE", "输出超限 OLE");
Assert((await Run("memory-limit", standard with { Limits = new() { MemoryMiB = 16 } }, "#include <cstdlib>\n#include <cstring>\nint main(){for(;;){auto p=malloc(1024*1024);if(!p)return 5;memset(p,1,1024*1024);asm volatile(\"\"::\"r\"(p):\"memory\");}}")).Status == "MLE", "内存超限 MLE");
using var cancel = new CancellationTokenSource(); cancel.CancelAfter(300);
Assert((await Run("cancel", standard, Examples.Sum, cancel.Token)).Status == "Cancelled", "取消编译或运行");
Console.WriteLine($"ALL {passed} PASSED; evidence: {work}");
var spj = Examples.All()[1];
Assert((await Run("checker-pe", spj with { Checker = "int main(){return 2;}" }, spj.ExampleSource)).Status == "PE", "checker 格式错误 PE");
Assert((await Run("checker-crash", spj with { Checker = "int main(){return 42;}" }, spj.ExampleSource)).Status == "Judge Error", "checker 异常 Judge Error");
Assert((await Run("checker-timeout", spj with { Checker = "int main(){for(;;){asm volatile(\"\");}}", JudgeLimits = new() { CpuMs=100, WallMs=500 } }, spj.ExampleSource)).Status == "Judge Error", "checker 超时不归因选手");
Assert((await Run("exit-259", standard, "int main(){return 259;}")).Status == "RE", "合法退出码 259 不误判超时");
var interactive = Examples.All()[2];
Assert((await Run("interactive-early-exit", interactive, "int main(){return 7;}")).Status == "RE", "交互选手提前异常退出");
Assert((await Run("interactive-judge-crash", interactive with { Interactor="int main(){return 42;}" }, interactive.ExampleSource)).Status == "Judge Error", "交互器崩溃");
Assert((await Run("interactive-stderr", interactive with { Limits = new() { OutputKiB=1 } }, "#include <iostream>\nint main(){for(;;)std::cerr<<\"stderr overload\\n\";}")).Status == "OLE", "交互 stderr 超限");
var comm = Examples.All()[3];
string isolated = "#include <iostream>\n#include <fstream>\n#include <string>\nint main(){std::string p;int n;std::cin>>p>>n;if(p==\"first\"){std::ofstream(\"round-file\")<<123;std::cout<<n+1<<std::endl;}else{std::ifstream f(\"round-file\");if(f.good())return 9;std::cout<<2*n<<std::endl;}}";
Assert((await Run("communication-isolation", comm, isolated)).Status == "AC", "通信轮次目录独立");
string managed = "#include \"acm_manager.hpp\"\n#include <fstream>\nint main(){auto init=acm::receive();std::ifstream in(init[\"inputPath\"].get<std::string>()),ans(init[\"answerPath\"].get<std::string>());int x,expected;in>>x;ans>>expected;acm::start();acm::send(0,std::to_string(x)+\"\\n\");acm::close(0);std::string out;for(int r=1;r<=2;r++){while(true){auto e=acm::receive();if(e[\"type\"]==\"eof\")return 3;if(e[\"type\"]==\"output\")out+=acm::decode(e[\"data\"]);if(e[\"type\"]==\"exit\")break;}if(r==1){acm::start();acm::send(0,out);acm::close(0);out.clear();}}acm::verdict(std::stoi(out)==expected?\"AC\":\"WA\");}";
Assert((await Run("managed-communication", comm with { ManagedCommunication=true, Manager=managed }, comm.ExampleSource)).Status == "AC", "manager 双轮通信");
Assert((await Run("managed-invalid-order", comm with { ManagedCommunication=true, Manager="#include \"acm_manager.hpp\"\nint main(){acm::receive();acm::start();acm::start();}" }, "#include <windows.h>\nint main(){Sleep(10000);}")).Status == "Judge Error", "禁止并行启动通信轮次");
var multi = Examples.All()[4];
Assert((await Run("multi-one-fails", multi, multi.ExampleSource.Replace("int n;", "if(role == 1)return 7; int n;"))).Status == "RE", "多进程任一异常失败");
Assert((await Run("multi-one-timeout", multi with { Limits=new(){CpuMs=100,WallMs=1000} }, multi.ExampleSource.Replace("int n;", "if(role == 1)for(;;){asm volatile(\"\");} int n;"))).Status == "TLE", "多进程任一超时失败");
Assert((await Run("manager-protocol-error", multi with { Manager="#include <iostream>\nint main(){std::cout<<\"not json\"<<std::endl;}" }, multi.ExampleSource)).Status == "Judge Error", "非法 manager 协议");
Assert((await Run("standard-ignores-checker", standard with { Checker="int main(){return 0;}" }, Examples.Wrong)).Status == "WA", "标准题不能被 checker 绕过");
Console.WriteLine($"FINAL ALL {passed} PASSED; evidence: {work}");
Assert((await Run("invalid-utf8", standard with { Tests=[new("01","","\ufffd")] }, "#include <cstdio>\nint main(){putchar(255);}")).Status == "WA", "无效 UTF-8 不替换后误判 AC");
var post=interactive with { Checker="int main(){return 0;}", Interactor="#include \"testlib.h\"\n#include <iostream>\nint main(int argc,char**argv){registerInteraction(argc,argv);for(int i=0;i<3;i++){int x=inf.readInt();std::cout<<x<<std::endl;int y=ouf.readInt();if(y!=2*x)quitf(_wa,\"bad\");tout<<y<<'\\n';}quitf(_ok,\"ok\");}" };
Assert((await Run("interactive-post-checker",post,post.ExampleSource)).Status=="AC","交互后置 checker");
Assert((await Run("communication-round2-timeout",comm with {Limits=new(){CpuMs=100,WallMs=1000}},"#include <iostream>\n#include <string>\nint main(){std::string p;int x;std::cin>>p>>x;if(p==\"first\")std::cout<<x<<std::endl;else for(;;){asm volatile(\"\");}}")).Status=="TLE","通信第二轮超时");
Console.WriteLine($"FINAL ALL {passed} PASSED; evidence: {work}");
File.WriteAllText(Path.Combine(work,"report.json"),Json.Write(new{passed}));

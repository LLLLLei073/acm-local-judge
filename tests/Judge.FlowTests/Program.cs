using System.Text;
using Judge.Core;
using Judge.Server;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string work = Path.Combine(root, "artifacts", "flow-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(work);
string compiler = Environment.GetEnvironmentVariable("ACM_COMPILER") ?? (OperatingSystem.IsWindows() ? @"D:\mingw64\bin\g++.exe" : "/usr/bin/g++");
List<string> checks = [];
void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
Problem Fresh() => FlowExample.Create();
async Task<JudgeResult> Run(string name, Problem p, string source, string expected, CancellationToken token = default)
{
    var r = await new Engine(new(name, p, source, compiler, Path.Combine(root, "toolkit"), Path.Combine(work, name))).Run(token);
    File.WriteAllText(Path.Combine(work, name + ".json"), Json.Write(r));
    Console.WriteLine($"{name}: {r.Status} {r.Message}");
    Assert(r.Status == expected, name + " => " + expected);
    return r;
}
void Reject(string name, Action<Problem> mutate)
{
    var p = Fresh(); mutate(p); bool rejected = false;
    try { p.Validate(); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected, name);
}
Reject("duplicate member names", p => p.Flow!.Members[1] = p.Flow.Members[0]);
Reject("member count mismatch", p => p.ProcessCount = 3);
Reject("unknown member", p => p.Flow!.Steps[0].To = 2);
Reject("self relay", p => p.Flow!.Steps[2].To = 0);
Reject("future message reference after reorder", p => (p.Flow!.Steps[3], p.Flow.Steps[4]) = (p.Flow.Steps[4], p.Flow.Steps[3]));
Reject("deleted reference", p => p.Flow!.Steps.RemoveAt(3));
Reject("duplicate step id", p => p.Flow!.Steps[1].Id = p.Flow.Steps[0].Id);
Reject("missing verification", p => p.Flow!.Steps.ForEach(s => s.Check = false));
Reject("send to closed member", p => p.Flow!.Steps.Insert(0, new() { Kind = "close", To = 0 }));
Reject("invalid test input range", p => p.Flow!.Steps[0].Data.StartLine = 9);
Reject("unsupported version", p => p.Flow!.Version = 2);
Reject("null data part", p => p.Flow!.Steps[0].AdditionalData = [null!]);
Reject("enabled in standard mode", p => p.Mode = JudgeMode.Standard);
var problem = Fresh();
var zipped = Packages.Export(problem);
using (var zip = new MemoryStream(zipped)) Assert(Json.Write(await Packages.Import(zip, default)) == Json.Write(problem), "ZIP preserves ordered flow, members and samples");
File.WriteAllBytes(Path.Combine(root, "examples", "VisualFlow.zip"), zipped);
var store = new Store(root, Path.Combine(work, "store"));
var saved = store.Save(Fresh(), true);
var submission = store.AddSubmission(saved, saved.ExampleSource, compiler);
saved.Flow!.Members[0] = "renamed"; saved.Flow.Steps.Reverse();
var snapshot = Json.Read<JudgeRequest>(File.ReadAllText(Path.Combine(store.SubmissionDir(submission.Id), "request.json")));
Assert(snapshot.Problem.Flow!.Members[0] == "角色 0" && snapshot.Problem.Flow.Steps[0].Id == "give-a", "submission freezes members and order");
Assert(store.Problem(saved.Id)!.Flow!.Steps[0].Id == "give-a", "SQLite preserves flow");
var good = await Run("relay", problem, problem.ExampleSource, "AC");
Assert(good.Cases.Count == 2 && good.Cases.All(c => c.CpuMs == c.Instances.Max(r => r.Run.Usage.CpuMs) && c.MemoryBytes == c.Instances.Max(r => r.Run.Usage.MemoryBytes)), "multiple tests and maximum usage");
string Sent(CaseResult c, string to) => Encoding.UTF8.GetString(c.Trace.Where(t => t.From == "manager" && t.To == to).SelectMany(t => Convert.FromBase64String(t.DataBase64)).ToArray());
Assert(Sent(good.Cases[0], "1:0") == "3\n7\r\n" && Sent(good.Cases[0], "1:1") == "4\n3\r\n", "relay preserves CRLF exactly");
await Run("wrong-answer", Fresh(), problem.WrongSource, "WA");
await Run("early-exit", Fresh(), "int main(){}", "WA");
await Run("crash", Fresh(), "int main(){return 7;}", "RE");
await Run("extra-output", Fresh(), problem.ExampleSource.Replace("std::cout << b << std::endl;", "std::cout << b << std::endl << 99 << std::endl;"), "WA");
var quick = Fresh(); quick.Limits = new() { CpuMs = 500, WallMs = 1000 };
await Run("no-flush", quick, problem.ExampleSource.Replace("std::endl", "'\\n'"), "TLE");
await Run("no-newline", Fresh(), "#include <iostream>\nint main(){std::cout<<3<<std::flush;}", "WA");
await Run("invalid-utf8", Fresh(), "#include <cstdio>\nint main(){putchar(255);putchar(10);}", "WA");
await Run("cpu-limit", quick, "int main(){for(;;){asm volatile(\"\");}}", "TLE");
var small = Fresh(); small.Limits.OutputKiB = 1;
await Run("output-limit", small, "#include <cstdio>\nint main(){for(int i=0;i<10000;i++)putchar('x');}", "OLE");
await Run("compile-error", Fresh(), "not C++", "CE");
using (var cancel = new CancellationTokenSource())
{
    var task = new Engine(new("cancel", Fresh(), "#include <thread>\n#include <chrono>\nint main(){std::this_thread::sleep_for(std::chrono::milliseconds(9000));}", compiler, Path.Combine(root, "toolkit"), Path.Combine(work, "cancel")), r => { if (r.Status == "Running") cancel.CancelAfter(150); }).Run(cancel.Token);
    Assert((await task).Status == "Cancelled", "cancel flow session");
}
// A received line may arrive in multiple pipe callbacks.
var splitSource = problem.ExampleSource.Replace("#include <cstdlib>", "#include <cstdlib>\n#include <thread>\n#include <chrono>").Replace("std::cout << a << std::endl;", "std::cout << a << std::flush; std::this_thread::sleep_for(std::chrono::milliseconds(30)); std::cout << std::endl;");
await Run("fragmented-line", Fresh(), splitSource, "AC");
// A line much larger than the native pipe chunk must be consumed only once.
var large = Fresh(); string payload = new('x', 100000);
large.Tests = [new("long", payload + "\n0\n", payload + "\n")];
large.Flow!.Steps[3].Expected.Source = "answer";
await Run("long-line", large, "#include <iostream>\n#include <string>\n#include <cstdlib>\nint main(int,char**v){std::string a,b;std::getline(std::cin,a);if(atoi(v[1])==0){std::cout<<a<<std::endl;std::getline(std::cin,b);std::cout<<b<<std::endl;}else{std::getline(std::cin,b);std::cout<<b<<std::endl;}}", "AC");
var parts = Fresh();
parts.Flow!.Steps[1].Data = new() { Source = "literal", Text = " " };
parts.Flow.Steps[1].AdditionalData = [new() { Source = "input", StartLine = 2, LineCount = 1 }];
await Run("additional-data-parts", parts, problem.ExampleSource, "AC");
var close = Fresh(); close.Tests = [new("01", "3\n4\n", "7\n")];
close.Flow!.Steps = [new() { To = 0, Data = new() { Source = "input" } }, new() { Kind = "close", To = 0 }, new() { Kind = "close", To = 1 }, new() { Kind = "receive", From = 0, To = -1, Check = true, Expected = new() { Source = "answer" } }];
await Run("close-input-eof", close, "#include <iostream>\n#include <cstdlib>\nint main(int,char**v){int x,s=0;while(std::cin>>x)s+=x;if(atoi(v[1])==0)std::cout<<s<<std::endl;}", "AC");
var disabled = MultisetExample.Create(); disabled.Flow = new() { Enabled = false }; disabled.Tests = [disabled.Tests[0]];
await Run("legacy-manager-unaffected", disabled, disabled.ExampleSource, "AC");
Assert(!System.Diagnostics.Process.GetProcessesByName("solution").Any(p => { try { return p.MainModule?.FileName.StartsWith(work, StringComparison.OrdinalIgnoreCase) == true; } finally { p.Dispose(); } }), "no remaining contestant processes");
File.WriteAllText(Path.Combine(work, "report.json"), Json.Write(new { passed = checks.Count, checks }));
Console.WriteLine($"ALL {checks.Count} FLOW CHECKS PASSED; {work}");

using System.Text;
using Judge.Core;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string work = Path.Combine(root, "artifacts", "multiprocess-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(work);
string compiler = Environment.GetEnvironmentVariable("ACM_COMPILER") ?? (OperatingSystem.IsWindows() ? @"D:\mingw64\bin\g++.exe" : "/usr/bin/g++");
List<string> checks = [];
void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
var problem = MultisetExample.Create();
File.WriteAllText(Path.Combine(work, "problem.json"), Json.Write(problem));
async Task<JudgeResult> Run(string name, Problem p, string source, string expected)
{
    var r = await new Engine(new(name, p, source, compiler, Path.Combine(root, "toolkit"), Path.Combine(work, name))).Run(default);
    File.WriteAllText(Path.Combine(work, name + ".json"), Json.Write(r));
    Console.WriteLine($"{name}: {r.Status} {r.Message}");
    if (r.Status != expected) Console.WriteLine(r.CompileLog);
    Assert(r.Status == expected, name + " => " + expected);
    return r;
}
string Sent(CaseResult c, string from, string to) =>
    Encoding.UTF8.GetString(c.Trace.Where(t => t.From == from && t.To == to).SelectMany(t => Convert.FromBase64String(t.DataBase64)).ToArray());
var good = await Run("multiset", problem, problem.ExampleSource, "AC");
Assert(good.Cases.Count == 3 && good.Cases.All(c => c.Verdict == "AC"), "duplicates, unequal multiplicity, 64-bit values");
var sample = good.Cases[0];
Assert(Sent(sample, "manager", "1:0") == "3\n1 2 2\n1\r\n", "role 0 receives only a, then peer verdict");
Assert(Sent(sample, "manager", "1:1") == "3\n2 1 2\n3\r\n1 2 2\r\n", "role 1 receives only b, then peer array");
Assert(Sent(sample, "1:0", "manager") == "3\r\n1 2 2\r\n1\r\n" && Sent(sample, "1:1", "manager") == "1\r\n", "both roles provide final answers");
Assert(good.Cases.All(c => c.Instances.Count == 2 && c.CpuMs == c.Instances.Max(i => i.Run.Usage.CpuMs) &&
    c.MemoryBytes == c.Instances.Max(i => i.Run.Usage.MemoryBytes)), "resource usage is per-role maximum");
var one = problem with { Tests = [problem.Tests[0]] };
await Run("disagree", one, problem.ExampleSource.Replace("std::cout << answer << '\\n' << std::flush; // role 0 final", "std::cout << (1 - answer) << '\\n' << std::flush; // wrong: roles disagree"), "WA");
await Run("both-wrong", one, problem.ExampleSource.Replace("(values == other ? 1 : 0)", "0"), "WA");
await Run("unsorted", problem with { Tests = [new("01", "3\n2 1 2\n2 2 1\n", "1\n")] },
    problem.ExampleSource.Replace("std::sort(values.begin(), values.end());", "if(role == 1) std::sort(values.begin(), values.end());"), "WA");
await Run("extra-output", one, problem.ExampleSource.Replace("// role 0 final", "std::cout << 99 << std::endl;"), "WA");
await Run("early-exit", one, "int main(){return 0;}", "WA");
await Run("role1-crash", one, problem.ExampleSource.Replace("int n;", "if(role == 1) return 7; int n;"), "RE");
await Run("role1-timeout", one with { Limits = new() { CpuMs = 100, WallMs = 1500 } },
    problem.ExampleSource.Replace("int n;", "if(role == 1) for(;;){asm volatile(\"\");} int n;"), "TLE");
await Run("no-flush", one with { Limits = new() { CpuMs = 1000, WallMs = 1500 } },
    problem.ExampleSource.Replace("std::cout << std::flush;", ""), "TLE");
// Split a logical protocol line across several writes. Preserve tabs/CRLF exactly.
string fragmented = """
    #include <cstdio>
    #include <iostream>
    #include <cstdlib>
    #include <thread>
    #include <chrono>
    #ifdef _WIN32
    #include <io.h>
    #include <fcntl.h>
    #endif
    int main(int argc,char** argv){
    #ifdef _WIN32
        _setmode(_fileno(stdout), _O_BINARY);
    #endif
        int role=atoi(argv[1]), n, x;
        std::cin>>n; for(int i=0;i<n;i++)std::cin>>x;
        if(role==0){
            const char* text="3\r\n1\t2  2\r\n";
            for(const char* p=text;*p;p++){std::fwrite(p,1,1,stdout);std::fflush(stdout);std::this_thread::sleep_for(std::chrono::milliseconds(5));}
            std::cin>>x;std::cout<<x<<std::endl;
        }else{
            std::cin>>n;for(int i=0;i<n;i++)std::cin>>x;
            std::cout<<"1\r\n"<<std::flush;
        }
    }
    """;
var split = await Run("fragmented", one, fragmented, "AC");
Assert(Sent(split.Cases[0], "manager", "1:1") == "3\n2 1 2\n3\r\n1\t2  2\r\n", "original array bytes relayed across fragments");
Assert(Sent(split.Cases[0], "manager", "1:0") == "3\n1 2 2\n1\r\n", "original answer bytes relayed");
// Largest permitted n, 19-digit values: exercises >64 KiB control writes and multi-MiB lines.
string values = string.Join(' ', Enumerable.Range(0, 200000).Select(i => (1000000000000000000L - i).ToString()));
string reverse = string.Join(' ', Enumerable.Range(0, 200000).Reverse().Select(i => (1000000000000000000L - i).ToString()));
await Run("maximum-data", problem with { Tests = [new("maximum", $"200000\n{values}\n{reverse}\n", "1\n")] }, problem.ExampleSource, "AC");
File.WriteAllText(Path.Combine(work, "report.json"), Json.Write(new { passed = checks.Count, checks }));
Console.WriteLine($"ALL {checks.Count} PASSED; evidence: {work}");

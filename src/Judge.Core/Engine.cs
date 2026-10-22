using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Judge.Core;

public sealed partial class Engine(JudgeRequest request, Action<JudgeResult>? progress = null)
{
    private readonly JudgeResult result = new();
    private Problem P => request.Problem;
    private string Root => request.WorkPath;
    private readonly Dictionary<string, string> binaries = [];
    public async Task<JudgeResult> Run(CancellationToken token, bool stopOnFailure = false)
    {
        try
        {
            P.Validate();
            if (P.Tests.Count == 0) throw new InvalidDataException("题目没有测试点");
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(request.Compiler) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            result.Status = "Compiling"; Publish();
            await using (var version = new NativeRunner(request.Compiler, ["--version"], Root, new() { CpuMs = 10000, WallMs = 10000 }, token, processLimit: 32))
            {
                await version.CloseInput(); var r = await version.Completion;
                if (r.Verdict != "AC") throw new InvalidOperationException("无法读取编译器版本：" + r.Message);
                result.CompilerVersion = r.Stdout.Split('\n')[0].Trim();
            }
            if (!await Compile("solution", request.Source, false, token)) return result;
            var tools = new List<(string, string)>();
            if (P.Mode == JudgeMode.Special || P.Mode == JudgeMode.Interactive || P.Mode == JudgeMode.Communication && !P.ManagedCommunication) tools.Add(("checker", P.Checker));
            if (P.Mode == JudgeMode.Interactive) tools.Add(("interactor", P.Interactor));
            if (P.Mode == JudgeMode.Multiprocess && P.Flow?.Enabled != true || P.Mode == JudgeMode.Communication && P.ManagedCommunication) tools.Add(("manager", P.Manager));
            foreach (var (name, source) in tools)
                if (!string.IsNullOrWhiteSpace(source) && !await Compile(name, source, true, token)) return result;
            result.Status = "Running"; Publish();
            for (int i = 0; i < P.Tests.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var test = P.Tests[i];
                var dir = Path.Combine(Root, $"case-{i:D4}"); Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, "input.txt"), test.Input, new UTF8Encoding(false), token);
                await File.WriteAllTextAsync(Path.Combine(dir, "answer.txt"), test.Answer, new UTF8Encoding(false), token);
                var c = P.Mode switch
                {
                    JudgeMode.Interactive => await Interactive(test, dir, token),
                    JudgeMode.Multiprocess when P.Flow?.Enabled == true => await RunFlow(test, dir, token),
                    JudgeMode.Multiprocess => await Managed(test, dir, token),
                    JudgeMode.Communication when P.ManagedCommunication => await Managed(test, dir, token),
                    _ => await Batch(test, dir, token)
                };
                result.Cases.Add(c); Publish();
                if (c.Verdict is "Judge Error" or "Cancelled" || stopOnFailure && c.Verdict != "AC") break;
            }
            result.Status = result.Cases.FirstOrDefault(c => c.Verdict is "Judge Error" or "Cancelled")?.Verdict
                ?? result.Cases.FirstOrDefault(c => c.Verdict != "AC")?.Verdict ?? "AC";
            result.Message = result.Status == "AC" ? $"全部 {result.Cases.Count} 个测试点通过" : result.Cases.First(c => c.Verdict == result.Status).Message;
        }
        catch (OperationCanceledException) { result.Status = "Cancelled"; result.Message = "评测已取消"; }
        catch (Exception ex) { result.Status = "Judge Error"; result.Message = ex.Message; }
        Publish(); return result;
    }
    private void Publish() => progress?.Invoke(result);
    private async Task<bool> Compile(string name, string source, bool judge, CancellationToken token)
    {
        var path = Path.Combine(Root, name + ".cpp");
        await File.WriteAllTextAsync(path, source, new UTF8Encoding(false), token);
        var binary = Path.Combine(Root, BinaryName(name));
        string[] args = ["-std=c++23", "-O2", "-pipe", "-I", request.ToolkitPath, path, "-o", binary];
        await using var runner = new NativeRunner(request.Compiler, args, Root, new() { CpuMs = 60000, WallMs = 60000, MemoryMiB = 1024, OutputKiB = 1024 }, token, processLimit: 32);
        await runner.CloseInput(); var r = await runner.Completion;
        result.CompileLog += $"[{name}]\n{r.Stdout}{r.Stderr}\n";
        if (r.Verdict != "AC")
        {
            result.Status = r.Verdict == "Cancelled" ? "Cancelled" : judge ? "Judge Error" : "CE";
            result.Message = $"{name} 编译失败：{r.Message}"; Publish(); return false;
        }
        binaries[name] = binary; Publish(); return true;
    }
    private NativeRunner Solution(string dir, CancellationToken token, Func<byte[], Task>? callback = null, string[]? args = null)
    {
        Directory.CreateDirectory(dir);
        return new(binaries["solution"], args ?? [], dir, P.Limits, token, callback);
    }
    private async Task<RunResult> Execute(string dir, string input, CancellationToken token)
        => await Execute(dir, Encoding.UTF8.GetBytes(input), token);
    private async Task<RunResult> Execute(string dir, byte[] input, CancellationToken token)
    {
        await using var p = Solution(dir, token);
        var feed = Task.Run(async () => { await p.Send(input); await p.CloseInput(); });
        var r = await p.Completion; await feed; return r;
    }
    private static CaseResult Case(TestData test, List<InstanceResult> runs, string verdict, string message, List<TraceEntry>? trace = null)
        => new(test.Name, verdict, message, runs.Count == 0 ? 0 : runs.Max(r => r.Run.Usage.CpuMs), runs.Count == 0 ? 0 : runs.Max(r => r.Run.Usage.MemoryBytes), runs, trace ?? []);
    private async Task<CaseResult> Batch(TestData test, string dir, CancellationToken token)
    {
        List<InstanceResult> runs = []; List<TraceEntry> trace = [];
        bool communication = P.Mode == JudgeMode.Communication;
        var first = await Execute(Path.Combine(dir, "round-1"), communication ? P.FirstMarker + "\n" + test.Input : test.Input, token);
        runs.Add(new(0, 1, first));
        if (first.Verdict != "AC") return Case(test, runs, first.Verdict, first.Message);
        var last = first;
        if (communication)
        {
            trace.Add(new(0, "round-1", "round-2", first.StdoutBase64));
            last = await Execute(Path.Combine(dir, "round-2"), [.. Encoding.UTF8.GetBytes(P.SecondMarker + "\n"), .. Convert.FromBase64String(first.StdoutBase64)], token);
            runs.Add(new(0, 2, last));
            if (last.Verdict != "AC") return Case(test, runs, last.Verdict, last.Message, trace);
        }
        var (verdict, message) = await Check(test, dir, Convert.FromBase64String(last.StdoutBase64), token);
        return Case(test, runs, verdict, message, trace);
    }
    public static (string Verdict, string Message) Compare(string actual, string expected, bool revealExpected = false)
    {
        var a = Regex.Split(actual, "[ \\t\\r\\n\\v\\f]+").Where(x => x.Length != 0).ToArray();
        var b = Regex.Split(expected, "[ \\t\\r\\n\\v\\f]+").Where(x => x.Length != 0).ToArray();
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            if (i >= a.Length || i >= b.Length || a[i] != b[i])
            {
                // Revealing expected tokens is a per-problem opt-in: the answer is not public to contestants by default.
                if (!revealExpected) return ("WA", $"第 {i + 1} 个 token 不同（期望 {b.Length} 个 token，实际 {a.Length} 个）");
                return ("WA", $"第 {i + 1} 个 token 不同：期望「{(i < b.Length ? Short(b[i]) : "<EOF>")}」，实际「{(i < a.Length ? Short(a[i]) : "<EOF>")}」");
            }
        return ("AC", "所有 token 相同");
    }
    public static string BinaryName(string name) => name + (OperatingSystem.IsWindows() ? ".exe" : "");
    private static string Short(string value) => value.Length > 160 ? value[..160] + "…" : value;
    private async Task<(string, string)> Check(TestData test, string dir, byte[] actual, CancellationToken token)
    {
        if (!binaries.ContainsKey("checker"))
        {
            // Text input/answers are UTF-8; reject malformed output instead of silently replacing bytes.
            try { return Compare(new UTF8Encoding(false, true).GetString(actual), test.Answer, P.RevealExpected); }
            catch (DecoderFallbackException) { return ("WA", "输出不是有效 UTF-8，无法与答案 token 匹配"); }
        }
        string output = Path.Combine(dir, "output.txt"); await File.WriteAllBytesAsync(output, actual, token);
        await using var checker = new NativeRunner(binaries["checker"], [Path.Combine(dir, "input.txt"), output, Path.Combine(dir, "answer.txt")], dir, P.JudgeLimits, token);
        await checker.CloseInput(); return JudgeVerdict(await checker.Completion);
    }
    private static (string, string) JudgeVerdict(RunResult run)
    {
        if (run.Verdict == "Cancelled") return ("Cancelled", run.Message);
        if (run.Verdict is not ("AC" or "RE")) return ("Judge Error", "判题程序：" + run.Message);
        var status = run.ExitCode switch { 0 => "AC", 1 => "WA", 2 => "PE", _ => "Judge Error" };
        return (status, string.IsNullOrWhiteSpace(run.Stderr) ? $"判题程序退出码 {run.ExitCode}" : Short(run.Stderr.Trim()));
    }
    private Limits SessionJudgeLimits(int rounds = 1) => P.JudgeLimits with { WallMs = Math.Max(P.JudgeLimits.WallMs, P.Limits.WallMs * rounds + 2000) };
    private async Task<CaseResult> Interactive(TestData test, string dir, CancellationToken token)
    {
        List<TraceEntry> trace = []; var clock = Stopwatch.StartNew();
        var solReady = new TaskCompletionSource<NativeRunner>(TaskCreationOptions.RunContinuationsAsynchronously);
        var judgeReady = new TaskCompletionSource<NativeRunner>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Forward(byte[] bytes, string from, string to, Task<NativeRunner> target)
        {
            lock (trace) trace.Add(new(clock.Elapsed.TotalMilliseconds, from, to, Convert.ToBase64String(bytes)));
            await (await target).Send(bytes);
        }
        await using var sol = Solution(Path.Combine(dir, "solution"), token, b => Forward(b, "solution", "interactor", judgeReady.Task));
        solReady.SetResult(sol);
        NativeRunner judge;
        try { judge = new(binaries["interactor"], [Path.Combine(dir, "input.txt"), Path.Combine(dir, "transcript.txt"), Path.Combine(dir, "answer.txt")], dir, SessionJudgeLimits(), token, b => Forward(b, "interactor", "solution", solReady.Task)); }
        catch { judgeReady.SetException(new IOException("交互器启动失败")); throw; }
        await using var judgeLease = judge;
        judgeReady.SetResult(judge);
        var solClose = Task.Run(async () => { await sol.Completion; await judge.CloseInput(); });
        var judgeClose = Task.Run(async () => { await judge.Completion; await sol.CloseInput(); });
        var first = await Task.WhenAny(sol.Completion, judge.Completion);
        if (first == sol.Completion && (await sol.Completion).Verdict != "AC") judge.Stop("Cancelled", "选手已失败");
        var sr = await sol.Completion;
        var jr = await judge.Completion;
        await Task.WhenAll(solClose, judgeClose);
        List<InstanceResult> runs = [new(0, 1, sr)];
        var jv = JudgeVerdict(jr);
        // An explicit WA/PE is authoritative if closing the conversation subsequently makes the solution exit.
        if (first == judge.Completion && jv.Item1 != "AC") return Case(test, runs, jv.Item1, jv.Item2, trace);
        if (sr.Verdict != "AC") return Case(test, runs, sr.Verdict, sr.Message, trace);
        if (jv.Item1 != "AC") return Case(test, runs, jv.Item1, jv.Item2, trace);
        if (binaries.ContainsKey("checker"))
        {
            var path = Path.Combine(dir, "transcript.txt");
            if (!File.Exists(path)) return Case(test, runs, "Judge Error", "交互器未生成后置 checker 所需的 transcript.txt", trace);
            jv = await Check(test, dir, await File.ReadAllBytesAsync(path, token), token);
        }
        return Case(test, runs, jv.Item1, jv.Item2, trace);
    }

    private async Task<CaseResult> Managed(TestData test, string dir, CancellationToken token)
    {
        bool communication = P.Mode == JudgeMode.Communication;
        var clock = Stopwatch.StartNew(); List<TraceEntry> trace = []; List<InstanceResult> runs = [];
        var children = new Dictionary<int, (NativeRunner Runner, int Round)>();
        var observers = new List<Task>(); int round = 0;
        var commands = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
        var partial = new StringBuilder(); var decoder = Encoding.UTF8.GetDecoder();
        Task Receive(byte[] bytes)
        {
            var chars = new char[bytes.Length + 2]; int n = decoder.GetChars(bytes, chars, false);
            foreach (var c in chars.AsSpan(0, n))
            {
                if (c == '\n') { commands.Writer.TryWrite(partial.ToString()); partial.Clear(); }
                else partial.Append(c);
                if (partial.Length > 65536) throw new InvalidDataException("manager 协议行超过 64 KiB");
            }
            return Task.CompletedTask;
        }
        await using var manager = new NativeRunner(binaries["manager"], [Path.Combine(dir, "input.txt"), Path.Combine(dir, "answer.txt")], dir, SessionJudgeLimits(communication ? 2 : 1), token, Receive);
        async Task Event(object value) => await manager.Send(JsonSerializer.Serialize(value, Json.Compact) + "\n");
        void Record(byte[] bytes, string from, string to) { lock (trace) trace.Add(new(clock.Elapsed.TotalMilliseconds, from, to, Convert.ToBase64String(bytes))); }
        async Task StartChild(int id, int r)
        {
            var child = Solution(Path.Combine(dir, $"round-{r}-process-{id}"), token, async b =>
            {
                Record(b, $"{r}:{id}", "manager");
                await Event(new { type = "output", id, round = r, data = Convert.ToBase64String(b) });
            }, communication ? [] : [id.ToString(), P.ProcessCount.ToString()]);
            children.Add(id, (child, r));
            observers.Add(Task.Run(async () =>
            {
                var rr = await child.Completion;
                lock (runs) runs.Add(new(id, r, rr));
                await Event(new { type = "exit", id, round = r, verdict = rr.Verdict, exitCode = rr.ExitCode });
                if (rr.Verdict != "AC") commands.Writer.TryWrite("{\"type\":\"$childFailed\"}");
            }));
            if (communication) await child.Send((r == 1 ? P.FirstMarker : P.SecondMarker) + "\n");
        }
        string verdict = "Judge Error", message = "manager 未给出最终判定";
        bool childFailed = false;
        var completion = Task.Run(async () => { await manager.Completion; commands.Writer.TryComplete(); });
        try
        {
            await Event(new { type = "init", version = 1, mode = P.Mode.ToString(), processCount = communication ? 1 : P.ProcessCount, inputPath = "input.txt", answerPath = "answer.txt" });
            if (!communication) for (int i = 0; i < P.ProcessCount; i++) await StartChild(i, 1);
            await foreach (var line in commands.Reader.ReadAllAsync(token))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line); var cmd = doc.RootElement;
                string type = cmd.GetProperty("type").GetString() ?? "";
                if (type == "$childFailed") { childFailed = true; manager.Stop("Cancelled", "选手已失败"); break; }
                if (type == "start")
                {
                    if (!communication || round >= 2) throw new InvalidDataException("start 只允许通信模式顺序启动两轮");
                    if (children.TryGetValue(0, out var old))
                    {
                        if (!old.Runner.Completion.IsCompleted || (await old.Runner.Completion).Verdict != "AC") throw new InvalidDataException("上一轮尚未正常结束");
                        await old.Runner.DisposeAsync(); children.Remove(0);
                    }
                    await StartChild(0, ++round); await Event(new { type = "started", id = 0, round });
                }
                else if (type is "send" or "close")
                {
                    int id = cmd.GetProperty("id").GetInt32();
                    if (!children.TryGetValue(id, out var target)) throw new InvalidDataException("未知进程编号");
                    if (type == "close") await target.Runner.CloseInput();
                    else { var b = Convert.FromBase64String(cmd.GetProperty("data").GetString()!); Record(b, "manager", $"{target.Round}:{id}"); await target.Runner.Send(b); }
                }
                else if (type == "verdict")
                {
                    verdict = cmd.GetProperty("verdict").GetString()!;
                    if (verdict is not ("AC" or "WA" or "PE" or "Judge Error")) throw new InvalidDataException("manager 返回未知判定");
                    message = cmd.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    if (verdict == "AC" && communication && round != 2) throw new InvalidDataException("通信题必须完成两轮后才能 AC");
                    break;
                }
                else throw new InvalidDataException("未知 manager 命令：" + type);
            }
            foreach (var c in children.Values) await c.Runner.CloseInput();
            if (verdict != "AC") foreach (var c in children.Values) if (!c.Runner.Completion.IsCompleted) c.Runner.Stop("Cancelled", "manager 结束会话");
            await manager.CloseInput();
            // AC is accepted only after every contestant exits successfully.
            await Task.WhenAll(observers);
            var failure = runs.OrderBy(x => x.Round).ThenBy(x => x.Id).FirstOrDefault(x => x.Run.Verdict is not ("AC" or "Cancelled"));
            if (failure != null) { verdict = failure.Run.Verdict; message = failure.Run.Message; }
            var mr = await manager.Completion;
            if (mr.Verdict != "AC" && !childFailed) { verdict = token.IsCancellationRequested ? "Cancelled" : "Judge Error"; message = "manager：" + mr.Message + " " + Short(mr.Stderr); }
        }
        catch (OperationCanceledException) { verdict = "Cancelled"; message = "评测已取消"; }
        catch (Exception ex) { verdict = "Judge Error"; message = "manager 协议错误：" + ex.Message; }
        finally
        {
            if (!manager.Completion.IsCompleted) manager.Stop("Cancelled", "会话结束");
            foreach (var child in children.Values) await child.Runner.DisposeAsync();
            await Task.WhenAll(observers); await completion;
        }
        return Case(test, runs.OrderBy(r => r.Round).ThenBy(r => r.Id).ToList(), verdict, message, trace);
    }
}

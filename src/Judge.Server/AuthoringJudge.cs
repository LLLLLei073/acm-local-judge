using System.Text;
using Judge.Core;

namespace Judge.Server;

public record TestInput(string Input, string? Answer = null);

/// <summary>Authoring checks use the same protocols and verdicts as contestant submissions.</summary>
internal static class AuthoringJudge
{
    internal static async Task<List<TestData>> Harden(Problem problem, IReadOnlyList<TestInput> inputs, string compiler, string toolkit, CancellationToken ct)
    {
        if (inputs.Count is < 1 or > 20) throw new InvalidDataException("单次请提供 1–20 个测试点");
        if (inputs.Any(t => Encoding.UTF8.GetByteCount(t.Input ?? "") > 1048576 || Encoding.UTF8.GetByteCount(t.Answer ?? "") > 1048576))
            throw new InvalidDataException("单个输入或参考判定不能超过 1 MiB");
        if (string.IsNullOrWhiteSpace(problem.ExampleSource)) throw new InvalidDataException("请先在「编辑题目 → 标准答案」中填写标准答案源码");
        var dir = Temporary("harden");
        try
        {
            bool calculateAnswer = problem.Mode is JudgeMode.Standard or JudgeMode.Special;
            string exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "reference.exe" : "reference");
            if (calculateAnswer)
            {
                var src = Path.Combine(dir, "reference.cpp");
                await File.WriteAllTextAsync(src, problem.ExampleSource, new UTF8Encoding(false), ct);
                await using var compilerRun = new NativeRunner(compiler, ["-std=c++23", "-O2", "-pipe", "-I", toolkit, src, "-o", exe], dir,
                    new() { CpuMs = 60000, WallMs = 60000, MemoryMiB = 1024, OutputKiB = 1024 }, ct, processLimit: 32);
                await compilerRun.CloseInput(); var compiled = await compilerRun.Completion; ct.ThrowIfCancellationRequested();
                if (compiled.Verdict != "AC") throw new InvalidDataException("标准答案编译失败：" + Limit(compiled.Message + compiled.Stdout + compiled.Stderr));
            }
            var names = problem.Tests.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var additions = new List<TestData>(); int next = problem.Tests.Count;
            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i].Input ?? "";
                var answer = inputs[i].Answer ?? "";
                if (calculateAnswer)
                {
                    var work = Path.Combine(dir, "answer-" + i); Directory.CreateDirectory(work);
                    await using var run = new NativeRunner(exe, [], work, problem.Limits, ct);
                    var feed = Task.Run(async () => { await run.Send(input); await run.CloseInput(); });
                    var result = await run.Completion; await feed; ct.ThrowIfCancellationRequested();
                    if (result.Verdict != "AC") throw new InvalidDataException($"第 {i + 1} 条输入：标准答案运行失败（{result.Verdict}）：{result.Message}");
                    answer = result.Stdout.Replace("\r\n", "\n");
                }
                string name; do { name = "h" + (++next).ToString("00"); } while (!names.Add(name));
                additions.Add(new(name, input, answer));
            }
            var candidate = problem with { Tests = additions };
            var validation = await new Engine(new("authoring-harden", candidate, problem.ExampleSource, compiler, toolkit, Path.Combine(dir, "validation"))).Run(ct, stopOnFailure: true);
            ct.ThrowIfCancellationRequested();
            if (validation.Status != "AC") throw new InvalidDataException($"新增数据按本题协议验证失败（{validation.Status}）：{Limit(validation.Message + "\n" + validation.CompileLog)}");
            return additions;
        }
        finally { Cleanup(dir); }
    }

    internal static async Task<JudgeResult> Brute(Problem problem, string source, string compiler, string toolkit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("请粘贴暴力程序源码");
        if (Encoding.UTF8.GetByteCount(source) > 1048576) throw new InvalidDataException("源码不能超过 1 MiB");
        var dir = Temporary("brute");
        try
        {
            var result = await new Engine(new("authoring-brute", problem, source, compiler, toolkit, dir)).Run(ct, stopOnFailure: true);
            ct.ThrowIfCancellationRequested();
            if (result.Cases.Count == 0) throw new InvalidDataException($"候选程序未进入测试（{result.Status}）：{Limit(result.Message + "\n" + result.CompileLog)}");
            return result;
        }
        finally { Cleanup(dir); }
    }

    private static string Temporary(string purpose) { var path = Path.Combine(Path.GetTempPath(), "acm-" + purpose + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static string Limit(string text) => text.Length > 1200 ? text[..1200] + "…" : text;
    private static void Cleanup(string path) { try { Directory.Delete(path, true); } catch { /* OS cleanup can reclaim a busy temporary directory later. */ } }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Judge.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("无效 JSON");
}
public enum JudgeMode { Standard, Special, Interactive, Communication, Multiprocess }
public record Limits
{
    public int CpuMs { get; set; } = 2000;
    public int WallMs { get; set; } = 10000;
    public int MemoryMiB { get; set; } = 256;
    public int OutputKiB { get; set; } = 16384;
    public void Validate()
    {
        if (CpuMs is < 50 or > 300000 || WallMs < CpuMs || WallMs > 600000 || MemoryMiB is < 8 or > 8192 || OutputKiB is < 1 or > 65536)
            throw new InvalidDataException("限制范围：CPU 50–300000ms，墙钟不少于 CPU 且不超过 600000ms，内存 8–8192MiB，输出 1–65536KiB");
    }
}
public record TestData(string Name, string Input, string Answer);
public record Problem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "未命名题目";
    public string Statement { get; set; } = "";
    public JudgeMode Mode { get; set; }
    public Limits Limits { get; set; } = new();
    public Limits JudgeLimits { get; set; } = new() { CpuMs = 10000, WallMs = 60000, MemoryMiB = 512 };
    public string Checker { get; set; } = "";
    public string Interactor { get; set; } = "";
    public string Manager { get; set; } = "";
    public bool ManagedCommunication { get; set; }
    public string FirstMarker { get; set; } = "first";
    public string SecondMarker { get; set; } = "second";
    public int ProcessCount { get; set; } = 2;
    public string Protocol { get; set; } = "";
    public List<TestData> Tests { get; set; } = [];
    public string ExampleSource { get; set; } = "";
    public string WrongSource { get; set; } = "";
    public CommunicationFlow? Flow { get; set; }
    public void Validate()
    {
        if (Limits is null || JudgeLimits is null || Tests is null || Statement is null || Checker is null || Interactor is null || Manager is null || ExampleSource is null || WrongSource is null || Protocol is null || FirstMarker is null || SecondMarker is null)
            throw new InvalidDataException("题目字段不能为 null");
        if (!Enum.IsDefined(Mode)) throw new InvalidDataException("未知评测类型");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 200) throw new InvalidDataException("标题应为 1–200 字符");
        Limits.Validate(); JudgeLimits.Validate();
        if (ProcessCount is < 2 or > 16) throw new InvalidDataException("进程数应为 2–16");
        if (Tests.Any(t => t is null) || Tests.Count > 1000 || Tests.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Tests.Count) throw new InvalidDataException("测试点无效、重复或超过 1000 个");
        foreach (var t in Tests)
        {
            if (t is null || t.Name is null || t.Input is null || t.Answer is null) throw new InvalidDataException("测试点字段不能为 null");
            if (!System.Text.RegularExpressions.Regex.IsMatch(t.Name, @"^[\p{L}\p{N}_-][\p{L}\p{N}_.-]{0,79}$") || System.Text.RegularExpressions.Regex.IsMatch(t.Name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new InvalidDataException("测试点名称无效或使用 Windows 保留设备名");
        }
        if (Tests.Sum(t => (long)System.Text.Encoding.UTF8.GetByteCount(t.Input) + System.Text.Encoding.UTF8.GetByteCount(t.Answer)) > 60L * 1024 * 1024) throw new InvalidDataException("测试数据总量不能超过 60 MiB");
        if (FirstMarker.Length > 128 || SecondMarker.Length > 128 || FirstMarker.Any(char.IsWhiteSpace) || SecondMarker.Any(char.IsWhiteSpace) || FirstMarker.Length == 0 || SecondMarker.Length == 0 || FirstMarker == SecondMarker) throw new InvalidDataException("轮次标记必须是不同的非空 token");
        if (Mode == JudgeMode.Special && string.IsNullOrWhiteSpace(Checker)) throw new InvalidDataException("特判需要 checker");
        if (Mode == JudgeMode.Interactive && string.IsNullOrWhiteSpace(Interactor)) throw new InvalidDataException("交互题需要 interactor");
        Flow?.Validate(this);
        if ((Mode == JudgeMode.Multiprocess && Flow?.Enabled != true || Mode == JudgeMode.Communication && ManagedCommunication) && string.IsNullOrWhiteSpace(Manager)) throw new InvalidDataException("此模式需要 manager");
        if (new[] { Checker, Interactor, Manager, ExampleSource, WrongSource }.Any(x => x.Length > 1024 * 1024)) throw new InvalidDataException("单份源码不能超过 1 MiB");
    }
}
public record JudgeRequest(string Id, Problem Problem, string Source, string Compiler, string ToolkitPath, string WorkPath);
public record Usage(double CpuMs, double WallMs, long MemoryBytes);
public record RunResult(string Verdict, string Message, uint ExitCode, Usage Usage, string Stdout, string Stderr, string StdoutBase64 = "");
public record InstanceResult(int Id, int Round, RunResult Run);
public record TraceEntry(double Ms, string From, string To, string DataBase64);
public record CaseResult(string Name, string Verdict, string Message, double CpuMs, long MemoryBytes, List<InstanceResult> Instances, List<TraceEntry> Trace);
public record JudgeResult
{
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "";
    public string CompilerVersion { get; set; } = "";
    public string CompileArgs { get; set; } = "-std=c++23 -O2 -pipe";
    public string CompileLog { get; set; } = "";
    public List<CaseResult> Cases { get; set; } = [];
}
public record Progress(string Status, string Message, int Completed, int Total);

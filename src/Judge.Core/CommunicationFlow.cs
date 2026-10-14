using System.Text;

namespace Judge.Core;

// Versioned, declarative protocol. Member IDs are the actual argv[1] values.
public record CommunicationFlow
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public List<string> Members { get; set; } = ["角色 0", "角色 1"];
    public List<FlowStep> Steps { get; set; } = [];

    public void Validate(Problem problem)
    {
        if (Version != 1 || Members is null || Steps is null) throw new InvalidDataException("通信流程版本或字段无效");
        if (Enabled && problem.Mode != JudgeMode.Multiprocess) throw new InvalidDataException("可视化执行流程仅用于多进程题");
        if (Members.Count is < 2 or > 16 || Members.Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 40) || Members.Distinct().Count() != Members.Count)
            throw new InvalidDataException("通信成员应为 2–16 个，名称非空、唯一且不超过 40 字符");
        if (problem.Mode == JudgeMode.Multiprocess && Members.Count != problem.ProcessCount) throw new InvalidDataException("成员数量必须与进程数一致");
        if (Steps.Count > 256 || Enabled && Steps.Count == 0) throw new InvalidDataException("启用的通信流程需要 1–256 个步骤");
        HashSet<string> ids = [], received = []; HashSet<int> closed = [];
        bool checkedOutput = false;
        foreach (var step in Steps)
        {
            if (step is null || string.IsNullOrWhiteSpace(step.Id) || step.Id.Length > 80 || !ids.Add(step.Id) || step.Sample is null || step.Sample.Length > 65536 || step.Data is null || step.Expected is null || step.AdditionalData is null || step.AdditionalData.Count > 16 || step.AdditionalData.Any(d => d is null))
                throw new InvalidDataException("通信步骤字段无效、编号重复或样例超过 64 KiB");
            bool Member(int id) => id >= 0 && id < Members.Count;
            if (step.Kind is not ("send" or "receive" or "close")) throw new InvalidDataException("未知通信步骤类型");
            if (step.Kind == "receive")
            {
                if (!Member(step.From) || step.To < -1 || step.To >= Members.Count || step.To == step.From || step.Lines is < 1 or > 1000000)
                    throw new InvalidDataException("接收步骤的成员或行数无效");
                if (step.To >= 0 && closed.Contains(step.To)) throw new InvalidDataException("不能转发给已关闭输入的成员");
                if (step.Check)
                {
                    step.Expected.Validate(received);
                    checkedOutput = true;
                }
                received.Add(step.Id);
            }
            else
            {
                if (!Member(step.To) || closed.Contains(step.To)) throw new InvalidDataException("发送/关闭步骤目标无效或输入已关闭");
                if (step.Kind == "close") closed.Add(step.To);
                else { step.Data.Validate(received); foreach (var part in step.AdditionalData) part.Validate(received); }
            }
        }
        if (Enabled && !checkedOutput) throw new InvalidDataException("执行流程至少需要一个输出校验，不能仅转发后直接判 AC");
        // Validate data ranges before any contestant runs. Bad test/configuration is Judge Error, never WA.
        if (Enabled) foreach (var test in problem.Tests) foreach (var step in Steps)
        {
            if (step.Kind == "send" && step.Data.Source != "message") step.Data.Resolve(test, []);
            if (step.Kind == "send") foreach (var part in step.AdditionalData) if (part.Source != "message") part.Resolve(test, []);
            if (step.Kind == "receive" && step.Check && step.Expected.Source != "message") step.Expected.Resolve(test, []);
        }
    }
}

public record FlowStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "send";
    public int From { get; set; } = 0;
    public int To { get; set; } = 0; // -1: scheduler only; otherwise forward received bytes unchanged.
    public int Lines { get; set; } = 1;
    public FlowData Data { get; set; } = new();
    public List<FlowData> AdditionalData { get; set; } = [];
    public bool Check { get; set; }
    public FlowData Expected { get; set; } = new() { Source = "answer" };
    public string Sample { get; set; } = "";
}

public record FlowData
{
    public string Source { get; set; } = "literal"; // literal, input, answer, message
    public string Text { get; set; } = "";
    public string StepId { get; set; } = "";
    public int StartLine { get; set; } = 1;
    public int LineCount { get; set; } // 0 = through EOF, preserves original line terminators.
    public void Validate(HashSet<string> received)
    {
        if (Source is not ("literal" or "input" or "answer" or "message") || Text is null || Text.Length > 65536 || StepId is null || StartLine is < 1 or > 1000000 || LineCount is < 0 or > 1000000)
            throw new InvalidDataException("通信数据来源、文本长度或行范围无效");
        if (Source == "message" && !received.Contains(StepId)) throw new InvalidDataException("只能引用前面接收步骤的数据；请检查步骤顺序");
    }
    public byte[] Resolve(TestData test, Dictionary<string, byte[]> messages)
    {
        if (Source == "literal") return Encoding.UTF8.GetBytes(Text);
        byte[] bytes = Source switch
        {
            "input" => Encoding.UTF8.GetBytes(test.Input),
            "answer" => Encoding.UTF8.GetBytes(test.Answer),
            "message" when messages.TryGetValue(StepId, out var value) => value,
            _ => throw new InvalidDataException("找不到引用的通信消息")
        };
        int start = 0;
        for (int line = 1; line < StartLine; line++)
        {
            int end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end < 0 || end + 1 >= bytes.Length) throw new InvalidDataException($"测试点 {test.Name} 的 {Source} 不含第 {StartLine} 行");
            start = end + 1;
        }
        if (LineCount == 0) return bytes[start..];
        int pos = start;
        for (int i = 0; i < LineCount; i++)
        {
            if (pos >= bytes.Length) throw new InvalidDataException($"测试点 {test.Name} 的 {Source} 行数不足");
            int end = Array.IndexOf(bytes, (byte)'\n', pos);
            pos = end < 0 ? bytes.Length : end + 1;
        }
        return bytes[start..pos];
    }
}

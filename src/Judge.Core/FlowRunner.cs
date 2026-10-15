using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Judge.Core;

public sealed partial class Engine
{
    private record FlowEvent(int Member, byte[]? Bytes, RunResult? Exit);
    private sealed class FlowFailure(string verdict, string message) : Exception(message)
    {
        public string Verdict { get; } = verdict;
    }
    // Scan each incoming byte once, independent of pipe chunk boundaries.
    private sealed class FlowBuffer : IDisposable
    {
        private readonly MemoryStream bytes = new();
        private readonly Queue<int> ends = new();
        private int consumed;
        public bool HasRemaining => bytes.Length > consumed;
        public void Append(byte[] data)
        {
            int offset = (int)bytes.Length;
            bytes.Write(data);
            for (int i = 0; i < data.Length; i++) if (data[i] == (byte)'\n') ends.Enqueue(offset + i + 1);
        }
        public byte[]? Take(int lines)
        {
            if (ends.Count < lines) return null;
            int end = 0;
            for (int i = 0; i < lines; i++) end = ends.Dequeue();
            var result = bytes.GetBuffer().AsSpan(consumed, end - consumed).ToArray();
            consumed = end;
            return result;
        }
        public void Dispose() => bytes.Dispose();
    }

    private async Task<CaseResult> RunFlow(TestData test, string dir, CancellationToken token)
    {
        var flow = P.Flow!;
        var clock = Stopwatch.StartNew();
        List<TraceEntry> trace = [];
        List<NativeRunner> children = [];
        List<Task> observers = [];
        var events = Channel.CreateUnbounded<FlowEvent>(new() { SingleReader = true });
        var buffers = flow.Members.Select(_ => new FlowBuffer()).ToArray();
        var exited = new bool[flow.Members.Count];
        Dictionary<string, byte[]> messages = [];
        int currentStep = 0;
        string verdict = "AC", message = "通信步骤及输出校验全部通过";
        void Record(byte[] bytes, string from, string to)
        {
            lock (trace) trace.Add(new(clock.Elapsed.TotalMilliseconds, from, to, Convert.ToBase64String(bytes)));
        }
        void Handle(FlowEvent e)
        {
            if (e.Bytes != null) buffers[e.Member].Append(e.Bytes);
            if (e.Exit != null)
            {
                exited[e.Member] = true;
                if (e.Exit.Verdict != "AC") throw new FlowFailure(e.Exit.Verdict, $"{flow.Members[e.Member]}：{e.Exit.Message}");
            }
        }
        async Task<byte[]> Receive(int member, int lines)
        {
            for (;;)
            {
                while (events.Reader.TryRead(out var queued)) Handle(queued);
                var packet = buffers[member].Take(lines);
                if (packet != null) return packet;
                if (exited[member]) throw new FlowFailure("WA", $"{flow.Members[member]} 提前退出：需要 {lines} 个以换行结尾的完整行");
                Handle(await events.Reader.ReadAsync(token));
            }
        }
        async Task Send(int member, byte[] bytes)
        {
            Record(bytes, "manager", $"1:{member}");
            await children[member].Send(bytes);
        }
        try
        {
            for (int i = 0; i < flow.Members.Count; i++)
            {
                int member = i;
                var child = Solution(Path.Combine(dir, $"flow-process-{i}"), token, b =>
                {
                    Record(b, $"1:{member}", "manager");
                    events.Writer.TryWrite(new(member, b, null));
                    return Task.CompletedTask;
                }, [i.ToString(), flow.Members.Count.ToString()]);
                children.Add(child);
                observers.Add(Task.Run(async () => events.Writer.TryWrite(new(member, null, await child.Completion))));
            }
            foreach (var step in flow.Steps)
            {
                currentStep++;
                token.ThrowIfCancellationRequested();
                while (events.Reader.TryRead(out var pending)) Handle(pending);
                if (step.Kind == "close") await children[step.To].CloseInput();
                else if (step.Kind == "send")
                {
                    await Send(step.To, step.Data.Resolve(test, messages));
                    foreach (var part in step.AdditionalData) await Send(step.To, part.Resolve(test, messages));
                }
                else
                {
                    var bytes = await Receive(step.From, step.Lines);
                    if (step.Check)
                    {
                        string actual;
                        try { actual = new UTF8Encoding(false, true).GetString(bytes); }
                        catch (DecoderFallbackException) { throw new FlowFailure("WA", "输出不是有效 UTF-8"); }
                        var expected = new UTF8Encoding(false, true).GetString(step.Expected.Resolve(test, messages));
                        var check = Compare(actual, expected, P.RevealExpected);
                        if (check.Verdict != "AC") throw new FlowFailure(check.Verdict, check.Message);
                    }
                    messages.Add(step.Id, bytes);
                    if (step.To >= 0) await Send(step.To, bytes);
                }
            }
            foreach (var child in children) await child.CloseInput();
            while (exited.Any(x => !x)) Handle(await events.Reader.ReadAsync(token));
            for (int i = 0; i < buffers.Length; i++)
                if (buffers[i].HasRemaining) throw new FlowFailure("WA", $"{flow.Members[i]} 存在流程之外的额外输出");
        }
        catch (FlowFailure ex) { verdict = ex.Verdict; message = $"步骤 {currentStep}：{ex.Message}"; }
        catch (OperationCanceledException) { verdict = "Cancelled"; message = "评测已取消"; }
        catch (Exception ex) { verdict = "Judge Error"; message = $"步骤 {currentStep} 的流程配置/运行错误：{ex.Message}"; }
        finally
        {
            // Stop all before awaiting any one runner, including partial startup failures.
            foreach (var child in children) if (!child.Completion.IsCompleted) child.Stop("Cancelled", "通信流程已结束");
            foreach (var child in children) await child.DisposeAsync();
            await Task.WhenAll(observers);
            foreach (var buffer in buffers) buffer.Dispose();
        }
        var runs = new List<InstanceResult>();
        for (int i = 0; i < children.Count; i++) runs.Add(new(i, 1, await children[i].Completion));
        // A pipe/protocol symptom must not mask a real contestant resource failure.
        var failure = runs.FirstOrDefault(r => r.Run.Verdict is not ("AC" or "Cancelled"));
        if (failure != null && verdict != "Cancelled") { verdict = failure.Run.Verdict; message = $"步骤 {currentStep} · {flow.Members[failure.Id]}：{failure.Run.Message}"; }
        return Case(test, runs, verdict, message, trace);
    }
}

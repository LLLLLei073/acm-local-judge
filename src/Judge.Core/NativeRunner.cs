using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Judge.Core;

// Windows processes are created suspended so that no contestant instruction runs outside its Job.
public sealed class NativeRunner : IAsyncDisposable
{
    private readonly SafeFileHandle job, port, process;
    private readonly FileStream input, output, error;
    private readonly Limits limits;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly SemaphoreSlim inputGate = new(1);
    private readonly MemoryStream stdout = new(), stderr = new();
    private readonly CancellationToken token;
    private long outputBytes;
    private string? forced;
    private string message = "";
    private bool inputClosed;
    public Task<RunResult> Completion { get; }

    public NativeRunner(string executable, IEnumerable<string> arguments, string directory, Limits limits,
        CancellationToken token, Func<byte[], Task>? onOutput = null, int processLimit = 1)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("评测内核仅支持 Windows");
        this.limits = limits; this.token = token;
        job = Own(N.CreateJobObjectW(IntPtr.Zero, null));
        port = Own(N.CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1));
        SafeFileHandle? proc = null;
        IntPtr childIn = IntPtr.Zero, parentIn = IntPtr.Zero, parentOut = IntPtr.Zero, childOut = IntPtr.Zero, parentErr = IntPtr.Zero, childErr = IntPtr.Zero;
        IntPtr attrs = IntPtr.Zero, handles = IntPtr.Zero;
        bool attrsInitialized = false;
        try
        {
            var ext = new N.ExtendedLimit();
            ext.Basic.LimitFlags = 0x2000 | 0x200 | 0x8; // KILL_ON_JOB_CLOSE | JOB_MEMORY | ACTIVE_PROCESS
            ext.Basic.ActiveProcessLimit = (uint)processLimit;
            ext.JobMemoryLimit = (UIntPtr)((ulong)limits.MemoryMiB * 1024 * 1024);
            SetJob(9, ext);
            SetJob(7, new N.CompletionPort { CompletionKey = new IntPtr(1), Port = port.DangerousGetHandle() });
            var sa = new N.SecurityAttributes { Length = Marshal.SizeOf<N.SecurityAttributes>(), Inherit = true };
            Check(N.CreatePipe(out childIn, out parentIn, ref sa, 0));
            Check(N.CreatePipe(out parentOut, out childOut, ref sa, 0));
            Check(N.CreatePipe(out parentErr, out childErr, ref sa, 0));
            foreach (var h in new[] { parentIn, parentOut, parentErr }) Check(N.SetHandleInformation(h, 1, 0));
            nuint bytes = 0;
            N.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attrs = Marshal.AllocHGlobal((nint)bytes);
            Check(N.InitializeProcThreadAttributeList(attrs, 1, 0, ref bytes)); attrsInitialized = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.Copy(new[] { childIn, childOut, childErr }, 0, handles, 3);
            Check(N.UpdateProcThreadAttribute(attrs, 0, (IntPtr)0x20002, handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            var start = new N.StartupInfoEx { AttributeList = attrs };
            start.Info.Size = Marshal.SizeOf<N.StartupInfoEx>(); start.Info.Flags = 0x100;
            start.Info.StdInput = childIn; start.Info.StdOutput = childOut; start.Info.StdError = childErr;
            var cmd = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
            Check(N.CreateProcessW(executable, cmd, IntPtr.Zero, IntPtr.Zero, true, 0x08000000 | 0x4 | 0x80000, IntPtr.Zero, directory, ref start, out var pi));
            proc = Own(pi.Process);
            using var thread = Own(pi.Thread);
            if (!N.AssignProcessToJobObject(job, proc)) { N.TerminateProcess(proc, 1); throw new Win32Exception(); }
            if (N.ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            process = proc;
            input = new FileStream(Own(parentIn), FileAccess.Write, 4096, false); parentIn = IntPtr.Zero;
            output = new FileStream(Own(parentOut), FileAccess.Read, 4096, false); parentOut = IntPtr.Zero;
            error = new FileStream(Own(parentErr), FileAccess.Read, 4096, false); parentErr = IntPtr.Zero;
        }
        catch { proc?.Dispose(); job.Dispose(); port.Dispose(); throw; }
        finally
        {
            foreach (var h in new[] { childIn, childOut, childErr, parentIn, parentOut, parentErr }) if (h != IntPtr.Zero) N.CloseHandle(h);
            if (attrsInitialized) N.DeleteProcThreadAttributeList(attrs);
            if (attrs != IntPtr.Zero) Marshal.FreeHGlobal(attrs);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
        }
        // Explicit Task.Run keeps synchronous Windows pipe handles off the caller's thread.
        var readOut = Task.Run(() => Pump(output, stdout, onOutput));
        var readErr = Task.Run(() => Pump(error, stderr, null));
        Completion = Finish(readOut, readErr);
    }

    public async Task Send(byte[] bytes)
    {
        await inputGate.WaitAsync(token);
        try { if (!inputClosed) { await input.WriteAsync(bytes, token); await input.FlushAsync(token); } }
        catch (IOException) { /* Peer closed stdin; its exit/verdict determines the result. */ }
        finally { inputGate.Release(); }
    }
    public Task Send(string text) => Send(Encoding.UTF8.GetBytes(text));
    public async Task CloseInput()
    {
        await inputGate.WaitAsync();
        try { if (!inputClosed) { inputClosed = true; input.Dispose(); } }
        finally { inputGate.Release(); }
    }
    public void Stop(string verdict, string reason)
    {
        if (Interlocked.CompareExchange(ref forced, verdict, null) == null) message = reason;
        if (!job.IsClosed) N.TerminateJobObject(job, 1);
    }
    private async Task Pump(FileStream stream, MemoryStream capture, Func<byte[], Task>? callback)
    {
        var buffer = new byte[8192];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                var total = Interlocked.Add(ref outputBytes, n);
                if (total > (long)limits.OutputKiB * 1024) { Stop("OLE", "stdout 与 stderr 合计超过输出限制"); break; }
                capture.Write(buffer, 0, n);
                if (callback != null) await callback(buffer[..n]);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception ex) { Stop("Judge Error", "管道处理失败：" + ex.Message); }
    }
    private async Task<RunResult> Finish(Task readOut, Task readErr)
    {
        uint code = 259;
        double cpu = 0; long memory = 0;
        while (true)
        {
            bool exited = N.WaitForSingleObject(process, 0) == 0;
            Check(N.GetExitCodeProcess(process, out code));
            while (N.GetQueuedCompletionStatus(port, out var msg, out _, out _, 0))
                if (msg is 9 or 10) Stop("MLE", "超过提交内存限制");
            var usage = Query<N.BasicAccounting>(1);
            cpu = (usage.TotalUserTime + usage.TotalKernelTime) / 10000.0;
            memory = (long)Query<N.ExtendedLimit>(9).PeakJobMemoryUsed;
            if (token.IsCancellationRequested) Stop("Cancelled", "评测已取消");
            else if (cpu > limits.CpuMs) Stop("TLE", "超过 CPU 时间限制");
            else if (!exited && clock.Elapsed.TotalMilliseconds > limits.WallMs) Stop("TLE", "超过墙钟时间限制，可能在等待输入或未 flush");
            if (exited) break;
            await Task.Delay(10);
        }
        // A parent exiting must not leave its children or pipe writers behind.
        N.TerminateJobObject(job, 1);
        await Task.WhenAll(readOut, readErr);
        clock.Stop();
        var verdict = forced ?? (code == 0 ? "AC" : "RE");
        return new(verdict, forced != null ? message : code == 0 ? "正常退出" : $"退出码 0x{code:X8}", code,
            new(cpu, clock.Elapsed.TotalMilliseconds, memory), Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()), Convert.ToBase64String(stdout.ToArray()));
    }
    private T Query<T>(int kind) where T : struct
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try { Check(N.QueryInformationJobObject(job, kind, p, (uint)Marshal.SizeOf<T>(), IntPtr.Zero)); return Marshal.PtrToStructure<T>(p); }
        finally { Marshal.FreeHGlobal(p); }
    }
    private void SetJob<T>(int kind, T value) where T : struct
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try { Marshal.StructureToPtr(value, p, false); Check(N.SetInformationJobObject(job, kind, p, (uint)Marshal.SizeOf<T>())); }
        finally { Marshal.FreeHGlobal(p); }
    }
    public async ValueTask DisposeAsync()
    {
        if (!Completion.IsCompleted) Stop("Cancelled", "会话结束");
        try { await Completion; } finally { input.Dispose(); output.Dispose(); error.Dispose(); process.Dispose(); job.Dispose(); port.Dispose(); stdout.Dispose(); stderr.Dispose(); }
    }
    private static SafeFileHandle Own(IntPtr handle) => handle == IntPtr.Zero || handle == new IntPtr(-1) ? throw new Win32Exception() : new(handle, true);
    private static void Check(bool ok) { if (!ok) throw new Win32Exception(); }
    public static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1).Append(c); slashes = 0; continue; }
            result.Append('\\', slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static class N
    {
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimit { public long ProcessUserTime, JobUserTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicAccounting { public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [StructLayout(LayoutKind.Sequential)] internal struct CompletionPort { public IntPtr CompletionKey, Port; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public ushort ShowWindow, Reserved2Size; public IntPtr Reserved2, StdInput, StdOutput, StdError; }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo Info; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, IntPtr info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, IntPtr info, uint size, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateJobObject(SafeFileHandle job, uint exit);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateProcess(SafeFileHandle process, uint exit);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attrs, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processAttrs, IntPtr threadAttrs, bool inherit, uint flags, IntPtr env, string cwd, ref StartupInfoEx startup, out ProcessInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exit);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeFileHandle handle, uint timeout);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr CreateIoCompletionPort(IntPtr file, IntPtr existing, UIntPtr key, uint threads);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetQueuedCompletionStatus(SafeFileHandle port, out uint bytes, out UIntPtr key, out IntPtr overlapped, uint timeout);
    }
}

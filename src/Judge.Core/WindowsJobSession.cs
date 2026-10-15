using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Judge.Core;

// Windows: processes are created suspended so that no contestant instruction runs outside its Job.
internal sealed class WindowsJobSession : SandboxSession
{
    private readonly SafeFileHandle job, port, process;
    private readonly FileStream output, error;
    public WindowsJobSession(string executable, IEnumerable<string> arguments, string directory, Limits limits,
        CancellationToken token, Func<byte[], Task>? onOutput, int processLimit) : base(limits, token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("评测内核仅支持 Windows");
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
            var input = new FileStream(Own(parentIn), FileAccess.Write, 4096, false); parentIn = IntPtr.Zero;
            output = new FileStream(Own(parentOut), FileAccess.Read, 4096, false); parentOut = IntPtr.Zero;
            error = new FileStream(Own(parentErr), FileAccess.Read, 4096, false); parentErr = IntPtr.Zero;
            Begin(input, output, error, onOutput);
        }
        catch { proc?.Dispose(); job.Dispose(); port.Dispose(); throw; }
        finally
        {
            foreach (var h in new[] { childIn, childOut, childErr, parentIn, parentOut, parentErr }) if (h != IntPtr.Zero) N.CloseHandle(h);
            if (attrsInitialized) N.DeleteProcThreadAttributeList(attrs);
            if (attrs != IntPtr.Zero) Marshal.FreeHGlobal(attrs);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
        }
    }
    protected override bool HasExited => N.WaitForSingleObject(process, 0) == 0;
    protected override uint ExitCode { get { Check(N.GetExitCodeProcess(process, out uint code)); return code; } }
    protected override string ExitFailureNote(uint code) => $"退出码 0x{code:X8}";
    protected override double CpuMilliseconds()
    {
        var usage = Query<N.BasicAccounting>(1);
        return (usage.TotalUserTime + usage.TotalKernelTime) / 10000.0;
    }
    protected override long PeakMemoryBytes() => (long)Query<N.ExtendedLimit>(9).PeakJobMemoryUsed;
    protected override void AfterPoll()
    {
        while (N.GetQueuedCompletionStatus(port, out var msg, out _, out _, 0))
            if (msg is 9 or 10) Stop("MLE", "超过提交内存限制");
    }
    protected override void Kill()
    {
        if (!job.IsClosed) N.TerminateJobObject(job, 1);
    }
    protected override void Cleanup()
    {
        output.Dispose(); error.Dispose(); process.Dispose(); job.Dispose(); port.Dispose();
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
    private static SafeFileHandle Own(IntPtr handle) => handle == IntPtr.Zero || handle == new IntPtr(-1) ? throw new Win32Exception() : new(handle, true);
    private static void Check(bool ok) { if (!ok) throw new Win32Exception(); }
    private static string Quote(string value)
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

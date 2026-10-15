using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Judge.Core;

// Linux confinement: rlimits as hard backstops, /proc (or cgroup v2) for tree-wide measurement,
// cgroup.kill / process-group SIGKILL to take down every descendant. This class only runs on Linux;
// the facade never constructs it on Windows, so it merely has to compile there.
internal sealed class LinuxProcessSession : SandboxSession
{
    private const int RLIMIT_CPU = 0, RLIMIT_FSIZE = 1, RLIMIT_CORE = 4, RLIMIT_NPROC = 6, RLIMIT_AS = 9;
    private const double TicksToMs = 10.0; // userspace CLK_TCK is fixed at 100 on mainstream Linux
    private static readonly string[] ToolDirs = ["/usr/bin", "/bin", "/usr/sbin", "/sbin"];
    private static readonly Lazy<string?> Unshare = new(() =>
        Environment.GetEnvironmentVariable("ACM_NETWORK_ISOLATION") != "0" && FindTool("unshare") is { } p && Probe(p) ? p : null);
    private static readonly Lazy<string?> Setsid = new(() => FindTool("setsid"));
    private static readonly Lazy<string?> CgroupRoot = new(ProbeCgroupRoot);

    private readonly Process process = new();
    private readonly Cgroup? cgroup;
    private readonly int pid;
    private int polls;
    private int oomKills;
    private double mainCpuMs, descendantCpuMs;
    private long peakBytes;
    private bool useChildrenFile = true;

    public LinuxProcessSession(string executable, IEnumerable<string> arguments, string directory, Limits limits,
        CancellationToken token, Func<byte[], Task>? onOutput, int processLimit) : base(limits, token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux 评测会话只能在 Linux 上启动");
        cgroup = Cgroup.Create((long)limits.MemoryMiB * 1024 * 1024, processLimit);
        var start = process.StartInfo;
        start.UseShellExecute = false;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.WorkingDirectory = directory;
        // Wrapper chain keeps the tracked pid: unshare execs through, and setsid does not fork because the
        // spawned child can never be a process-group leader here — so the contestant stays the direct child.
        var command = new List<string>();
        if (Unshare.Value != null) command.AddRange([Unshare.Value!, "--user", "--map-root-user", "--net", "--"]);
        if (Setsid.Value != null) command.AddRange([Setsid.Value!, "--"]);
        command.Add(executable);
        start.FileName = command[0];
        foreach (var part in command.Skip(1)) start.ArgumentList.Add(part);
        foreach (var a in arguments) start.ArgumentList.Add(a);
        try
        {
            process.Start();
            pid = process.Id;
            ApplyRlimits();
            cgroup?.Enter(pid);
            Begin(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, process.StandardError.BaseStream, onOutput);
        }
        catch { cgroup?.Dispose(); process.Dispose(); throw; }
    }
    protected override bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }
    protected override uint ExitCode
    {
        get
        {
            int raw = process.ExitCode; // negative on Unix when killed by signal N
            return raw >= 0 ? (uint)raw : (uint)(128 - raw);
        }
    }
    protected override string ExitFailureNote(uint code)
    {
        int raw = process.ExitCode;
        return raw < 0 ? $"被信号 {-raw}（{SignalName(-raw)}）终止" : $"退出码 {code}";
    }
    protected override double CpuMilliseconds()
    {
        if (cgroup?.ReadNumber("cpu.stat", "usage_usec") is { } usec) return usec / 1000.0;
        MaybeScanTree();
        if (TryReadMainStat(out var total)) mainCpuMs = total;
        return mainCpuMs + descendantCpuMs;
    }
    protected override long PeakMemoryBytes()
    {
        if (cgroup?.ReadNumber("memory.peak") is { } peak) return peak;
        if (cgroup?.ReadNumber("memory.current") is { } current && current > peakBytes) { peakBytes = current; return peakBytes; }
        MaybeScanTree();
        var own = ReadStatusKb("VmHWM");
        if (own > peakBytes) peakBytes = own;
        return peakBytes;
    }
    protected override void AfterPoll()
    {
        polls++;
        var limit = (long)limits.MemoryMiB * 1024 * 1024;
        if (cgroup != null)
        {
            var killed = (int)(cgroup.ReadNumber("memory.events", "oom_kill") ?? 0);
            if (killed > oomKills) { oomKills = killed; Stop("MLE", "超过提交内存限制"); return; }
            if ((cgroup.ReadNumber("memory.peak") ?? cgroup.ReadNumber("memory.current") ?? 0) > limit) Stop("MLE", "超过提交内存限制");
            return;
        }
        if (peakBytes > limit) Stop("MLE", "超过提交内存限制");
    }
    protected override void Kill()
    {
        try { if (cgroup?.Alive == true) File.WriteAllText(Path.Combine(cgroup.Path, "cgroup.kill"), "1"); } catch { }
        try
        {
            int pgid = getpgid(pid), own = getpgid(0);
            if (pgid > 0 && own > 0 && pgid != own) kill(-pgid, 9);
        }
        catch { }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
    protected override void Cleanup()
    {
        try { process.Dispose(); } catch { }
        cgroup?.Dispose();
    }
    private void ApplyRlimits()
    {
        // Backstops for the polling loop. AS gets headroom over the polled RSS limit so a bad_alloc
        // (which would look like RE) loses the race against the polled MLE, matching the Windows verdict.
        Set(RLIMIT_CPU, (ulong)Math.Ceiling(limits.CpuMs / 1000.0));
        Set(RLIMIT_AS, (ulong)limits.MemoryMiB * 1024 * 1024 * 4 / 3);
        Set(RLIMIT_FSIZE, 64UL * 1024 * 1024);
        Set(RLIMIT_NPROC, 512);
        Set(RLIMIT_CORE, 0);
    }
    private void Set(int resource, ulong value)
    {
        var limit = new Rlimit { Cur = value, Max = value };
        prlimit64(pid, resource, ref limit, IntPtr.Zero);
    }
    private void MaybeScanTree()
    {
        if (polls % 5 != 0) return;
        long cpuTicks = 0;
        var seen = new HashSet<int> { pid };
        var frontier = new List<int> { pid };
        while (frontier.Count > 0)
        {
            var next = new List<int>();
            foreach (var id in frontier)
                foreach (var child in ChildrenOf(id))
                    if (seen.Add(child))
                    {
                        next.Add(child);
                        var s = Sample(child);
                        cpuTicks += s.cpuTicks;
                        if (s.peakBytes > peakBytes) peakBytes = s.peakBytes;
                    }
            frontier = next;
        }
        descendantCpuMs = cpuTicks * TicksToMs;
    }
    private bool TryReadMainStat(out double cpuMs)
    {
        cpuMs = 0;
        try
        {
            var text = File.ReadAllText($"/proc/{pid}/stat");
            int close = text.LastIndexOf(')'); if (close < 0) return false;
            var f = text[(close + 2)..].Split(' ');
            if (f.Length < 15) return false;
            // utime+stime of the process itself plus children it already reaped (g++ waits its cc1plus).
            cpuMs = (long.Parse(f[11]) + long.Parse(f[12]) + long.Parse(f[13]) + long.Parse(f[14])) * TicksToMs;
            return true;
        }
        catch { return false; }
    }
    private long ReadStatusKb(string key)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
                if (line.StartsWith(key + ':'))
                    return long.Parse(line[(key.Length + 1)..].Trim().Split(' ')[0]) * 1024;
        }
        catch { }
        return 0;
    }
    private (long cpuTicks, long peakBytes) Sample(int id)
    {
        long cpu = 0, peak = 0;
        try
        {
            var text = File.ReadAllText($"/proc/{id}/stat");
            int close = text.LastIndexOf(')'); var f = text[(close + 2)..].Split(' ');
            if (f.Length >= 15) cpu = long.Parse(f[11]) + long.Parse(f[12]);
            foreach (var line in File.ReadLines($"/proc/{id}/status"))
                if (line.StartsWith("VmHWM:")) { peak = long.Parse(line[6..].Trim().Split(' ')[0]) * 1024; break; }
        }
        catch { }
        return (cpu, peak);
    }
    private IEnumerable<int> ChildrenOf(int id)
    {
        if (useChildrenFile)
        {
            try
            {
                return File.ReadAllText($"/proc/{id}/task/{id}/children").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse);
            }
            catch (FileNotFoundException) { useChildrenFile = false; }
            catch { return []; }
        }
        // Fallback: one pass over /proc building the ppid map (kernels without PROC_CHILDREN).
        var map = new List<int>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
                if (int.TryParse(Path.GetFileName(dir), out int candidate))
                {
                    var text = File.ReadAllText($"{dir}/stat");
                    int close = text.LastIndexOf(')'); var f = text[(close + 2)..].Split(' ');
                    if (f.Length > 1 && int.TryParse(f[1], out int ppid) && ppid == id) map.Add(candidate);
                }
        }
        catch { }
        return map;
    }
    private static string? FindTool(string name)
    {
        foreach (var dir in ToolDirs)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
    private static bool Probe(string unshare)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(unshare, "--user --map-root-user --net true") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
            if (p == null) return false;
            if (p.WaitForExit(3000)) return p.ExitCode == 0;
            try { p.Kill(); } catch { }
            return false;
        }
        catch { return false; }
    }
    private static string? ProbeCgroupRoot()
    {
        if (Environment.GetEnvironmentVariable("ACM_CGROUP") == "0") return null;
        var custom = Environment.GetEnvironmentVariable("ACM_CGROUP_DIR");
        if (custom != null) return Cgroup.Test(custom) ? custom : null;
        // Prefer this unit's own delegated directory (systemd Delegate=yes), then the bare root (root-run).
        try
        {
            foreach (var line in File.ReadLines("/proc/self/cgroup"))
            {
                int split = line.IndexOf("::");
                if (split < 0) continue;
                var relative = line[(split + 2)..].Trim('/');
                if (relative.Length == 0) break;
                var own = Path.Combine("/sys/fs/cgroup", relative);
                if (Cgroup.Test(own)) return own;
            }
        }
        catch { }
        var fs = "/sys/fs/cgroup";
        return File.Exists(Path.Combine(fs, "cgroup.controllers")) && Cgroup.Test(fs) ? fs : null;
    }
    private static string SignalName(int sig) => sig switch { 2 => "SIGINT", 4 => "SIGILL", 6 => "SIGABRT", 8 => "SIGFPE", 9 => "SIGKILL", 11 => "SIGSEGV", 13 => "SIGPIPE", 15 => "SIGTERM", 24 => "SIGXCPU", 25 => "SIGXFSZ", _ => $"signal {sig}" };

    // Best-effort cgroup v2 delegation: when the judge runs under a delegated slice (or as root) each
    // session gets its own group with memory/pids limits, tree-wide peak counters and cgroup.kill.
    private sealed class Cgroup(string path)
    {
        public string Path { get; } = path;
        public bool Alive { get; private set; } = true;
        public static bool Test(string root)
        {
            try
            {
                var probe = System.IO.Path.Combine(root, "judge-probe-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(probe); Directory.Delete(probe);
                return true;
            }
            catch { return false; }
        }
        public static Cgroup? Create(long memoryBytes, int processLimit)
        {
            var root = CgroupRoot.Value;
            if (root == null) return null;
            try
            {
                var path = System.IO.Path.Combine(root, "judge-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(path);
                var group = new Cgroup(path);
                group.Write("memory.max", memoryBytes.ToString());
                group.Write("memory.swap.max", "0");
                group.Write("pids.max", processLimit.ToString());
                // Without the memory controller files the group would silently under-measure; bail out.
                if (!File.Exists(System.IO.Path.Combine(path, "memory.max"))) { group.Dispose(); return null; }
                return group;
            }
            catch { return null; }
        }
        public void Enter(int id) { if (Alive) Write("cgroup.procs", id.ToString()); }
        public long? ReadNumber(string file, string? key = null)
        {
            try
            {
                foreach (var line in File.ReadLines(System.IO.Path.Combine(Path, file)))
                {
                    if (key == null) return long.TryParse(line.Trim(), out var single) ? single : null;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[0] == key && long.TryParse(parts[1], out var value)) return value;
                }
            }
            catch { }
            return null;
        }
        private void Write(string file, string content)
        {
            if (!Alive) return;
            try { File.WriteAllText(System.IO.Path.Combine(Path, file), content); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        public void Dispose()
        {
            Alive = false;
            try { Directory.Delete(Path, false); } catch { }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rlimit { public ulong Cur, Max; }
    [DllImport("libc", SetLastError = true)] private static extern int prlimit64(int pid, int resource, ref Rlimit limit, IntPtr old);
    [DllImport("libc", SetLastError = true)] private static extern int getpgid(int pid);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
}

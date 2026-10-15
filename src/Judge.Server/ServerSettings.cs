using System.Security.Cryptography;
using Judge.Core;

namespace Judge.Server;

public static class ServerSettings
{
    public const int DefaultParallelism = 2;
    public const int MaxParallelism = 8;
    public const string CookieName = "acm_access";

    public static Dictionary<string, string> Read(string data)
    {
        var path = Path.Combine(data, "settings.json");
        return File.Exists(path) ? Json.Read<Dictionary<string, string>>(File.ReadAllText(path)) : [];
    }

    private static void Merge(string data, params KeyValuePair<string, string>[] values)
    {
        var settings = Read(data);
        foreach (var (key, value) in values) settings[key] = value;
        File.WriteAllText(Path.Combine(data, "settings.json"), Json.Write(settings));
    }

    public static string Compiler(string data)
    {
        var settings = Read(data);
        if (settings.TryGetValue("compiler", out var path) && path.Length > 0) return path;
        return Environment.GetEnvironmentVariable("ACM_COMPILER") ?? DefaultCompiler();
    }
    public static string DefaultCompiler() => OperatingSystem.IsWindows() ? @"D:\mingw64\bin\g++.exe" : "/usr/bin/g++";

    public static int Parallelism(string data)
    {
        var settings = Read(data);
        if (!settings.TryGetValue("parallelism", out var raw) && (raw = Environment.GetEnvironmentVariable("ACM_PARALLELISM")) == null) return DefaultParallelism;
        return int.TryParse(raw, out var value) && value is >= 1 and <= MaxParallelism ? value : DefaultParallelism;
    }

    // The access token gates non-loopback API access. ACM_TOKEN explicitly overrides; otherwise the
    // token is minted once, persisted and printed at startup.
    public static string Token(string data)
    {
        if (Environment.GetEnvironmentVariable("ACM_TOKEN") is { Length: > 0 } fromEnv) return fromEnv;
        var settings = Read(data);
        if (settings.TryGetValue("token", out var token) && token.Length > 0) return token;
        token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        Merge(data, new KeyValuePair<string, string>("token", token));
        return token;
    }

    public static List<string> AllowedHosts(string data)
    {
        var settings = Read(data);
        var raw = settings.TryGetValue("allowedHosts", out var value) && value.Length > 0 ? value : Environment.GetEnvironmentVariable("ACM_ALLOWED_HOSTS") ?? "";
        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(NormalizeHost), "127.0.0.1", "localhost", "::1"];
    }
    public static string NormalizeHost(string host) => host.Trim().TrimStart('[').TrimEnd(']').ToLowerInvariant();

    // Per-IP submissions per minute; 0 disables the limiter.
    public static int RateLimit(string data)
    {
        var settings = Read(data);
        var raw = settings.TryGetValue("rateLimit", out var value) ? value : Environment.GetEnvironmentVariable("ACM_RATE_LIMIT");
        return int.TryParse(raw, out var limit) && limit >= 0 ? limit : 30;
    }

    // Disk retention; 0 keeps everything (the historical behaviour). Problem version directories and
    // submission directories are only pruned when explicitly configured.
    public static int RetainProblemVersions(string data) => IntSetting(data, "retainProblemVersions", "ACM_RETAIN_PROBLEM_VERSIONS");
    public static int RetainSubmissions(string data) => IntSetting(data, "retainSubmissions", "ACM_RETAIN_SUBMISSIONS");

    // When open, remote contestants may browse and submit without the admin token; management
    // endpoints always require it. Default closed: remote access needs the token everywhere.
    public static bool OjOpen(string data)
    {
        var settings = Read(data);
        if (settings.TryGetValue("ojOpen", out var value)) return value == "1";
        return Environment.GetEnvironmentVariable("ACM_OJ_OPEN") == "1";
    }
    private static int IntSetting(string data, string key, string env)
    {
        var settings = Read(data);
        var raw = settings.TryGetValue(key, out var value) ? value : Environment.GetEnvironmentVariable(env);
        return int.TryParse(raw, out var parsed) && parsed >= 0 ? parsed : 0;
    }

    public static void Save(string data, string compiler, int parallelism)
    {
        // Merge: settings.json also holds token/allowedHosts/rateLimit, which must survive a save.
        Merge(data, new KeyValuePair<string, string>("compiler", compiler), new("parallelism", parallelism.ToString()));
    }
}

using Judge.Core;

namespace Judge.Server;

public static class ServerSettings
{
    public const int DefaultParallelism = 2;
    public const int MaxParallelism = 8;

    public static Dictionary<string, string> Read(string data)
    {
        var path = Path.Combine(data, "settings.json");
        return File.Exists(path) ? Json.Read<Dictionary<string, string>>(File.ReadAllText(path)) : [];
    }

    public static string Compiler(string data)
    {
        var settings = Read(data);
        if (settings.TryGetValue("compiler", out var path) && path.Length > 0) return path;
        return Environment.GetEnvironmentVariable("ACM_COMPILER") ?? @"D:\mingw64\bin\g++.exe";
    }

    public static int Parallelism(string data)
    {
        var settings = Read(data);
        if (!settings.TryGetValue("parallelism", out var raw) && (raw = Environment.GetEnvironmentVariable("ACM_PARALLELISM")) == null) return DefaultParallelism;
        return int.TryParse(raw, out var value) && value is >= 1 and <= MaxParallelism ? value : DefaultParallelism;
    }

    public static void Save(string data, string compiler, int parallelism)
    {
        // All values are stored as strings: settings.json is read back as Dictionary<string,string>, and a JSON number would throw on read.
        File.WriteAllText(Path.Combine(data, "settings.json"), Json.Write(new Dictionary<string, string> { ["compiler"] = compiler, ["parallelism"] = parallelism.ToString() }));
    }
}

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Judge.Core;

namespace Judge.Server;

public static class Packages
{
    public const long MaxBytes = 64 * 1024 * 1024;
    public static byte[] Export(Problem problem)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var metadata = problem with { Tests = [], Checker = "", Interactor = "", Manager = "", Statement = "" };
            var manifest = JsonNode.Parse(Json.Write(metadata))!; manifest["formatVersion"] = 1;
            Write(zip, "problem.json", manifest.ToJsonString(Json.Options)); Write(zip, "statement.md", problem.Statement);
            foreach (var (name, content) in new[] { ("checker", problem.Checker), ("interactor", problem.Interactor), ("manager", problem.Manager) }) if (content.Length > 0) Write(zip, name + ".cpp", content);
            foreach (var test in problem.Tests) { Write(zip, "tests/" + test.Name + ".in", test.Input); Write(zip, "tests/" + test.Name + ".ans", test.Answer); }
        }
        return memory.ToArray();
    }
    private static void Write(ZipArchive zip, string path, string content) { using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); w.Write(content); }
    public static async Task<Problem> Import(Stream stream, CancellationToken token)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
        if (zip.Entries.Count > 2100) throw new InvalidDataException("ZIP 文件数过多");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            string path = entry.FullName.Replace('\\', '/');
            if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(x => x is ".." or ".") || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("ZIP 含越界路径或符号链接");
            if (path.EndsWith('/')) continue;
            total += entry.Length; if (total > MaxBytes) throw new InvalidDataException("解压总量超过 64 MiB");
            await using var input = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[8192]; int n;
            while ((n = await input.ReadAsync(buffer, token)) > 0) { if (output.Length + n > entry.Length || output.Length + n > MaxBytes) throw new InvalidDataException("无效 ZIP 文件长度"); await output.WriteAsync(buffer.AsMemory(0, n), token); }
            if (!files.TryAdd(path, new UTF8Encoding(false, true).GetString(output.ToArray()))) throw new InvalidDataException("ZIP 含重复文件");
        }
        if (!files.TryGetValue("problem.json", out var manifest)) throw new InvalidDataException("缺少根目录 problem.json");
        if (JsonNode.Parse(manifest)?["formatVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("不支持的题目包版本");
        var p = Json.Read<Problem>(manifest); p.Id = "";
        p.Statement = files.GetValueOrDefault("statement.md", ""); p.Checker = files.GetValueOrDefault("checker.cpp", ""); p.Interactor = files.GetValueOrDefault("interactor.cpp", ""); p.Manager = files.GetValueOrDefault("manager.cpp", ""); p.Tests = [];
        var inputs = files.Keys.Where(k => k.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".in", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var path in inputs)
        {
            var answerPath = path[..^3] + ".ans";
            if (!files.ContainsKey(answerPath) && p.Mode is JudgeMode.Standard or JudgeMode.Special or JudgeMode.Communication) throw new InvalidDataException("缺少答案：" + answerPath);
            p.Tests.Add(new(Path.GetFileNameWithoutExtension(path), files[path], files.GetValueOrDefault(answerPath, "")));
        }
        if (files.Keys.Any(k => k.StartsWith("tests/") && k.EndsWith(".ans") && !files.ContainsKey(k[..^4] + ".in"))) throw new InvalidDataException("存在没有输入的答案文件");
        p.Validate(); return p;
    }
}

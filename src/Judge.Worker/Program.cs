using Judge.Core;
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

if (args.Length != 1) { Console.Error.WriteLine("Usage: Judge.Worker request.json"); return 2; }
var requestPath = Path.GetFullPath(args[0]);
var request = Json.Read<JudgeRequest>(await File.ReadAllTextAsync(requestPath));
var resultPath = Path.Combine(Path.GetDirectoryName(requestPath)!, "result.json");
using var cancellation = new CancellationTokenSource();
System.Diagnostics.Process? parent = null;
if (int.TryParse(Environment.GetEnvironmentVariable("ACM_PARENT_PID"), out int parentId))
{
    try { parent = System.Diagnostics.Process.GetProcessById(parentId); }
    catch (ArgumentException) { cancellation.Cancel(); }
}
var watcher = Task.Run(async () =>
{
    while (!cancellation.IsCancellationRequested)
    {
        if (parent?.HasExited == true || File.Exists(Path.Combine(Path.GetDirectoryName(requestPath)!, "cancel"))) { cancellation.Cancel(); break; }
        await Task.Delay(50);
    }
});
try
{
    var engine = new Engine(request, result =>
    {
        File.WriteAllText(resultPath + ".tmp", Json.Write(result));
        File.Move(resultPath + ".tmp", resultPath, true);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new Progress(result.Status, result.Message, result.Cases.Count, request.Problem.Tests.Count), Json.Compact));
    });
    await engine.Run(cancellation.Token);
    return 0;
}
finally { cancellation.Cancel(); await watcher; parent?.Dispose(); }

using System.Net.Http.Headers;
using System.Net.Http.Json;
using Judge.Core;
using Judge.Server;

var endpoint = Environment.GetEnvironmentVariable("ACM_CLOUD_URL") ?? throw new InvalidDataException("请配置 ACM_CLOUD_URL");
var uri = new Uri(endpoint);
if (uri.Scheme != "https" && !uri.IsLoopback) throw new InvalidDataException("远程评测只接受 HTTPS 地址");
var token = Environment.GetEnvironmentVariable("ACM_WORKER_TOKEN") ?? throw new InvalidDataException("请配置 ACM_WORKER_TOKEN");
var compiler = Environment.GetEnvironmentVariable("ACM_COMPILER") ?? (OperatingSystem.IsWindows() ? @"D:\mingw64\bin\g++.exe" : "/usr/bin/g++");
if (!File.Exists(compiler)) throw new InvalidDataException("本机编译器不存在");
using var client = new HttpClient { BaseAddress = new Uri(uri.GetLeftPart(UriPartial.Authority)), Timeout = TimeSpan.FromSeconds(20) };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine("Windows 评测端已启动，等待云端任务。按 Ctrl+C 停止。");
while (!stop.IsCancellationRequested)
{
    try
    {
        using var response = await client.PostAsync("/api/worker/claim", null, stop.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) { await Task.Delay(10000, stop.Token); continue; }
        var claim = await response.Content.ReadFromJsonAsync<RemoteClaim>(Json.Options, stop.Token) ?? throw new InvalidDataException("无效任务");
        if (!Guid.TryParseExact(claim.Id, "N", out _)) throw new InvalidDataException("无效任务编号");
        Console.WriteLine($"开始任务 {claim.Id} ({claim.Kind})");
        var work = Path.Combine(Path.GetTempPath(), "acm-agent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        string? latest = null; RemoteOutput? final = null;
        var pump = Task.Run(async () =>
        {
            while (!pumpStop.IsCancellationRequested)
            {
                try
                {
                    var snapshot = Volatile.Read(ref latest);
                    using var heartbeat = await client.PostAsJsonAsync($"/api/worker/{claim.Id}/heartbeat", new RemoteHeartbeat(claim.Lease, snapshot == null ? null : Json.Read<RemoteOutput>(snapshot)), Json.Options, pumpStop.Token);
                    if (!heartbeat.IsSuccessStatusCode) { cancel.Cancel(); break; }
                    await Task.Delay(5000, pumpStop.Token);
                }
                catch (OperationCanceledException) when (pumpStop.IsCancellationRequested) { break; }
                catch { cancel.Cancel(); break; } // Stop immediately on lost lease/network, before lease expiry.
            }
        });
        try
        {
            var toolkit = Path.Combine(work, "toolkit"); Directory.CreateDirectory(toolkit);
            if (claim.Payload.Toolkit.Count > 64) throw new InvalidDataException("工具包文件过多");
            long total = 0;
            foreach (var (name, encoded) in claim.Payload.Toolkit)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_.-]+$") || name is "." or "..") throw new InvalidDataException("无效工具包路径");
                var bytes = Convert.FromBase64String(encoded); total += bytes.Length;
                if (total > 16 * 1024 * 1024) throw new InvalidDataException("工具包超过限额");
                await File.WriteAllBytesAsync(Path.Combine(toolkit, name), bytes, cancel.Token);
            }
            var request = claim.Payload.Request with { Compiler = compiler, ToolkitPath = toolkit, WorkPath = Path.Combine(work, "run") };
            request.Problem.Validate();
            if (claim.Kind == "judge") final = new(await new Engine(request, result => Volatile.Write(ref latest, Json.Write(new RemoteOutput(result)))).Run(cancel.Token));
            else if (claim.Kind == "brute") final = new(await AuthoringJudge.Brute(request.Problem, request.Source, compiler, toolkit, cancel.Token));
            else if (claim.Kind == "harden") final = new(Tests: await AuthoringJudge.Harden(request.Problem, (claim.Payload.Inputs ?? []).Select(t => new TestInput(t.Input, t.Answer)).ToList(), compiler, toolkit, cancel.Token));
            else throw new InvalidDataException("未知任务类型");
        }
        catch (OperationCanceledException) { final = new(Error: "评测端任务已停止"); }
        catch (Exception ex) { final = new(Error: ex is InvalidDataException ? ex.Message : "本机评测执行失败，请查看评测端环境"); Console.WriteLine(ex.GetType().Name); }
        finally
        {
            try
            {
                pumpStop.Cancel(); await pump;
                if (!cancel.IsCancellationRequested)
                {
                    using var completed = await client.PostAsJsonAsync($"/api/worker/{claim.Id}/heartbeat", new RemoteHeartbeat(claim.Lease, final, true), Json.Options, stop.Token);
                    completed.EnsureSuccessStatusCode();
                }
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        Console.WriteLine($"结束任务 {claim.Id}");
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"云端连接或任务失败 ({ex.GetType().Name})，稍后重试。");
        try { await Task.Delay(10000, stop.Token); } catch (OperationCanceledException) { break; }
    }
}

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Judge.Core;

namespace Judge.Server;

// Codeforces integration. Handle ownership is proven with a challenge code the user must place in
// their CF profile "Last name"; rating/rank are then fetched from the public user.info API.
// ACM_CF_API overrides the API base so tests can point it at a local stub.
public static class Codeforces
{
    public const int ChallengeMinutes = 15;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    // All callers (verification, leaderboard and history) share CF's two-second quota.
    private static readonly SemaphoreSlim ApiGate = new(1);
    private static DateTimeOffset nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(2100);
    private static readonly ConcurrentDictionary<string, (string Handle, string Code, DateTimeOffset Expires)> Challenges = [];

    public static string ApiBase => Environment.GetEnvironmentVariable("ACM_CF_API") is { Length: > 0 } custom ? custom.TrimEnd('/') : "https://codeforces.com/api";

    public record CfInfo(string Handle, int? Rating, string Rank, string? LastName);

    public static async Task<List<CfInfo>> FetchAsync(IEnumerable<string> handles, CancellationToken ct = default, bool fresh = false)
    {
        var users = await RequestAsync<List<CfUser>>($"user.info?handles={Uri.EscapeDataString(string.Join(";", handles))}&checkHistoricHandles=false&lang=en", fresh, ct);
        return users.Select(u => new CfInfo(u.Handle, u.Rating, u.Rank ?? "", u.LastName)).ToList();
    }

    internal static async Task<T> RequestAsync<T>(string method, bool fresh, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await ApiGate.WaitAsync(ct);
            try
            {
                var delay = nextRequest - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(2100);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/{method}" + (fresh ? $"&_={Guid.NewGuid():N}" : ""));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                using var response = await Http.SendAsync(request, ct);
                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                {
                    if (attempt < 2) { nextRequest = DateTimeOffset.UtcNow.AddSeconds(3 * (attempt + 1)); continue; }
                    throw new InvalidDataException("Codeforces 暂时限流或服务繁忙，请稍后重试；已有记录已保留");
                }
                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadFromJsonAsync<CfResponse<T>>(cancellationToken: ct);
                if (payload?.Status == "FAILED" && payload.Comment?.Contains("limit exceeded", StringComparison.OrdinalIgnoreCase) == true && attempt < 2)
                { nextRequest = DateTimeOffset.UtcNow.AddSeconds(3 * (attempt + 1)); continue; }
                if (payload?.Status != "OK" || payload.Result == null)
                    throw new InvalidDataException("Codeforces API：" + (payload?.Comment ?? "返回数据异常"));
                return payload.Result;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt < 2) continue;
                throw new InvalidDataException("无法连接 Codeforces API，请稍后重试；已有记录已保留", ex);
            }
            finally
            {
                // Space completed calls too: a slow first response must not make subsequent
                // requests reach CF closer together than the documented quota.
                var earliest = DateTimeOffset.UtcNow.AddMilliseconds(2100);
                if (nextRequest < earliest) nextRequest = earliest;
                ApiGate.Release();
            }
        }
    }
    private record CfResponse<T>(string Status, T? Result, string? Comment);
    private record CfUser(string Handle, int? Rating, string Rank, string? FirstName, string? LastName);

    // One outstanding challenge per user; codes expire and are compared case-insensitively.
    public static string IssueChallenge(string userId, string handle)
    {
        if (Challenges.TryGetValue(userId, out var previous) && previous.Expires > DateTimeOffset.UtcNow && previous.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase)) return previous.Code;
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 无易混淆字符
        var code = new string(RandomNumberGenerator.GetItems<char>(alphabet, 6));
        Challenges[userId] = (handle, code, DateTimeOffset.UtcNow.AddMinutes(ChallengeMinutes));
        return code;
    }
    public static (string Handle, string Code) TakeChallenge(string userId)
    {
        if (!Challenges.TryGetValue(userId, out var challenge) || challenge.Expires < DateTimeOffset.UtcNow)
        {
            Challenges.TryRemove(userId, out _);
            throw new InvalidDataException("验证码不存在或已过期，请重新获取");
        }
        return (challenge.Handle, challenge.Code);
    }
    public static void ClearChallenge(string userId) => Challenges.TryRemove(userId, out _);
    public static object? PendingChallenge(string userId) => Challenges.TryGetValue(userId, out var value) && value.Expires > DateTimeOffset.UtcNow
        ? new { handle = value.Handle, code = value.Code, expiresAt = value.Expires, minutes = ChallengeMinutes } : null;
}

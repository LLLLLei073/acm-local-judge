using System.Collections.Concurrent;
using System.Net.Http.Json;
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
    private static readonly ConcurrentDictionary<string, (string Handle, string Code, DateTimeOffset Expires)> Challenges = [];

    public static string ApiBase => Environment.GetEnvironmentVariable("ACM_CF_API") is { Length: > 0 } custom ? custom.TrimEnd('/') : "https://codeforces.com/api";

    public record CfInfo(string Handle, int? Rating, string Rank, string? LastName);

    public static async Task<List<CfInfo>> FetchAsync(IEnumerable<string> handles, CancellationToken ct = default)
    {
        var url = $"{ApiBase}/user.info?handles={Uri.EscapeDataString(string.Join(";", handles))}";
        try
        {
            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<CfResponse>(cancellationToken: ct);
            if (payload?.Status != "OK" || payload.Result == null) throw new InvalidDataException("Codeforces API 返回异常，请稍后重试");
            return payload.Result.Select(u => new CfInfo(u.Handle, u.Rating, u.Rank ?? "", u.LastName)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidDataException("Codeforces API 暂不可用，请稍后重试");
        }
    }
    private record CfResponse(string Status, List<CfUser> Result);
    private record CfUser(string Handle, int? Rating, string Rank, string? FirstName, string? LastName);

    // One outstanding challenge per user; codes expire and are compared case-insensitively.
    public static string IssueChallenge(string userId, string handle)
    {
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
}

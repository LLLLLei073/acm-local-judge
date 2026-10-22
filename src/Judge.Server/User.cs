using System.Security.Cryptography;
using System.Text;

namespace Judge.Server;

// A registered contestant. Real name + student id + email identify the person; the leaderboard may
// mask all of it when the user opts into anonymity (the entry keeps its rank either way).
public record User
{
    public string Id { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string CodeforcesHandle { get; set; } = "";
    public string CodeforcesBindingVersion { get; set; } = "";
    public int? CodeforcesRating { get; set; }
    public string CodeforcesRank { get; set; } = "";
    public DateTimeOffset? RatingUpdatedAt { get; set; }
    // In-site Elo rating, settled automatically when a rated contest finishes; null = unrated yet.
    public int? SiteRating { get; set; }
    public bool Anonymous { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public void Validate()
    {
        // 学号是成员资格的唯一绑定：浙江科技大学学号为纯数字，且全站唯一（SaveUser 强制）。
        if (!System.Text.RegularExpressions.Regex.IsMatch(StudentId, @"^\d{4,12}$")) throw new InvalidDataException("请填写浙江科技大学学号（4–12 位数字）");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 50) throw new InvalidDataException("姓名应为 1–50 字符");
        if (Email.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new InvalidDataException("邮箱格式不正确");
    }
    public static void ValidatePassword(string password)
    {
        if (password.Length is < 6 or > 128) throw new InvalidDataException("密码应为 6–128 位字符");
    }
    public static bool IsValidCodeforcesHandle(string handle) => System.Text.RegularExpressions.Regex.IsMatch(handle, @"^[A-Za-z0-9_]{1,24}$");
}

// PBKDF2-SHA256 with a per-user salt; the stored form is pbkdf2$iterations$salt$hash.
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations) || iterations is < 1 or > 10_000_000) return false;
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

// Stateless session cookie "userId.expiry.HMAC(userId.expiry)". Survives restarts without a session
// table; rotating the session secret revokes every session at once.
public static class UserSessions
{
    public const string CookieName = "acm_user";
    public const int LifetimeDays = 30;

    public static string Issue(string secret, string userId)
    {
        var payload = $"{userId}.{DateTimeOffset.UtcNow.AddDays(LifetimeDays).ToUnixTimeSeconds()}";
        return $"{payload}.{Signature(secret, payload)}";
    }
    public static string? Resolve(string secret, string? cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        var marker = cookie.LastIndexOf('.');
        if (marker <= 0 || marker == cookie.Length - 1) return null;
        var payload = cookie[..marker];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Signature(secret, payload)), Encoding.ASCII.GetBytes(cookie[(marker + 1)..]))) return null;
        var dot = payload.IndexOf('.');
        if (dot <= 0 || !long.TryParse(payload[(dot + 1)..], out var expiry) || expiry < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        return payload[..dot];
    }
    private static string Signature(string secret, string payload) => Convert.ToHexString(new HMACSHA256(Encoding.ASCII.GetBytes(secret)).ComputeHash(Encoding.ASCII.GetBytes(payload)));
}

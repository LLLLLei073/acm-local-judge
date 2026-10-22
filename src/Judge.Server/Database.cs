using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Judge.Server;

// Both providers use named @parameters. The remote schema is provisioned separately;
// the application role has no rights to Supabase Auth, public tables or other projects.
internal static class Database
{
    internal static DbConnection Open(string data, string? postgres)
    {
        DbConnection connection = postgres is null
            ? new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "judge.db"), Pooling = true }.ToString())
            : new NpgsqlConnection(postgres);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    internal static string? ValidatePostgres(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var settings = new NpgsqlConnectionStringBuilder(value);
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Username))
            throw new InvalidDataException("PostgreSQL 连接缺少 Host 或 Username");
        if (settings.SslMode != SslMode.VerifyFull)
            throw new InvalidDataException("云端 PostgreSQL 必须使用 SSL Mode=VerifyFull");
        settings.SearchPath = "acm";
        settings.MaxPoolSize = Math.Min(settings.MaxPoolSize, 10);
        settings.IncludeErrorDetail = false;
        settings.LogParameters = false;
        return settings.ConnectionString;
    }

    internal static void AddWithValue(this DbParameterCollection parameters, string name, object value)
    {
        DbParameter parameter = parameters is NpgsqlParameterCollection
            ? new NpgsqlParameter(name, value) : new SqliteParameter(name, value);
        parameters.Add(parameter);
    }
}

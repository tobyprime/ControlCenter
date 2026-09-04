using System.Reflection;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Infrastructure;

/// <summary>
/// 手写迁移机制：按文件名升序执行 Infrastructure/Migrations/*.sql（嵌入式资源），
/// 已应用的版本记录在 schema_migrations 表中，重启只补执行未应用的迁移。
/// </summary>
public static class DatabaseMigrator
{
    private const string MigrationResourceNamespace = "DevicePanel.Web.Infrastructure.Migrations";

    public static void Migrate(SqliteConnection connection)
    {
        EnsureMigrationsTable(connection);
        var appliedVersions = LoadAppliedVersions(connection);

        foreach (var (version, sql) in LoadEmbeddedMigrations())
        {
            if (appliedVersions.Contains(version))
            {
                continue;
            }

            ApplyOne(connection, version, sql);
        }
    }

    /// <summary>升级到指定版本为止（含）。用于模拟旧版本库的增量升级路径（如一期库回填目标表）。</summary>
    internal static void MigrateUpTo(SqliteConnection connection, string maxVersion)
    {
        EnsureMigrationsTable(connection);
        var appliedVersions = LoadAppliedVersions(connection);

        foreach (var (version, sql) in LoadEmbeddedMigrations())
        {
            if (appliedVersions.Contains(version) || string.CompareOrdinal(version, maxVersion) > 0)
            {
                continue;
            }

            ApplyOne(connection, version, sql);
        }
    }

    private static void ApplyOne(SqliteConnection connection, string version, string sql)
    {
        using var transaction = connection.BeginTransaction();
        using (var script = connection.CreateCommand())
        {
            script.Transaction = transaction;
            script.CommandText = sql;
            script.ExecuteNonQuery();
        }

        using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES ($version, $appliedAt)";
        record.Parameters.AddWithValue("$version", version);
        record.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("O"));
        record.ExecuteNonQuery();

        transaction.Commit();
    }

    private static void EnsureMigrationsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version        TEXT PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static HashSet<string> LoadAppliedVersions(SqliteConnection connection)
    {
        var versions = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            versions.Add(reader.GetString(0));
        }

        return versions;
    }

    private static List<(string Version, string Sql)> LoadEmbeddedMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var migrations = new List<(string, string)>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(MigrationResourceNamespace, StringComparison.Ordinal))
            {
                continue;
            }

            var version = resourceName.Substring(MigrationResourceNamespace.Length + 1);
            if (!version.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            version = version[..^4];
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"迁移资源 {resourceName} 无法读取。");
            using var reader = new StreamReader(stream);
            migrations.Add((version, reader.ReadToEnd()));
        }

        return migrations.OrderBy(m => m.Item1, StringComparer.Ordinal).ToList();
    }
}

using Npgsql;

namespace AccountantApp.Api.Shared.Migrations;

public static class SqlMigrationRunner
{
    public static async Task RunAsync(string connectionString, string contentRoot, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_versions (
                    script_name VARCHAR(500) PRIMARY KEY,
                    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        var slicesRoot = Path.Combine(contentRoot, "Slices");
        var scripts = Directory.EnumerateFiles(slicesRoot, "*.sql", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Infrastructure{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Select(path => (Path: path, Key: SliceRelativeKey(slicesRoot, path)))
            // The YYYYMMDD_### prefix orders changes across slices; the key breaks ties
            // deterministically, because sequence numbers restart at 001 in every slice.
            .OrderBy(script => VersionPrefix(script.Path), StringComparer.Ordinal)
            .ThenBy(script => script.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var (script, name) in scripts)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT EXISTS (SELECT 1 FROM schema_versions WHERE script_name = @name)";
            check.Parameters.AddWithValue("name", name);
            if ((bool)(await check.ExecuteScalarAsync(ct) ?? false))
                continue;

            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var apply = connection.CreateCommand();
            apply.Transaction = transaction;
            apply.CommandText = await File.ReadAllTextAsync(script, ct);
            await apply.ExecuteNonQueryAsync(ct);

            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_versions (script_name) VALUES (@name)";
            record.Parameters.AddWithValue("name", name);
            await record.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
    }

    // The ordering key is YYYYMMDD_### and nothing else. Ordering by the whole filename lets the
    // description decide: on the same date, Customers/20260830_001_CreateCustomersSchema.sql
    // would run before Notifications/20260830_001_CreateNotificationsSchema.sql for no better
    // reason than "C" sorting before "N", and renaming a file for clarity would reorder the
    // deployment. A script whose name does not have two underscores sorts by its whole name,
    // which is wrong but visible, rather than silently landing first.
    private static string VersionPrefix(string scriptPath)
    {
        var name = Path.GetFileNameWithoutExtension(scriptPath);
        var firstUnderscore = name.IndexOf('_');
        var secondUnderscore = firstUnderscore < 0 ? -1 : name.IndexOf('_', firstUnderscore + 1);
        return secondUnderscore < 0 ? name : name[..secondUnderscore];
    }

    // The tracking key is the path relative to Slices/, never the bare filename: two slices
    // will pick the same YYYYMMDD_### prefix, and a filename key would silently skip the
    // second script. Forward slashes so a script applied on Windows is not re-applied on Linux.
    private static string SliceRelativeKey(string slicesRoot, string scriptPath) =>
        Path.GetRelativePath(slicesRoot, scriptPath).Replace(Path.DirectorySeparatorChar, '/');
}
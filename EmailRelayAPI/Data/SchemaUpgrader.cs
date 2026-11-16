using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace EmailRelayAPI.Data;

/// <summary>
/// Additive schema upgrade for databases created with EnsureCreated: creates missing tables (with their indexes
/// and foreign keys) and adds missing columns, using the provider's own migrations SQL generator.
/// It never drops or alters existing tables or columns.
/// </summary>
public static class SchemaUpgrader
{
    public static async Task UpgradeAsync(AppDbContext ctx, ILogger logger, CancellationToken ct = default)
    {
        var designModel = ctx.GetService<IDesignTimeModel>().Model;
        var target = designModel.GetRelationalModel();
        var operations = ctx.GetService<IMigrationsModelDiffer>().GetDifferences(null, target);

        var existing = await ReadSchemaAsync(ctx, ct);
        var missingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upgrade = new List<MigrationOperation>();

        foreach (var create in operations.OfType<CreateTableOperation>())
        {
            if (!existing.TryGetValue(create.Name, out var columns))
            {
                missingTables.Add(create.Name);
                upgrade.Add(create);
                continue;
            }

            foreach (var column in create.Columns.Where(c => !columns.Contains(c.Name)))
            {
                if (!column.IsNullable && column.DefaultValue == null && column.DefaultValueSql == null && column.ComputedColumnSql == null)
                {
                    column.DefaultValue = DefaultFor(column.ClrType);
                }
                upgrade.Add(column);
            }
        }

        upgrade.AddRange(operations.OfType<CreateIndexOperation>().Where(i => missingTables.Contains(i.Table)));

        if (upgrade.Count == 0) return;

        var commands = ctx.GetService<IMigrationsSqlGenerator>().Generate(upgrade, designModel);
        await ctx.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(commands, ctx.GetService<IRelationalConnection>(), ct);

        foreach (var op in upgrade)
        {
            switch (op)
            {
                case CreateTableOperation t: logger.LogWarning("Schema upgrade: created table {Table}", t.Name); break;
                case AddColumnOperation c: logger.LogWarning("Schema upgrade: added column {Table}.{Column}", c.Table, c.Name); break;
            }
        }
    }

    private static object? DefaultFor(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return string.Empty;
        if (t == typeof(DateTime)) return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (t == typeof(Guid)) return Guid.Empty;
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }

    private static async Task<Dictionary<string, HashSet<string>>> ReadSchemaAsync(AppDbContext ctx, CancellationToken ct)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var connection = ctx.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            if (ctx.Database.IsSqlite())
            {
                var tables = new List<string>();
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
                }
                foreach (var table in tables)
                {
                    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
                    result[table] = columns;
                }
            }
            else
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = SCHEMA_NAME()";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var table = reader.GetString(0);
                    if (!result.TryGetValue(table, out var columns)) result[table] = columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    columns.Add(reader.GetString(1));
                }
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
        return result;
    }
}

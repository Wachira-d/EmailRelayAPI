using EmailRelayAPI.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Data;

public static class DbInitializer
{
    /// <summary>Registers the DbContext for the configured provider.</summary>
    public static IServiceCollection AddRelayDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var db = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();

        services.AddDbContext<AppDbContext>(options =>
        {
            if (db.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                options.UseSqlServer(db.ConnectionString, sql => sql.EnableRetryOnFailure(3));
            }
            else
            {
                EnsureSqliteDirectory(db.ConnectionString);
                options.UseSqlite(db.ConnectionString);
            }
        });

        return services;
    }

    /// <summary>Creates the schema on first run and performs light-weight maintenance.</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");
        var dbOptions = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        await ctx.Database.EnsureCreatedAsync(ct);

        if (!dbOptions.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            // Better concurrency for the SQLite file under a multi-worker dispatcher.
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", ct);
        }

        logger.LogInformation("Database ready ({Provider})", dbOptions.Provider);
    }

    private static void EnsureSqliteDirectory(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
            var dir = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch
        {
            // ignore - EF will surface a meaningful error if the path is invalid
        }
    }
}

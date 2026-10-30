using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Shipping.Platform;

public static class DatabaseExtensions
{
    /// <summary>
    /// Builds the Npgsql connection string from Db:* settings. On GKE, Db:Host is 127.0.0.1
    /// because the Cloud SQL Auth Proxy sidecar listens on localhost:5432.
    /// </summary>
    public static string GetNpgsqlConnectionString(this IConfiguration cfg, string applicationName)
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = cfg["Db:Host"] ?? "localhost",
            Port = int.TryParse(cfg["Db:Port"], out var port) ? port : 5432,
            Database = cfg["Db:Name"] ?? "shipping",
            Username = cfg["Db:User"] ?? "app",
            Password = cfg["Db:Password"],
            ApplicationName = applicationName,
            MaxPoolSize = 20,
        };
        return b.ConnectionString;
    }

    /// <summary>
    /// POC bootstrap: creates the service's schema + tables on first start (guarded by an advisory lock so
    /// several replicas can start together). Retries while the DB / proxy is still coming up.
    /// For production replace with EF Core migrations run by a Helm pre-install/upgrade Job.
    /// </summary>
    public static async Task EnsureDatabaseAsync<TContext>(
        this IServiceProvider services, string schema, string probeTable, CancellationToken ct = default)
        where TContext : DbContext
    {
        await using var scope = services.CreateAsyncScope();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbBootstrap");
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        const int maxAttempts = 12;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await BootstrapAsync(db, schema, probeTable, log, ct);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Database not ready (attempt {Attempt}/{Max}); retrying in 5s", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private static async Task BootstrapAsync(DbContext db, string schema, string probeTable, ILogger log, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            // Session-level advisory lock; released explicitly below (and implicitly if the connection drops).
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtext({schema}))", ct);
            try
            {
                await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS " + schema, ct);

                var qualified = $"{schema}.{probeTable}";
                var existing = await db.Database
                    .SqlQuery<string?>($"SELECT to_regclass({qualified})::text AS \"Value\"")
                    .SingleAsync(ct);

                if (existing is null)
                {
                    log.LogInformation("Creating tables for schema {Schema}", schema);
                    await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(ct);
                }
            }
            finally
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtext({schema}))", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    // ---- health: the diagram's "DB health ping (SELECT 1)" ----

    public static IServiceCollection AddPlatformHealth<TContext>(this IServiceCollection services) where TContext : DbContext
    {
        services.AddHealthChecks().AddCheck<DbPingHealthCheck<TContext>>("db-ping", tags: new[] { "ready" });
        return services;
    }
}

public sealed class DbPingHealthCheck<TContext>(TContext db) : IHealthCheck where TContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SELECT 1 failed", ex);
        }
    }
}

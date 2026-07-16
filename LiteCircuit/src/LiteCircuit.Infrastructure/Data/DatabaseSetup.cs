using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiteCircuit.Infrastructure.Data;

public static class DatabaseSetup
{
    /// <summary>
    /// Registers AppDbContext against the provider selected in configuration:
    /// Database:Provider = Sqlite | SqlServer | Postgres | MySql
    /// with the matching connection string in ConnectionStrings:{Provider}.
    /// </summary>
    public static IServiceCollection AddLiteCircuitDatabase(this IServiceCollection services, IConfiguration cfg)
    {
        var provider = (cfg["Database:Provider"] ?? "Sqlite").Trim();
        var cs = cfg.GetConnectionString(provider) ?? "Data Source=litecircuit.db";

        services.AddDbContextFactory<AppDbContext>(o => Configure(o, provider, cs));
        // Identity and per-request usage get a scoped context created from the factory.
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        return services;
    }

    private static void Configure(DbContextOptionsBuilder o, string provider, string cs)
    {
        switch (provider.ToLowerInvariant())
        {
            case "sqlite":
                o.UseSqlite(cs);
                break;
            case "sqlserver":
                o.UseSqlServer(cs);
                break;
            case "postgres":
            case "postgresql":
                o.UseNpgsql(cs);
                break;
            case "mysql":
                // Pinned server version avoids a connection at startup (AutoDetect would connect).
                o.UseMySql(cs, Microsoft.EntityFrameworkCore.ServerVersion.Create(
                    new Version(8, 0, 36), Pomelo.EntityFrameworkCore.MySql.Infrastructure.ServerType.MySql));
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown Database:Provider '{provider}'. Use Sqlite, SqlServer, Postgres or MySql.");
        }
    }
}

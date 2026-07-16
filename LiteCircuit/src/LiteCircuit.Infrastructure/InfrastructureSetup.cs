using LiteCircuit.Infrastructure.Ai;
using LiteCircuit.Infrastructure.Data;
using LiteCircuit.Infrastructure.Services;
using LiteCircuit.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiteCircuit.Infrastructure;

public static class InfrastructureSetup
{
    public static IServiceCollection AddLiteCircuit(this IServiceCollection services, IConfiguration cfg, string contentRoot)
    {
        services.AddLiteCircuitDatabase(cfg);
        services.AddLiteCircuitStorage(cfg, contentRoot);

        services.Configure<AiOptions>(cfg.GetSection(AiOptions.Section));
        services.Configure<TavilyOptions>(cfg.GetSection(TavilyOptions.Section));
        services.AddHttpClient("ai").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));

        // Engineering services
        services.AddSingleton<DrcService>();
        services.AddSingleton<AutoRouterService>();
        services.AddSingleton<GerberService>();
        services.AddSingleton<SpiceService>();
        services.AddSingleton<DfmService>();
        services.AddSingleton<ComplianceService>();
        services.AddSingleton<SignalIntegrityService>();
        services.AddSingleton<McadService>();
        services.AddSingleton<ScriptingService>();
        services.AddSingleton<TemplateService>();
        services.AddScoped<BomService>();
        services.AddScoped<VersionControlService>();
        services.AddScoped<LibraryImportService>();

        // AI
        services.AddSingleton<MarkdownService>();
        services.AddScoped<ElectraKernelFactory>();
        services.AddScoped<ElectraChatService>();
        services.AddScoped<DesignAiService>();

        return services;
    }
}

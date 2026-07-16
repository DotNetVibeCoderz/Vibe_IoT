using LiteCircuit.Infrastructure.Data;
using LiteCircuit.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace LiteCircuit.Infrastructure.Ai;

/// <summary>
/// Builds a Semantic Kernel wired to the configured LLM provider.
/// OpenAI, Anthropic, Gemini and Ollama are all consumed through their
/// OpenAI-compatible chat completion endpoints, so a single connector covers all four.
/// </summary>
public class ElectraKernelFactory(
    IOptionsMonitor<AiOptions> options,
    IOptionsMonitor<TavilyOptions> tavily,
    IHttpClientFactory httpFactory,
    IDbContextFactory<AppDbContext> dbFactory,
    BomService bomService,
    DrcService drcService)
{
    public AiOptions Options => options.CurrentValue;

    public Kernel Create(bool withPlugins = true)
    {
        var o = options.CurrentValue;
        var (model, endpoint, apiKey) = o.Resolve();

        if (!o.IsConfigured)
            throw new InvalidOperationException(
                $"AI provider '{o.Provider}' is not configured. Set Ai:ApiKey (or Ai:Providers:{o.Provider}:ApiKey) in appsettings.json.");

        var builder = Kernel.CreateBuilder();
        if (endpoint is null)
            builder.AddOpenAIChatCompletion(model, apiKey);
        else
            builder.AddOpenAIChatCompletion(model, new Uri(endpoint),
                string.IsNullOrEmpty(apiKey) ? "ollama" : apiKey);

        if (withPlugins)
        {
            builder.Plugins.AddFromObject(new UtilityPlugin(), "utility");
            builder.Plugins.AddFromObject(new WebPlugin(httpFactory, tavily.CurrentValue.ApiKey), "web");
            builder.Plugins.AddFromObject(new DataPlugin(dbFactory, bomService), "data");
            builder.Plugins.AddFromObject(new DesignPlugin(dbFactory, drcService), "design");
        }
        return builder.Build();
    }
}

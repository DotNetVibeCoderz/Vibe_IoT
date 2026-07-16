namespace LiteCircuit.Infrastructure.Ai;

/// <summary>Bound from the "Ai" configuration section (appsettings.json).</summary>
public class AiOptions
{
    public const string Section = "Ai";

    /// <summary>OpenAI | Anthropic | Gemini | Ollama</summary>
    public string Provider { get; set; } = "OpenAI";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>Optional endpoint override; each provider has a sensible default.</summary>
    public string Endpoint { get; set; } = "";
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 4000;
    public string SystemPrompt { get; set; } =
        "You are Electra, LiteCircuit's PCB design copilot.";
    public Dictionary<string, ProviderDefaults> Providers { get; set; } = new();

    public class ProviderDefaults
    {
        public string Model { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }

    public (string Model, string? Endpoint, string ApiKey) Resolve()
    {
        Providers.TryGetValue(Provider, out var d);
        var model = !string.IsNullOrWhiteSpace(Model) ? Model : d?.Model ?? "";
        var key = !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey : d?.ApiKey ?? "";
        var endpoint = !string.IsNullOrWhiteSpace(Endpoint) ? Endpoint : d?.Endpoint ?? "";
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = Provider.ToLowerInvariant() switch
            {
                // All providers are consumed through their OpenAI-compatible chat endpoints,
                // so one Semantic Kernel connector serves all four.
                "anthropic" => "https://api.anthropic.com/v1/",
                "gemini" => "https://generativelanguage.googleapis.com/v1beta/openai/",
                "ollama" => "http://localhost:11434/v1",
                _ => "",
            };
        if (string.IsNullOrWhiteSpace(model))
            model = Provider.ToLowerInvariant() switch
            {
                "anthropic" => "claude-sonnet-5",
                "gemini" => "gemini-2.0-flash",
                "ollama" => "llama3.2",
                _ => "gpt-4o-mini",
            };
        return (model, string.IsNullOrWhiteSpace(endpoint) ? null : endpoint, key);
    }

    public bool IsConfigured =>
        Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) || Resolve().ApiKey.Length > 0;
}

public class TavilyOptions
{
    public const string Section = "Tavily";
    public string ApiKey { get; set; } = "";
}

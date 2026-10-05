using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IoTCom.Samples.Medical;

/// <summary>
/// Connection settings for an OpenAI-compatible chat endpoint. Read from environment variables
/// (<c>IOTCOM_AI_PROVIDER</c>, <c>IOTCOM_AI_ENDPOINT</c>, <c>IOTCOM_AI_KEY</c>, <c>IOTCOM_AI_MODEL</c>,
/// <c>IOTCOM_AI_VISION_MODEL</c>, <c>IOTCOM_AI_API_VERSION</c>) or from <c>%APPDATA%/IoTCom.Net/ai.json</c>.
/// Keys are never logged or shown.
/// </summary>
public sealed record AiSettings
{
    /// <summary><c>azure</c> (Azure OpenAI), <c>huggingface</c>, <c>deepseek</c>, <c>openai</c> or any OpenAI-compatible server.</summary>
    public string Provider { get; init; } = "azure";
    /// <summary>Base endpoint, e.g. <c>https://name.openai.azure.com</c>, <c>https://router.huggingface.co/v1</c>.</summary>
    public string Endpoint { get; init; } = string.Empty;
    /// <summary>API key / token.</summary>
    public string ApiKey { get; init; } = string.Empty;
    /// <summary>Text model (Azure: deployment name).</summary>
    public string Model { get; init; } = string.Empty;
    /// <summary>Vision-capable model (defaults to <see cref="Model"/>).</summary>
    public string VisionModel { get; init; } = string.Empty;
    /// <summary>Azure API version.</summary>
    public string ApiVersion { get; init; } = "2025-04-01-preview";

    /// <summary>True when an endpoint, key and model are present.</summary>
    public bool IsConfigured => Endpoint.Length > 0 && ApiKey.Length > 0 && Model.Length > 0;

    /// <summary>Display label without secrets.</summary>
    public string Label => IsConfigured ? $"{ProviderName} · {Model}{(VisionModel.Length > 0 && VisionModel != Model ? $" / {VisionModel}" : "")}" : "not configured";

    private string ProviderName => Provider.ToLowerInvariant() switch
    {
        "azure" => "Azure OpenAI",
        "huggingface" => "Hugging Face",
        "deepseek" => "DeepSeek",
        "openai" => "OpenAI",
        _ => Provider,
    };

    /// <summary>Loads settings: environment variables win over the settings file.</summary>
    public static AiSettings Load()
    {
        var file = FromFile() ?? new AiSettings();
        string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
        return file with
        {
            Provider = Env("IOTCOM_AI_PROVIDER", file.Provider),
            Endpoint = Env("IOTCOM_AI_ENDPOINT", file.Endpoint),
            ApiKey = Env("IOTCOM_AI_KEY", file.ApiKey),
            Model = Env("IOTCOM_AI_MODEL", file.Model),
            VisionModel = Env("IOTCOM_AI_VISION_MODEL", file.VisionModel),
            ApiVersion = Env("IOTCOM_AI_API_VERSION", file.ApiVersion),
        };
    }

    /// <summary>Path of the optional settings file.</summary>
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IoTCom.Net", "ai.json");

    private static AiSettings? FromFile()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath));
            string S(string k) => node?[k]?.GetValue<string>() ?? string.Empty;
            return new AiSettings
            {
                Provider = S("provider") is { Length: > 0 } p ? p : "azure",
                Endpoint = S("endpoint"),
                ApiKey = S("apiKey"),
                Model = S("model"),
                VisionModel = S("visionModel"),
                ApiVersion = S("apiVersion") is { Length: > 0 } v ? v : "2025-04-01-preview",
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Minimal OpenAI-compatible chat client (text and image input).</summary>
public sealed class AiClient(AiSettings settings, HttpClient? http = null) : IDisposable
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    private readonly bool _ownsHttp = http is null;

    /// <summary>Settings in use.</summary>
    public AiSettings Settings { get; } = settings;

    /// <summary>Sends a chat request and returns the assistant text.</summary>
    /// <param name="system">System prompt.</param>
    /// <param name="user">User prompt.</param>
    /// <param name="imagePng">Optional PNG attached to the user message (uses the vision model).</param>
    /// <param name="maxTokens">Output budget (reasoning models also spend it on reasoning).</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="InvalidOperationException">Not configured, or the service returned an error.</exception>
    public async Task<string> CompleteAsync(string system, string user, byte[]? imagePng = null, int maxTokens = 3000, CancellationToken ct = default)
    {
        if (!Settings.IsConfigured) throw new InvalidOperationException("AI is not configured. Set IOTCOM_AI_ENDPOINT, IOTCOM_AI_KEY and IOTCOM_AI_MODEL.");
        var model = imagePng is not null && Settings.VisionModel.Length > 0 ? Settings.VisionModel : Settings.Model;
        var provider = Settings.Provider.ToLowerInvariant();

        JsonNode userContent = imagePng is null
            ? JsonValue.Create(user)!
            : new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = user },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(imagePng) } },
            };
        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = userContent },
            },
        };
        var reasoningModel = model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) || model.StartsWith("o", StringComparison.OrdinalIgnoreCase);
        if (provider is "azure" or "openai")
        {
            body["max_completion_tokens"] = maxTokens;
            if (reasoningModel) body["reasoning_effort"] = "low";
        }
        else
        {
            body["max_tokens"] = maxTokens;
        }

        string url;
        using var request = new HttpRequestMessage(HttpMethod.Post, (Uri?)null);
        var endpoint = Settings.Endpoint.TrimEnd('/');
        if (provider == "azure")
        {
            url = $"{endpoint}/openai/deployments/{Uri.EscapeDataString(model)}/chat/completions?api-version={Uri.EscapeDataString(Settings.ApiVersion)}";
            request.Headers.Add("api-key", Settings.ApiKey);
        }
        else
        {
            body["model"] = model;
            url = endpoint.EndsWith("/chat/completions", StringComparison.Ordinal) ? endpoint : $"{endpoint}/chat/completions";
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.ApiKey);
        }
        request.RequestUri = new Uri(url);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI service returned {(int)response.StatusCode}: {Trim(text)}");
        var json = JsonNode.Parse(text);
        var content = json?["choices"]?[0]?["message"]?["content"];
        var answer = content switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(answer))
        {
            var reason = json?["choices"]?[0]?["finish_reason"]?.GetValue<string>();
            throw new InvalidOperationException($"AI service returned no text (finish_reason: {reason ?? "unknown"}).");
        }
        return answer.Trim();
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

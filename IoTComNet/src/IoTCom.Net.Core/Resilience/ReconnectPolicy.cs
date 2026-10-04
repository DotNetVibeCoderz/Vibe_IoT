namespace IoTCom.Net;

/// <summary>
/// Exponential backoff with jitter for reconnecting endpoints. Dependency-free (no Polly required).
/// </summary>
public sealed record ReconnectPolicy
{
    /// <summary>No automatic reconnect.</summary>
    public static ReconnectPolicy None { get; } = new() { MaxAttempts = 0 };

    /// <summary>Sensible default: 0.5 s doubling up to 30 s, unlimited attempts.</summary>
    public static ReconnectPolicy Default { get; } = new();

    /// <summary>First delay.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for the delay.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Multiplier applied after each failed attempt.</summary>
    public double Multiplier { get; init; } = 2.0;

    /// <summary>Random jitter fraction (0..1) added to each delay.</summary>
    public double Jitter { get; init; } = 0.2;

    /// <summary>Maximum attempts; <c>-1</c> means unlimited, <c>0</c> disables reconnect.</summary>
    public int MaxAttempts { get; init; } = -1;

    /// <summary>True when reconnect is enabled.</summary>
    public bool Enabled => MaxAttempts != 0;

    /// <summary>Delay before attempt number <paramref name="attempt"/> (1-based).</summary>
    public TimeSpan GetDelay(int attempt)
    {
        if (attempt < 1) attempt = 1;
        var ms = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, attempt - 1);
        ms = Math.Min(ms, MaxDelay.TotalMilliseconds);
        if (Jitter > 0) ms += ms * Jitter * Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>True when another attempt is allowed.</summary>
    public bool CanRetry(int attempt) => MaxAttempts < 0 || attempt <= MaxAttempts;
}

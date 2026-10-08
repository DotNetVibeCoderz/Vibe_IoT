using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.AtCommand;

/// <summary>How a command ended.</summary>
public enum AtResult
{
    /// <summary>OK.</summary>
    Ok,
    /// <summary>ERROR (no code).</summary>
    Error,
    /// <summary>+CME ERROR: n (equipment / network).</summary>
    CmeError,
    /// <summary>+CMS ERROR: n (SMS).</summary>
    CmsError,
    /// <summary>CONNECT (data mode).</summary>
    Connect,
    /// <summary>NO CARRIER.</summary>
    NoCarrier,
    /// <summary>BUSY.</summary>
    Busy,
    /// <summary>NO ANSWER.</summary>
    NoAnswer,
    /// <summary>NO DIALTONE.</summary>
    NoDialtone,
    /// <summary>The module asks for data ("&gt; ", e.g. SMS text after AT+CMGS).</summary>
    Prompt,
}

/// <summary>The response to one command: information lines and the final result.</summary>
/// <param name="Command">The command sent.</param>
/// <param name="Lines">Information lines (echo removed).</param>
/// <param name="Result">Final result.</param>
/// <param name="ErrorCode">Numeric code of +CME/+CMS ERROR, if any.</param>
/// <param name="ErrorText">Text of a verbose +CME/+CMS ERROR, if any.</param>
public sealed record AtResponse(string Command, IReadOnlyList<string> Lines, AtResult Result, int? ErrorCode = null, string? ErrorText = null)
{
    /// <summary>Succeeded (OK, CONNECT or a prompt).</summary>
    public bool IsSuccess => Result is AtResult.Ok or AtResult.Connect or AtResult.Prompt;

    /// <summary>Values of the first line starting with <paramref name="prefix"/> (e.g. "+CSQ"), split on commas outside quotes.</summary>
    public IReadOnlyList<string>? Values(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var line = Lines.FirstOrDefault(l => l.StartsWith(prefix + ":", StringComparison.Ordinal));
        return line is null ? null : AtParser.SplitValues(line[(prefix.Length + 1)..]);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Command} → {(Lines.Count > 0 ? string.Join(" | ", Lines) + " · " : "")}{Result}{(ErrorCode is { } c ? $" {c}" : "")}{(ErrorText is { } t ? $" ({t})" : "")}";
}

/// <summary>An unsolicited result code (network registration, incoming SMS, ring, vendor events).</summary>
/// <param name="Line">The line.</param>
/// <param name="Time">When it arrived.</param>
public sealed record AtUrc(string Line, DateTimeOffset Time)
{
    /// <summary>Prefix without the colon (e.g. "+CEREG", "RING").</summary>
    public string Name => Line.IndexOf(':', StringComparison.Ordinal) is var i and > 0 ? Line[..i] : Line;

    /// <summary>Values after the colon.</summary>
    public IReadOnlyList<string> Values => Line.IndexOf(':', StringComparison.Ordinal) is var i and > 0 ? AtParser.SplitValues(Line[(i + 1)..]) : [];
}

/// <summary>
/// Sans-I/O AT response parser (3GPP TS 27.007/27.005 and V.250): splits the byte stream into lines, strips the
/// command echo, collects information lines, recognises final result codes and the SMS prompt, and separates
/// unsolicited result codes — including those that arrive in the middle of a response.
/// </summary>
public sealed class AtParser
{
    private static readonly string[] KnownUrcs =
    [
        "RING", "+CRING", "+CLIP", "+CMTI", "+CMT", "+CDSI", "+CBM", "+CREG", "+CGREG", "+CEREG", "+C5GREG", "+CGEV", "+CTZV", "+CTZE",
        "+CUSD", "+CPIN", "+QIND", "+QIURC", "+QMTRECV", "+QMTSTAT", "+UUSORD", "+UUSOCL", "+CIEV", "+NPSMR", "+QPSMTIMER", "RDY", "POWERED DOWN",
        "^SYSSTART", "+SIMCARD", "+CSCON", "+QNTP",
    ];

    private readonly StringBuilder _line = new();
    private readonly List<string> _info = [];
    private string? _pending;
    private string? _pendingPrefix;
    private readonly Queue<AtResponse> _responses = new();
    private readonly Queue<AtUrc> _urcs = new();

    /// <summary>Starts waiting for the response to <paramref name="command"/> (lines that answer it are no longer URCs).</summary>
    public void Begin(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _pending = command.Trim();
        _info.Clear();
        var c = _pending.ToUpperInvariant();
        _pendingPrefix = c.StartsWith("AT+", StringComparison.Ordinal) || c.StartsWith("AT^", StringComparison.Ordinal)
            ? "+" + new string(c[3..].TakeWhile(ch => char.IsLetterOrDigit(ch)).ToArray()) : null;
    }

    /// <summary>A command is waiting for its final result.</summary>
    public bool Busy => _pending is not null;

    /// <summary>Feeds received bytes.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            var c = (char)b;
            if (c == '\n')
            {
                Line();
                continue;
            }

            if (c == '\r') continue;
            _line.Append(c);
            // The SMS prompt has no line ending.
            if (_pending is not null && _line.Length == 2 && _line[0] == '>' && _line[1] == ' ')
            {
                _line.Clear();
                Complete(AtResult.Prompt);
            }
        }
    }

    /// <summary>Takes the next complete response.</summary>
    public bool TryTakeResponse(out AtResponse? response) => _responses.TryDequeue(out response);

    /// <summary>Takes the next unsolicited result code.</summary>
    public bool TryTakeUrc(out AtUrc? urc) => _urcs.TryDequeue(out urc);

    private void Line()
    {
        var line = _line.ToString().Trim();
        _line.Clear();
        if (line.Length == 0) return;
        if (_pending is not null && string.Equals(line, _pending, StringComparison.OrdinalIgnoreCase)) return; // echo
        if (_pending is null)
        {
            _urcs.Enqueue(new AtUrc(line, DateTimeOffset.UtcNow));
            return;
        }

        switch (line)
        {
            case "OK":
                Complete(AtResult.Ok);
                return;
            case "ERROR":
                Complete(AtResult.Error);
                return;
            case "NO CARRIER":
                Complete(AtResult.NoCarrier);
                return;
            case "BUSY":
                Complete(AtResult.Busy);
                return;
            case "NO ANSWER":
                Complete(AtResult.NoAnswer);
                return;
            case "NO DIALTONE":
                Complete(AtResult.NoDialtone);
                return;
        }

        if (line.StartsWith("CONNECT", StringComparison.Ordinal))
        {
            Complete(AtResult.Connect);
            return;
        }

        if (line.StartsWith("+CME ERROR:", StringComparison.Ordinal) || line.StartsWith("+CMS ERROR:", StringComparison.Ordinal))
        {
            var text = line[11..].Trim();
            var numeric = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
            Complete(line[3] == 'E' ? AtResult.CmeError : AtResult.CmsError, numeric ? code : null, numeric ? null : text);
            return;
        }

        if (IsUrc(line))
        {
            _urcs.Enqueue(new AtUrc(line, DateTimeOffset.UtcNow));
            return;
        }

        _info.Add(line);
    }

    private bool IsUrc(string line)
    {
        var name = line.IndexOf(':', StringComparison.Ordinal) is var i and > 0 ? line[..i] : line;
        if (_pendingPrefix is not null && name.Equals(_pendingPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return KnownUrcs.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private void Complete(AtResult result, int? code = null, string? text = null)
    {
        _responses.Enqueue(new AtResponse(_pending!, [.. _info], result, code, text));
        _info.Clear();
        (_pending, _pendingPrefix) = (null, null);
    }

    /// <summary>Splits "a,\"b,c\",d" into ["a", "b,c", "d"], trimming spaces and quotes.</summary>
    public static IReadOnlyList<string> SplitValues(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (c == ',' && !quoted)
            {
                values.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        values.Add(sb.ToString().Trim());
        return values;
    }

    /// <summary>Meaning of common +CME ERROR codes.</summary>
    public static string CmeErrorName(int code) => code switch
    {
        3 => "operation not allowed",
        4 => "operation not supported",
        10 => "SIM not inserted",
        11 => "SIM PIN required",
        12 => "SIM PUK required",
        13 => "SIM failure",
        14 => "SIM busy",
        16 => "incorrect password",
        30 => "no network service",
        50 => "incorrect parameters",
        100 => "unknown",
        _ => $"error {code}",
    };
}

/// <summary>Network registration status (+CREG/+CEREG &lt;stat&gt;).</summary>
public enum AtRegistration
{
    /// <summary>Not registered, not searching.</summary>
    NotRegistered = 0,
    /// <summary>Registered, home network.</summary>
    Home = 1,
    /// <summary>Searching.</summary>
    Searching = 2,
    /// <summary>Registration denied.</summary>
    Denied = 3,
    /// <summary>Unknown.</summary>
    Unknown = 4,
    /// <summary>Registered, roaming.</summary>
    Roaming = 5,
}

/// <summary>Signal quality from +CSQ.</summary>
/// <param name="Rssi">Raw rssi (0–31, 99 unknown).</param>
/// <param name="Ber">Raw bit error rate (0–7, 99 unknown).</param>
public readonly record struct AtSignal(int Rssi, int Ber)
{
    /// <summary>RSSI in dBm (−113 + 2 × rssi), or null when unknown.</summary>
    public int? Dbm => Rssi is >= 0 and <= 31 ? -113 + (2 * Rssi) : null;

    /// <summary>Bars 0–4.</summary>
    public int Bars => Rssi switch { 99 or < 2 => 0, < 10 => 1, < 15 => 2, < 20 => 3, _ => 4 };

    /// <inheritdoc />
    public override string ToString() => Dbm is { } d ? $"{d} dBm ({Bars}/4)" : "unknown";
}

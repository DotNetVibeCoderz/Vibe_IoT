using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Coap;

/// <summary>A link from a CoRE Link Format document (RFC 6690), e.g. <c>&lt;/sensors/temp&gt;;rt="temperature-c";obs</c>.</summary>
/// <param name="Path">Target path.</param>
/// <param name="Attributes">Attributes; flags such as <c>obs</c> have an empty value.</param>
public sealed record CoapLink(string Path, IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>Resource type (<c>rt</c>).</summary>
    public string? ResourceType => Attributes.GetValueOrDefault("rt");

    /// <summary>Interface description (<c>if</c>).</summary>
    public string? Interface => Attributes.GetValueOrDefault("if");

    /// <summary>Title.</summary>
    public string? Title => Attributes.GetValueOrDefault("title");

    /// <summary>Content format (<c>ct</c>).</summary>
    public ushort? ContentFormat => Attributes.TryGetValue("ct", out var ct) && ushort.TryParse(ct, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>True when the resource is observable (<c>obs</c>).</summary>
    public bool Observable => Attributes.ContainsKey("obs");
}

/// <summary>Parser and formatter for <c>application/link-format</c>.</summary>
public static class CoapLinkFormat
{
    /// <summary>Parses a link-format document (tolerates whitespace and quoted commas).</summary>
    public static IReadOnlyList<CoapLink> Parse(string text)
    {
        var links = new List<CoapLink>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i]))) i++;
            if (i >= text.Length) break;
            if (text[i] != '<') throw new FormatException($"Expected '<' at {i} in link-format.");
            var end = text.IndexOf('>', i);
            if (end < 0) throw new FormatException("Unterminated link target.");
            var path = text[(i + 1)..end];
            i = end + 1;
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            while (i < text.Length && text[i] == ';')
            {
                i++;
                var nameStart = i;
                while (i < text.Length && text[i] is not ('=' or ';' or ',')) i++;
                var name = text[nameStart..i].Trim();
                var value = "";
                if (i < text.Length && text[i] == '=')
                {
                    i++;
                    if (i < text.Length && text[i] == '"')
                    {
                        var close = text.IndexOf('"', i + 1);
                        if (close < 0) throw new FormatException("Unterminated quoted attribute.");
                        value = text[(i + 1)..close];
                        i = close + 1;
                    }
                    else
                    {
                        var vs = i;
                        while (i < text.Length && text[i] is not (';' or ',')) i++;
                        value = text[vs..i];
                    }
                }
                if (name.Length > 0) attrs[name] = value;
            }
            links.Add(new CoapLink(path, attrs));
        }
        return links;
    }

    /// <summary>Formats links (string attributes quoted, <c>ct</c>/<c>sz</c> bare, empty values as flags).</summary>
    public static string Format(IEnumerable<CoapLink> links)
    {
        var sb = new StringBuilder();
        foreach (var link in links)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append('<').Append(link.Path).Append('>');
            foreach (var (k, v) in link.Attributes)
            {
                sb.Append(';').Append(k);
                if (v.Length == 0) continue;
                sb.Append('=');
                if (k is "ct" or "sz") sb.Append(v);
                else sb.Append('"').Append(v).Append('"');
            }
        }
        return sb.ToString();
    }
}

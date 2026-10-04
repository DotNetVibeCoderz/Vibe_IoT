using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace IoTCom.Net.Gallery.Infrastructure;

/// <summary>Small, dependency-free C# highlighter for the Code tab.</summary>
public static partial class CodeHighlighter
{
    [GeneratedRegex("""(?<comment>//[^\n]*|/\*[\s\S]*?\*/)|(?<string>@?\$?"(?:[^"\\\n]|\\.)*"|'(?:[^'\\]|\\.)')|(?<keyword>\b(?:using|namespace|public|private|protected|internal|sealed|static|readonly|const|class|record|struct|interface|enum|new|return|if|else|for|foreach|while|do|switch|case|default|break|continue|await|async|var|void|bool|byte|ushort|short|int|uint|long|ulong|double|float|string|object|true|false|null|this|base|override|virtual|abstract|partial|in|out|is|as|try|catch|finally|throw|when|get|set|init|value|typeof|nameof|with|yield)\b)|(?<number>\b0x[0-9A-Fa-f]+\b|\b\d+(?:\.\d+)?\b)|(?<type>\b[A-Z][A-Za-z0-9]+(?=[\s<(.\[]))""")]
    private static partial Regex Token();

    /// <summary>Builds inlines for <paramref name="code"/> using the given brushes.</summary>
    public static InlineCollection Highlight(string code, IBrush keyword, IBrush str, IBrush comment, IBrush type, IBrush number)
    {
        var inlines = new InlineCollection();
        var last = 0;
        foreach (Match m in Token().Matches(code))
        {
            if (m.Index > last) inlines.Add(new Run(code[last..m.Index]));
            var brush = m.Groups["comment"].Success ? comment
                : m.Groups["string"].Success ? str
                : m.Groups["keyword"].Success ? keyword
                : m.Groups["number"].Success ? number
                : type;
            inlines.Add(new Run(m.Value) { Foreground = brush, FontStyle = m.Groups["comment"].Success ? FontStyle.Italic : FontStyle.Normal });
            last = m.Index + m.Length;
        }
        if (last < code.Length) inlines.Add(new Run(code[last..]));
        return inlines;
    }

    /// <summary>Loads an embedded demo source.</summary>
    public static string LoadSource(string file)
    {
        using var stream = typeof(CodeHighlighter).Assembly.GetManifestResourceStream("Source." + file);
        if (stream is null) return $"// Source for {file} is not embedded.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}

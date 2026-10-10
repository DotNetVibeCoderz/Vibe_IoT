namespace IoTCom.Net.Adapters.Zenoh;

/// <summary>
/// Pure C# Zenoh key-expression rules: validation and matching. Chunks are separated by <c>/</c>; <c>*</c> is exactly one
/// whole chunk, <c>**</c> is any number of chunks (including none, so <c>a/**</c> also matches <c>a</c>), and <c>$*</c>
/// inside a chunk stands for any run of characters (including none), as in <c>sensor$*</c> or <c>$*_temp</c>.
/// </summary>
/// <example>
/// <code>
/// ZenohKeyExpr.Includes("plant/*/temp", "plant/line1/temp");   // true
/// ZenohKeyExpr.Includes("plant/**", "plant/line1/tank/level"); // true
/// ZenohKeyExpr.Intersects("plant/*/temp", "plant/line1/**");   // true
/// </code>
/// </example>
public static class ZenohKeyExpr
{
    private const char Star = '\0';   // marks "$*" inside a tokenised chunk (a real NUL never occurs in a valid key)

    /// <summary>Checks <paramref name="keyExpr"/> against the key-expression grammar; <paramref name="error"/> says what is wrong.</summary>
    public static bool IsValid(string? keyExpr, out string? error)
    {
        error = Validate(keyExpr);
        return error is null;
    }

    /// <summary>Checks <paramref name="keyExpr"/> against the key-expression grammar.</summary>
    public static bool IsValid(string? keyExpr) => Validate(keyExpr) is null;

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="keyExpr"/> is not a valid key expression.</summary>
    public static void ThrowIfInvalid(string? keyExpr, string? paramName = null)
    {
        if (Validate(keyExpr) is { } error) throw new ArgumentException($"'{keyExpr}' is not a valid Zenoh key expression: {error}", paramName ?? nameof(keyExpr));
    }

    /// <summary>True when the key expression contains <c>*</c>, <c>**</c> or <c>$*</c>.</summary>
    public static bool IsWild(string keyExpr)
    {
        ArgumentNullException.ThrowIfNull(keyExpr);
        return keyExpr.Contains('*', StringComparison.Ordinal);
    }

    private static string? Validate(string? keyExpr)
    {
        if (string.IsNullOrEmpty(keyExpr)) return "it is empty";
        if (keyExpr[0] == '/' || keyExpr[^1] == '/') return "it must not start or end with '/'";
        foreach (var chunk in keyExpr.Split('/'))
        {
            if (chunk.Length == 0) return "it contains an empty chunk ('//')";
            if (chunk is "*" or "**") continue;
            for (var i = 0; i < chunk.Length; i++)
            {
                switch (chunk[i])
                {
                    case '?' or '#':
                        return $"'{chunk[i]}' is reserved (selector parameters belong after the key expression, use get)";
                    case '$' when i + 1 >= chunk.Length || chunk[i + 1] != '*':
                        return "'$' must be followed by '*'";
                    case '$':
                        i++;
                        break;
                    case '*':
                        return "'*' must be a whole chunk; use '$*' inside a chunk";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when every key matched by <paramref name="key"/> is also matched by <paramref name="pattern"/> (so for a concrete key:
    /// "does this subscription receive it?").
    /// </summary>
    public static bool Includes(string pattern, string key)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(key);
        return IncludesChunks(pattern.Split('/'), key.Split('/'));
    }

    /// <summary>Alias of <see cref="Includes"/> for the common case of matching a concrete key against a subscription pattern.</summary>
    public static bool Matches(string pattern, string key) => Includes(pattern, key);

    /// <summary>True when at least one key is matched by both expressions (this is how queries find queryables).</summary>
    public static bool Intersects(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return IntersectChunks(a.Split('/'), b.Split('/'));
    }

    private static bool IncludesChunks(string[] p, string[] k)
    {
        var memo = new bool?[p.Length + 1, k.Length + 1];
        bool Go(int i, int j)
        {
            if (memo[i, j] is { } known) return known;
            bool result;
            if (i == p.Length) result = j == k.Length;
            else if (p[i] == "**") result = Go(i + 1, j) || (j < k.Length && Go(i, j + 1));
            else if (j == k.Length || k[j] == "**") result = false;
            else result = ChunkIncludes(p[i], k[j]) && Go(i + 1, j + 1);
            memo[i, j] = result;
            return result;
        }

        return Go(0, 0);
    }

    private static bool IntersectChunks(string[] a, string[] b)
    {
        var memo = new bool?[a.Length + 1, b.Length + 1];
        bool Go(int i, int j)
        {
            if (memo[i, j] is { } known) return known;
            bool result;
            if (i == a.Length && j == b.Length) result = true;
            else if (i < a.Length && a[i] == "**") result = Go(i + 1, j) || (j < b.Length && Go(i, j + 1));
            else if (j < b.Length && b[j] == "**") result = Go(i, j + 1) || (i < a.Length && Go(i + 1, j));
            else if (i == a.Length || j == b.Length) result = false;
            else result = ChunkIntersects(a[i], b[j]) && Go(i + 1, j + 1);
            memo[i, j] = result;
            return result;
        }

        return Go(0, 0);
    }

    /// <summary>Tokenises a chunk: <c>*</c> and <c>$*</c> become <see cref="Star"/>, other characters stay.</summary>
    private static char[] Tokens(string chunk)
    {
        if (chunk == "*") return [Star];
        var tokens = new List<char>(chunk.Length);
        for (var i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] == '$' && i + 1 < chunk.Length && chunk[i + 1] == '*')
            {
                tokens.Add(Star);
                i++;
            }
            else
            {
                tokens.Add(chunk[i]);
            }
        }

        return [.. tokens];
    }

    private static bool ChunkIncludes(string pattern, string chunk)
    {
        if (pattern == chunk) return true;
        var p = Tokens(pattern);
        var k = Tokens(chunk);
        var memo = new bool?[p.Length + 1, k.Length + 1];
        bool Go(int i, int j)
        {
            if (memo[i, j] is { } known) return known;
            bool result;
            if (i == p.Length) result = j == k.Length;
            else if (p[i] == Star) result = Go(i + 1, j) || (j < k.Length && Go(i, j + 1));
            else result = j < k.Length && k[j] == p[i] && Go(i + 1, j + 1);
            memo[i, j] = result;
            return result;
        }

        return Go(0, 0);
    }

    private static bool ChunkIntersects(string a, string b)
    {
        if (a == b) return true;
        var p = Tokens(a);
        var q = Tokens(b);
        var memo = new bool?[p.Length + 1, q.Length + 1];
        bool Go(int i, int j)
        {
            if (memo[i, j] is { } known) return known;
            bool result;
            if (i == p.Length && j == q.Length) result = true;
            else if (i < p.Length && p[i] == Star) result = Go(i + 1, j) || (j < q.Length && Go(i, j + 1));
            else if (j < q.Length && q[j] == Star) result = Go(i, j + 1) || (i < p.Length && Go(i + 1, j));
            else result = i < p.Length && j < q.Length && p[i] == q[j] && Go(i + 1, j + 1);
            memo[i, j] = result;
            return result;
        }

        return Go(0, 0);
    }
}

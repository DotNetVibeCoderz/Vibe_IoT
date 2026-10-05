using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace IoTCom.Net.Protocols.Mavlink.Generator;

/// <summary>
/// Generates MAVLink message classes, enums and a dialect registry from dialect XML files passed as
/// <c>AdditionalFiles</c>. Includes are resolved among the provided files; CRC_EXTRA and the wire order (fields sorted
/// by type size, extensions last) follow the MAVLink specification.
/// MSBuild properties: <c>MavlinkNamespace</c> (default <c>$(RootNamespace).Mavlink</c>) and <c>MavlinkDialectName</c> (default <c>Mavlink</c>).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MavlinkGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor XmlError = new("MAV001", "Invalid MAVLink dialect", "MAVLink dialect '{0}' could not be read: {1}",
        "MAVLink", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingInclude = new("MAV002", "MAVLink include not found",
        "MAVLink dialect '{0}' includes '{1}', which is not in AdditionalFiles", "MAVLink", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(t => t.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select((t, ct) => (Path: t.Path, Text: t.GetText(ct)?.ToString() ?? ""))
            .Where(f => f.Text.Contains("<mavlink>"))
            .Collect();
        var options = context.AnalyzerConfigOptionsProvider.Select((o, _) =>
        {
            o.GlobalOptions.TryGetValue("build_property.MavlinkNamespace", out var ns);
            o.GlobalOptions.TryGetValue("build_property.RootNamespace", out var root);
            o.GlobalOptions.TryGetValue("build_property.MavlinkDialectName", out var name);
            return (Namespace: string.IsNullOrWhiteSpace(ns) ? (string.IsNullOrWhiteSpace(root) ? "Mavlink" : root + ".Mavlink") : ns!,
                Dialect: string.IsNullOrWhiteSpace(name) ? "Mavlink" : name!);
        });
        context.RegisterSourceOutput(files.Combine(options), (spc, input) => Generate(spc, input.Left, input.Right.Namespace, input.Right.Dialect));
    }

    private sealed class Field
    {
        public string Name = "";
        public string Type = "";       // base C type
        public int ArrayLength;        // 0 = scalar
        public string? Enum;
        public string? Units;
        public string Description = "";
        public bool Extension;
        public int Size => TypeSize(Type) * Math.Max(1, ArrayLength);
    }

    private sealed class Message
    {
        public uint Id;
        public string Name = "";
        public string Description = "";
        public bool Deprecated;
        public List<Field> Fields = new();
    }

    private sealed class EnumDef
    {
        public string Name = "";
        public string Description = "";
        public bool Bitmask;
        public List<(string Name, ulong Value, string Description)> Entries = new();
    }

    private static int TypeSize(string t) => t switch
    {
        "char" or "uint8_t" or "int8_t" or "uint8_t_mavlink_version" => 1,
        "uint16_t" or "int16_t" => 2,
        "uint32_t" or "int32_t" or "float" => 4,
        "uint64_t" or "int64_t" or "double" => 8,
        _ => throw new FormatException("unknown MAVLink type " + t),
    };

    private static string CsType(string t) => t switch
    {
        "char" => "byte",
        "uint8_t" or "uint8_t_mavlink_version" => "byte",
        "int8_t" => "sbyte",
        "uint16_t" => "ushort",
        "int16_t" => "short",
        "uint32_t" => "uint",
        "int32_t" => "int",
        "float" => "float",
        "uint64_t" => "ulong",
        "int64_t" => "long",
        "double" => "double",
        _ => throw new FormatException("unknown MAVLink type " + t),
    };

    private static void Generate(SourceProductionContext spc, ImmutableArray<(string Path, string Text)> files, string ns, string dialect)
    {
        if (files.IsEmpty) return;
        var byName = new Dictionary<string, (string Path, XElement Root)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            try
            {
                byName[System.IO.Path.GetFileName(f.Path)] = (f.Path, XElement.Parse(f.Text));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(XmlError, Location.None, f.Path, ex.Message));
            }
        }

        var messages = new SortedDictionary<uint, Message>();
        var enums = new Dictionary<string, EnumDef>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Load(string file)
        {
            if (!visited.Add(file)) return;
            var (path, root) = byName[file];
            foreach (var inc in root.Elements("include"))
            {
                var name = System.IO.Path.GetFileName(inc.Value.Trim());
                if (byName.ContainsKey(name)) Load(name);
                else spc.ReportDiagnostic(Diagnostic.Create(MissingInclude, Location.None, path, name));
            }
            foreach (var e in root.Element("enums")?.Elements("enum") ?? Enumerable.Empty<XElement>())
            {
                var name = (string)e.Attribute("name")!;
                if (!enums.TryGetValue(name, out var def))
                {
                    def = new EnumDef { Name = name, Description = Clean(e.Element("description")?.Value), Bitmask = (string?)e.Attribute("bitmask") == "true" };
                    enums[name] = def;
                }
                foreach (var entry in e.Elements("entry"))
                {
                    var en = (string)entry.Attribute("name")!;
                    if (def.Entries.Any(x => x.Name == en)) continue;
                    var raw = (string)entry.Attribute("value")!;
                    var value = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? ulong.Parse(raw.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                        : ulong.Parse(raw, CultureInfo.InvariantCulture);
                    def.Entries.Add((en, value, Clean(entry.Element("description")?.Value)));
                }
            }
            foreach (var m in root.Element("messages")?.Elements("message") ?? Enumerable.Empty<XElement>())
            {
                var msg = new Message
                {
                    Id = uint.Parse((string)m.Attribute("id")!, CultureInfo.InvariantCulture),
                    Name = (string)m.Attribute("name")!,
                    Description = Clean(m.Element("description")?.Value),
                    Deprecated = m.Element("deprecated") is not null,
                };
                var ext = false;
                foreach (var c in m.Elements())
                {
                    if (c.Name == "extensions") ext = true;
                    if (c.Name != "field") continue;
                    var type = (string)c.Attribute("type")!;
                    var f = new Field
                    {
                        Name = (string)c.Attribute("name")!,
                        Enum = (string?)c.Attribute("enum"),
                        Units = (string?)c.Attribute("units"),
                        Description = Clean(c.Value),
                        Extension = ext,
                    };
                    var bracket = type.IndexOf('[');
                    if (bracket >= 0)
                    {
                        f.Type = type.Substring(0, bracket);
                        f.ArrayLength = int.Parse(type.Substring(bracket + 1, type.Length - bracket - 2), CultureInfo.InvariantCulture);
                    }
                    else f.Type = type;
                    msg.Fields.Add(f);
                }
                messages[msg.Id] = msg;
            }
        }

        foreach (var file in byName.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()) Load(file);

        var enumNames = new HashSet<string>(enums.Keys, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> IoTCom.Net MAVLink generator — do not edit. Message definitions: MAVLink (MIT). </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591, CS0618, CA1707, CA1028, CA1008, CA1069, CA1027, CA1711, CA1720, CA1819, CA1724, CA1716, CA1056, CA1054, CA2217");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers.Binary;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();

        foreach (var e in enums.Values.OrderBy(e => e.Name, StringComparer.Ordinal)) WriteEnum(sb, e);
        spc.AddSource($"{dialect}.Enums.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));

        foreach (var chunk in messages.Values.Select((m, i) => (m, i)).GroupBy(x => x.i / 40))
        {
            sb.Clear();
            sb.AppendLine("// <auto-generated> IoTCom.Net MAVLink generator — do not edit. Message definitions: MAVLink (MIT). </auto-generated>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("#pragma warning disable CS1591, CS0618, CA1707, CA1819, CA1720, CA1711, CA1724, CA1716, CA1056, CA1054, CA1305, CA1062, CA1822, CA2208, CA1065");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Buffers.Binary;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
            foreach (var (m, _) in chunk) WriteMessage(sb, m, enumNames);
            spc.AddSource($"{dialect}.Messages{chunk.Key}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        sb.Clear();
        WriteDialect(sb, ns, dialect, messages.Values.ToList());
        spc.AddSource($"{dialect}.Dialect.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = string.Join(" ", text!.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries));
        return t.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static readonly char[] Underscore = ['_'];

    /// <summary>MAV_TYPE → MavType; global_position → GlobalPosition.</summary>
    internal static string Pascal(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var part in name.Split(Underscore, StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append(char.ToUpperInvariant(part[0]));
            sb.Append(part.Length > 1 ? (IsAllUpper(part) ? part.Substring(1).ToLowerInvariant() : part.Substring(1)) : "");
        }
        var s = sb.ToString();
        return s.Length > 0 && char.IsDigit(s[0]) ? "_" + s : s;
    }

    private static bool IsAllUpper(string s) => s.All(c => !char.IsLetter(c) || char.IsUpper(c));

    /// <summary>Wire order: base fields sorted by element size (stable), extensions after in declaration order.</summary>
    private static List<Field> WireOrder(Message m) =>
        m.Fields.Where(f => !f.Extension).Select((f, i) => (f, i)).OrderByDescending(x => TypeSize(x.f.Type)).ThenBy(x => x.i).Select(x => x.f)
            .Concat(m.Fields.Where(f => f.Extension)).ToList();

    private static byte CrcExtra(Message m)
    {
        ushort crc = 0xFFFF;
        void Acc(string s)
        {
            foreach (var b in Encoding.ASCII.GetBytes(s)) crc = X25(crc, b);
        }
        Acc(m.Name + " ");
        foreach (var f in WireOrder(m).Where(f => !f.Extension))
        {
            Acc((f.Type == "uint8_t_mavlink_version" ? "uint8_t" : f.Type) + " ");
            Acc(f.Name + " ");
            if (f.ArrayLength > 0) crc = X25(crc, (byte)f.ArrayLength);
        }
        return (byte)((crc & 0xFF) ^ (crc >> 8));
    }

    private static ushort X25(ushort crc, byte b)
    {
        unchecked
        {
            var t = (byte)(b ^ (byte)(crc & 0xFF));
            t ^= (byte)(t << 4);
            return (ushort)((crc >> 8) ^ (t << 8) ^ (t << 3) ^ (t >> 4));
        }
    }

    private static void WriteEnum(StringBuilder sb, EnumDef e)
    {
        var name = Pascal(e.Name);
        var underlying = e.Entries.Any(x => x.Value > uint.MaxValue) ? "ulong" : "uint";
        if (e.Description.Length > 0) sb.AppendLine($"/// <summary>{e.Description}</summary>");
        if (e.Bitmask) sb.AppendLine("[Flags]");
        sb.AppendLine($"public enum {name} : {underlying}");
        sb.AppendLine("{");
        if (!e.Entries.Any(x => x.Value == 0)) sb.AppendLine("    /// <summary>No value.</summary>\n    None = 0,");
        var used = new HashSet<string>(StringComparer.Ordinal) { "None" };
        foreach (var (en, value, desc) in e.Entries)
        {
            var stripped = en.StartsWith(e.Name + "_", StringComparison.Ordinal) ? en.Substring(e.Name.Length + 1) : en;
            var member = Pascal(stripped);
            if (member.Length == 0 || member == name || !used.Add(member)) member = Pascal(en);
            used.Add(member);
            if (desc.Length > 0) sb.AppendLine($"    /// <summary>{desc}</summary>");
            sb.AppendLine($"    {member} = {value.ToString(CultureInfo.InvariantCulture)},");
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static string PropertyName(Field f, string className)
    {
        var p = Pascal(f.Name);
        if (p == className) return "Value";   // PARAM_VALUE.param_value → ParamValue.Value
        return p is "MavlinkId" or "MavlinkName" or "MavlinkCrcExtra" or "MinLength" or "MaxLength" ? p + "Value" : p;
    }

    private static void WriteMessage(StringBuilder sb, Message m, HashSet<string> enums)
    {
        var cls = Pascal(m.Name);
        var wire = WireOrder(m);
        var min = wire.Where(f => !f.Extension).Sum(f => f.Size);
        var max = wire.Sum(f => f.Size);
        var crc = CrcExtra(m);
        sb.AppendLine($"/// <summary>{(m.Description.Length > 0 ? m.Description : m.Name)} (MAVLink message {m.Id}, {m.Name}).</summary>");
        if (m.Deprecated) sb.AppendLine("[Obsolete(\"Deprecated in the MAVLink definitions.\")]");
        sb.AppendLine($"public sealed partial class {cls} : global::IoTCom.Net.Protocols.Mavlink.IMavlinkMessage");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>Message id.</summary>\n    public const uint MavlinkId = {m.Id};");
        sb.AppendLine($"    /// <summary>CRC_EXTRA seed.</summary>\n    public const byte MavlinkCrcExtra = {crc};");
        sb.AppendLine($"    /// <summary>Payload length without extensions.</summary>\n    public const int MinLength = {min};");
        sb.AppendLine($"    /// <summary>Payload length with extensions.</summary>\n    public const int MaxLength = {max};");
        sb.AppendLine($"    /// <summary>MAVLink name.</summary>\n    public const string MavlinkName = \"{m.Name}\";");
        sb.AppendLine("    uint global::IoTCom.Net.Protocols.Mavlink.IMavlinkMessage.MessageId => MavlinkId;");
        sb.AppendLine("    byte global::IoTCom.Net.Protocols.Mavlink.IMavlinkMessage.CrcExtra => MavlinkCrcExtra;");
        sb.AppendLine("    string global::IoTCom.Net.Protocols.Mavlink.IMavlinkMessage.Name => MavlinkName;");
        sb.AppendLine("    int global::IoTCom.Net.Protocols.Mavlink.IMavlinkMessage.MaxPayloadLength => MaxLength;");
        sb.AppendLine();

        foreach (var f in m.Fields)
        {
            var prop = PropertyName(f, cls);
            var doc = f.Description + (f.Units is null ? "" : $" [{Clean(f.Units)}]") + (f.Extension ? " (extension)" : "");
            if (doc.Length > 0) sb.AppendLine($"    /// <summary>{doc}</summary>");
            if (f.Type == "char" && f.ArrayLength > 0) sb.AppendLine($"    public string {prop} {{ get; set; }} = \"\";");
            else if (f.ArrayLength > 0) sb.AppendLine($"    public {CsType(f.Type)}[] {prop} {{ get; set; }} = new {CsType(f.Type)}[{f.ArrayLength}];");
            else if (f.Enum is not null && enums.Contains(f.Enum)) sb.AppendLine($"    public {Pascal(f.Enum)} {prop} {{ get; set; }}");
            else if (f.Type == "uint8_t_mavlink_version") sb.AppendLine($"    public byte {prop} {{ get; set; }} = 3;");
            else sb.AppendLine($"    public {CsType(f.Type)} {prop} {{ get; set; }}");
        }
        sb.AppendLine();

        // Serialize (always the full length; MAVLink 2 truncation happens in the framer).
        sb.AppendLine("    /// <summary>Writes the full payload (with extensions) into <paramref name=\"buffer\"/> and returns its length.</summary>");
        sb.AppendLine("    public int Serialize(Span<byte> buffer)");
        sb.AppendLine("    {");
        sb.AppendLine("        var b = buffer.Slice(0, MaxLength);");
        sb.AppendLine("        b.Clear();");
        var offset = 0;
        foreach (var f in wire)
        {
            var prop = PropertyName(f, cls);
            var size = TypeSize(f.Type);
            var cast = f.Enum is not null && enums.Contains(f.Enum) && f.ArrayLength == 0 ? $"({CsType(f.Type)})" : "";
            if (f.Type == "char" && f.ArrayLength > 0)
                sb.AppendLine($"        global::System.Text.Encoding.UTF8.GetBytes({prop}.Length > {f.ArrayLength} ? {prop}.Substring(0, {f.ArrayLength}) : {prop}, b.Slice({offset}, {f.ArrayLength}));");
            else if (f.ArrayLength > 0)
                sb.AppendLine($"        for (var i = 0; i < {f.ArrayLength} && i < {prop}.Length; i++) {Write(f.Type, $"b.Slice({offset} + i * {size})", $"{prop}[i]")};");
            else
                sb.AppendLine($"        {Write(f.Type, $"b.Slice({offset})", cast + prop)};");
            offset += f.Size;
        }
        sb.AppendLine("        return MaxLength;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Deserialize (zero-extends truncated MAVLink 2 payloads).
        sb.AppendLine("    /// <summary>Reads a payload (shorter payloads are zero-extended, as MAVLink 2 requires).</summary>");
        sb.AppendLine($"    public static {cls} Deserialize(ReadOnlySpan<byte> payload)");
        sb.AppendLine("    {");
        sb.AppendLine("        Span<byte> b = stackalloc byte[MaxLength];");
        sb.AppendLine("        b.Clear();");
        sb.AppendLine("        payload.Slice(0, global::System.Math.Min(payload.Length, MaxLength)).CopyTo(b);");
        sb.AppendLine($"        var m = new {cls}();");
        offset = 0;
        foreach (var f in wire)
        {
            var prop = PropertyName(f, cls);
            var size = TypeSize(f.Type);
            var isEnum = f.Enum is not null && enums.Contains(f.Enum) && f.ArrayLength == 0;
            if (f.Type == "char" && f.ArrayLength > 0)
                sb.AppendLine($"        {{ var s = b.Slice({offset}, {f.ArrayLength}); var z = s.IndexOf((byte)0); m.{prop} = global::System.Text.Encoding.UTF8.GetString(z < 0 ? s : s.Slice(0, z)); }}");
            else if (f.ArrayLength > 0)
                sb.AppendLine($"        for (var i = 0; i < {f.ArrayLength}; i++) m.{prop}[i] = {Read(f.Type, $"b.Slice({offset} + i * {size})")};");
            else
                sb.AppendLine($"        m.{prop} = {(isEnum ? $"({Pascal(f.Enum!)})" : "")}{Read(f.Type, $"b.Slice({offset})")};");
            offset += f.Size;
        }
        sb.AppendLine("        return m;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <inheritdoc />");
        sb.AppendLine($"    public override string ToString() => \"{m.Name} {{\" + {ToStringBody(m, cls)} + \" }}\";");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static string ToStringBody(Message m, string cls)
    {
        var parts = m.Fields.Take(8).Select(f =>
        {
            var p = PropertyName(f, cls);
            return f.ArrayLength > 0 && f.Type != "char" ? $"\" {f.Name}=[{f.ArrayLength}]\"" : $"\" {f.Name}=\" + global::System.FormattableString.Invariant($\"{{{p}}}\")";
        });
        var body = string.Join(" + ", parts);
        return body.Length == 0 ? "\"\"" : body + (m.Fields.Count > 8 ? " + \" …\"" : "");
    }

    private static string Write(string type, string span, string value) => type switch
    {
        "char" or "uint8_t" or "uint8_t_mavlink_version" => $"{span}[0] = (byte)({value})",
        "int8_t" => $"{span}[0] = unchecked((byte)({value}))",
        "uint16_t" => $"global::System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian({span}, (ushort)({value}))",
        "int16_t" => $"global::System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian({span}, (short)({value}))",
        "uint32_t" => $"global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian({span}, (uint)({value}))",
        "int32_t" => $"global::System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian({span}, (int)({value}))",
        "float" => $"global::System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian({span}, {value})",
        "uint64_t" => $"global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian({span}, (ulong)({value}))",
        "int64_t" => $"global::System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian({span}, (long)({value}))",
        "double" => $"global::System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian({span}, {value})",
        _ => throw new FormatException(type),
    };

    private static string Read(string type, string span) => type switch
    {
        "char" or "uint8_t" or "uint8_t_mavlink_version" => $"{span}[0]",
        "int8_t" => $"unchecked((sbyte){span}[0])",
        "uint16_t" => $"global::System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian({span})",
        "int16_t" => $"global::System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian({span})",
        "uint32_t" => $"global::System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian({span})",
        "int32_t" => $"global::System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian({span})",
        "float" => $"global::System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian({span})",
        "uint64_t" => $"global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian({span})",
        "int64_t" => $"global::System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian({span})",
        "double" => $"global::System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian({span})",
        _ => throw new FormatException(type),
    };

    private static void WriteDialect(StringBuilder sb, string ns, string dialect, List<Message> messages)
    {
        sb.AppendLine("// <auto-generated> IoTCom.Net MAVLink generator — do not edit. </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS0618, CA1822");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>The {dialect} MAVLink dialect: {messages.Count} messages, generated from XML.</summary>");
        sb.AppendLine($"public sealed partial class {dialect}Dialect : global::IoTCom.Net.Protocols.Mavlink.IMavlinkDialect");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>Shared instance.</summary>\n    public static {dialect}Dialect Instance {{ get; }} = new();");
        sb.AppendLine();
        sb.AppendLine("    private static readonly global::IoTCom.Net.Protocols.Mavlink.MavlinkMessageInfo[] All =");
        sb.AppendLine("    [");
        foreach (var m in messages)
        {
            var cls = Pascal(m.Name);
            sb.AppendLine($"        new({cls}.MavlinkId, {cls}.MavlinkName, {cls}.MavlinkCrcExtra, {cls}.MinLength, {cls}.MaxLength),");
        }
        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Dialect name.</summary>");
        sb.AppendLine($"    public string Name => \"{dialect}\";");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>All messages of the dialect.</summary>");
        sb.AppendLine("    public IReadOnlyList<global::IoTCom.Net.Protocols.Mavlink.MavlinkMessageInfo> Messages => All;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Looks up a message by id.</summary>");
        sb.AppendLine("    public bool TryGetInfo(uint id, out global::IoTCom.Net.Protocols.Mavlink.MavlinkMessageInfo info)");
        sb.AppendLine("    {");
        sb.AppendLine("        var lo = 0; var hi = All.Length - 1;");
        sb.AppendLine("        while (lo <= hi) { var mid = (lo + hi) >> 1; var v = All[mid].Id; if (v == id) { info = All[mid]; return true; } if (v < id) lo = mid + 1; else hi = mid - 1; }");
        sb.AppendLine("        info = default;");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Decodes a payload into its message class, or null for an unknown id.</summary>");
        sb.AppendLine("    public global::IoTCom.Net.Protocols.Mavlink.IMavlinkMessage? Deserialize(uint id, ReadOnlySpan<byte> payload) => id switch");
        sb.AppendLine("    {");
        foreach (var m in messages) sb.AppendLine($"        {m.Id} => {Pascal(m.Name)}.Deserialize(payload),");
        sb.AppendLine("        _ => null,");
        sb.AppendLine("    };");
        sb.AppendLine("}");
    }
}

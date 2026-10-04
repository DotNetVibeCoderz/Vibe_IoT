using System.ComponentModel;

namespace IoTCom.Net.Gallery.Infrastructure;

/// <summary>A string in English and Bahasa Indonesia.</summary>
public readonly record struct Text(string En, string Id)
{
    public string Get(string lang) => lang == "id" ? Id : En;
    public override string ToString() => Get(Loc.Instance.Language);
}

/// <summary>
/// Runtime-switchable localisation. XAML binds through the indexer:
/// <c>{Binding [run], Source={x:Static i:Loc.Instance}}</c>; switching raises <c>Item[]</c> so every label updates.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private static readonly Dictionary<string, Text> Strings = new()
    {
        ["gallery"] = new("Gallery", "Galeri"),
        ["tagline"] = new("Learn every protocol by running it", "Pelajari setiap protokol dengan menjalankannya"),
        ["search"] = new("Search demos or protocols", "Cari demo atau protokol"),
        ["run"] = new("RUN", "JALANKAN"),
        ["code"] = new("CODE", "KODE"),
        ["docs"] = new("DOCS", "DOKUMENTASI"),
        ["traffic"] = new("TRAFFIC", "LALU LINTAS"),
        ["start"] = new("START DEMO", "MULAI DEMO"),
        ["stop"] = new("STOP DEMO", "HENTIKAN DEMO"),
        ["noHardware"] = new("No hardware needed", "Tanpa perangkat keras"),
        ["frames"] = new("frames", "frame"),
        ["clear"] = new("Clear", "Bersihkan"),
        ["idle"] = new("Press Start demo to bring the simulated devices online.", "Tekan Mulai demo untuk menyalakan perangkat simulasi."),
        ["noFrames"] = new("No frames yet. Start the demo and interact with it — every byte on the wire appears here, decoded field by field.",
                          "Belum ada frame. Mulai demo lalu berinteraksi — setiap byte di jalur muncul di sini, diurai per field."),
        ["selectFrame"] = new("Select a frame to see its hex dump.", "Pilih frame untuk melihat hex dump-nya."),
        ["sourceNote"] = new("This is the exact file compiled into the Gallery.", "Ini adalah file yang sama persis yang dikompilasi ke Galeri."),
        ["credit"] = new(IoTComInfo.CreditEn, IoTComInfo.CreditId),
        ["language"] = new("BAHASA", "ENGLISH"),
        ["theme"] = new("DARK", "GELAP"),
        ["themeLight"] = new("LIGHT", "TERANG"),
        ["beginner"] = new("Beginner", "Pemula"),
        ["intermediate"] = new("Intermediate", "Menengah"),
        ["advanced"] = new("Advanced", "Lanjutan"),
        ["Industrial"] = new("INDUSTRIAL", "INDUSTRI"),
        ["Navigation"] = new("NAVIGATION & MARINE", "NAVIGASI & MARITIM"),
        ["Building"] = new("SMART BUILDING & STAGE", "GEDUNG PINTAR & PANGGUNG"),
        ["Messaging"] = new("MESSAGING", "PESAN"),
        ["Workbench"] = new("PROTOCOL WORKBENCH", "MEJA KERJA PROTOKOL"),
    };

    private string _language = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? LanguageChanged;

    public string Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke();
        }
    }

    public string this[string key] => Strings.TryGetValue(key, out var t) ? t.Get(_language) : key;

    /// <summary>Immutable string table for the current language. A new instance per language lets compiled
    /// bindings (<c>{Binding L[key]}</c>) refresh simply by re-reading the property.</summary>
    public LocTable Table => _language == "id" ? IdTable : EnTable;

    private static readonly LocTable EnTable = new("en");
    private static readonly LocTable IdTable = new("id");

    public static string T(Text text) => text.Get(Instance.Language);

    /// <summary>Inline bilingual string for demo views: <c>L("Temperature", "Suhu")</c>.</summary>
    public static string L(string en, string id) => Instance.Language == "id" ? id : en;

    /// <summary>String table bound by XAML.</summary>
    public sealed class LocTable(string language)
    {
        public string this[string key] => Strings.TryGetValue(key, out var t) ? t.Get(language) : key;
    }
}

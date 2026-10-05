using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class VehicleDiagnosticsDemo
{
    // Instrument-cluster screen: anthracite glass, amber needle colour for rpm, white for speed.
    private static readonly IBrush ClusterBg = new SolidColorBrush(Color.Parse("#14181C"));
    private static readonly IBrush ClusterLine = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush ClusterLabel = new SolidColorBrush(Color.Parse("#8C96A0"));
    private static readonly IBrush ClusterWhite = new SolidColorBrush(Color.Parse("#F1F2EE"));
    private static readonly IBrush ClusterDim = new SolidColorBrush(Color.Parse("#4A5058"));
    private static readonly IBrush CoolantOk = new SolidColorBrush(Color.Parse("#4FC3A1"));

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        // ---- options -----------------------------------------------------------------------------------
        var writes = new CheckBox { Content = L("Allow writes (clear DTCs, write data)", "Izinkan penulisan (hapus DTC, tulis data)") };
        writes.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AllowWrites)));
        ToolTip.SetTip(writes, L("The tester is read-only by default; real ECUs keep the change.", "Tester read-only secara bawaan; ECU sungguhan menyimpan perubahan."));
        var native = new TextBlock
        {
            Text = L("iotcom_isotp native library not found — build it with: cargo build --release (rust/)", "Pustaka native iotcom_isotp tidak ditemukan — build dengan: cargo build --release (rust/)"),
            Foreground = Palette.Red, IsVisible = !NativeAvailable, TextWrapping = TextWrapping.Wrap,
        };

        // ---- cluster: the signature — an analogue tachometer and the tell-tale strip of a real dashboard --------
        var tach = new Tachometer { Width = 250, Height = 190 };
        tach.Bind(Tachometer.ValueProperty, new Binding(nameof(Rpm)));
        var rpmText = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 40, Foreground = ClusterWhite, HorizontalAlignment = HorizontalAlignment.Center };
        rpmText.Bind(TextBlock.TextProperty, new Binding(nameof(Rpm)) { StringFormat = "{0:0}" });
        var tachFace = new Panel
        {
            Children =
            {
                tach,
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 18),
                    Children = { rpmText, new TextBlock { Text = "RPM", Foreground = ClusterLabel, FontSize = 11, LetterSpacing = 3, HorizontalAlignment = HorizontalAlignment.Center } },
                },
            },
        };

        var speedText = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 96, LineHeight = 92, Foreground = ClusterWhite, HorizontalAlignment = HorizontalAlignment.Center };
        speedText.Bind(TextBlock.TextProperty, new Binding(nameof(Speed)) { StringFormat = "{0:0}" });
        var speedFace = Stack(0, speedText,
            new TextBlock { Text = "KM/H", Foreground = ClusterLabel, FontSize = 12, LetterSpacing = 3, HorizontalAlignment = HorizontalAlignment.Center });
        speedFace.VerticalAlignment = VerticalAlignment.Center;

        var coolantText = Bound(new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 34 }, nameof(Coolant), "{0:0} °C");
        coolantText.Bind(TextBlock.ForegroundProperty, new Binding(nameof(Coolant)) { Converter = new FuncValueConverter<double, IBrush>(c => c > 105 ? Palette.Red : CoolantOk) });

        Control Small(string label, string path, string format) =>
            Columns("*,auto",
                new TextBlock { Text = label.ToUpperInvariant(), Foreground = ClusterLabel, FontSize = 10, LetterSpacing = 1.2, VerticalAlignment = VerticalAlignment.Center },
                Bound(new TextBlock { FontFamily = mono, FontSize = 13, Foreground = ClusterWhite }, path, format));

        var side = Stack(8,
            new TextBlock { Text = L("COOLANT", "PENDINGIN"), Foreground = ClusterLabel, FontSize = 10, LetterSpacing = 1.2 },
            coolantText,
            Sparkline(CoolantTrend, CoolantOk, 30),
            new Border { Height = 1, Background = ClusterLine, Margin = new Thickness(0, 2) },
            Small(L("Throttle", "Throttle"), nameof(Throttle), "{0:0} %"),
            Small(L("Load", "Beban"), nameof(Load), "{0:0} %"),
            Small("MAF", nameof(Maf), "{0:0.0} g/s"),
            Small(L("Battery", "Aki"), nameof(Voltage), "{0:0.0} V"),
            Small(L("Fuel", "BBM"), nameof(Fuel), "{0:0.0} %"),
            Small(L("CAN frames", "Frame CAN"), nameof(CanFrames), "{0:N0}"));

        // Tell-tales: lit in their IEC colour, dark glass when off — the vocabulary a mechanic reads first.
        Control Telltale(string text, string path, Func<object?, bool> isOn, IBrush lit)
        {
            var label = new TextBlock { Text = text, FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 12, LetterSpacing = 1.6 };
            var lamp = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 4), BorderThickness = new Thickness(1), Child = label };
            lamp.Bind(Border.BackgroundProperty, new Binding(path) { Converter = new FuncValueConverter<object?, IBrush>(v => isOn(v) ? lit : Brushes.Transparent) });
            lamp.Bind(Border.BorderBrushProperty, new Binding(path) { Converter = new FuncValueConverter<object?, IBrush>(v => isOn(v) ? lit : ClusterLine) });
            label.Bind(TextBlock.ForegroundProperty, new Binding(path) { Converter = new FuncValueConverter<object?, IBrush>(v => isOn(v) ? Palette.Ink : ClusterDim) });
            return lamp;
        }
        var telltales = new WrapPanel
        {
            ItemSpacing = 10, LineSpacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                Telltale(L("CHECK ENGINE", "CEK MESIN"), nameof(Mil), v => v is true, Palette.Amber),
                Telltale(L("COOLANT TEMP", "SUHU PENDINGIN"), nameof(Coolant), v => v is double c && c > 105, Palette.Red),
                Telltale(L("DIAG SESSION", "SESI DIAGNOSTIK"), nameof(Session), v => v is string t && t != "Default", Palette.Blue),
                Telltale("SECURITY", nameof(Unlocked), v => v is true, Palette.Green),
            },
        };

        var cluster = new Border
        {
            Background = ClusterBg, CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 16), BorderBrush = ClusterLine, BorderThickness = new Thickness(1),
            Child = Stack(12,
                Columns("auto,24,*,24,200", tachFace, new Border(), speedFace, new Border(), side),
                new Border { Height = 1, Background = ClusterLine },
                telltales),
        };

        // ---- identification ----------------------------------------------------------------------------
        Control Field(string label, string path) => Stack(1, Eyebrow(label), Bound(new TextBlock { FontFamily = mono, FontSize = 14, TextWrapping = TextWrapping.Wrap }, path, "{0}"));
        var ident = Card(Stack(10,
            Field("VIN · DID F190", nameof(Vin)),
            Field(L("Part number · DID F187", "Nomor part · DID F187"), nameof(PartNumber)),
            Field(L("Software · DID F189", "Perangkat lunak · DID F189"), nameof(SoftwareVersion)),
            Field(L("Workshop code · DID F198", "Kode bengkel · DID F198"), nameof(WorkshopCode))), L("ECU identification", "Identitas ECU"));

        // ---- trouble codes -----------------------------------------------------------------------------
        var dtcList = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<DtcRow>((d, _) => Columns("64,*,auto",
                new TextBlock { Text = d.Code, FontFamily = mono, FontWeight = FontWeight.Bold, Foreground = d.Confirmed ? Palette.Red : Palette.Amber, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = d.Description, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) },
                Chip(d.State))),
        };
        dtcList.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Dtcs)));
        var empty = new TextBlock { Text = L("No trouble codes stored.", "Tidak ada kode kerusakan."), Classes = { "muted" } };
        empty.Bind(Visual.IsVisibleProperty, new Binding($"{nameof(Dtcs)}.Count") { Converter = new FuncValueConverter<int, bool>(n => n == 0) });

        var read = new Button { Content = L("Read DTCs", "Baca DTC"), Classes = { "ghost" } };
        read.Click += async (_, _) => await ReadDtcsAsync();
        var clear = new Button { Content = L("Clear DTCs", "Hapus DTC"), Classes = { "ghost" }, Foreground = Palette.Red };
        clear.Click += async (_, _) => await ClearDtcsAsync();
        var fault = new ToggleButton { Content = L("Inject cooling fault", "Simulasikan gangguan pendingin") };
        fault.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(Overheating)) { Mode = BindingMode.OneWay });
        fault.IsCheckedChanged += (_, _) => InjectOverheating(fault.IsChecked == true);
        foreach (var b in new Control[] { read, clear, fault }) b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));

        var dtcCard = Card(Stack(10,
            dtcList, empty,
            new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { read, clear, fault } }), L("Trouble codes", "Kode kerusakan"));

        // ---- security ----------------------------------------------------------------------------------
        var session = Bound(new TextBlock { Classes = { "value" } }, nameof(Session), "{0}");
        var unlock = new Button { Content = L("Extended session + unlock", "Session extended + buka kunci"), Classes = { "primary" } };
        unlock.Click += async (_, _) => await UnlockAsync();
        unlock.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var code = new TextBox { Text = "GRAVICODE-01", MaxLength = 16, FontFamily = mono, MinWidth = 150 };
        var write = new Button { Content = L("Write code", "Tulis kode"), Classes = { "ghost" } };
        write.Click += async (_, _) => await WriteWorkshopCodeAsync(code.Text ?? "");
        write.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var security = Card(Stack(12,
            Columns("*,*", Stack(1, Eyebrow(L("Session", "Session")), session), Lamp(L("Security unlocked", "Security terbuka"), nameof(Unlocked))),
            unlock,
            new TextBlock { Text = L("Seed → key uses EcuSimulator.ComputeKey (demo algorithm).", "Seed → key memakai EcuSimulator.ComputeKey (algoritma demo)."), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            Row(8, code, write)), L("Session & security", "Session & security"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16,
                native,
                writes,
                cluster,
                Columns("*,14,1.2*,14,*", ident, new Border(), dtcCard, new Border(), security),
                status),
        };
    }

    private static TextBlock Bound(TextBlock block, string path, string format)
    {
        block.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = format });
        return block;
    }
}

/// <summary>
/// Analogue tachometer: a 240° arc from 0 to 8000 rpm with a red zone from 6000, ticks every 1000 rpm and an
/// amber value arc — drawn, not a bitmap, so it stays sharp at any scale.
/// </summary>
internal sealed class Tachometer : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Tachometer, double>(nameof(Value));
    private const double Max = 8000, Redline = 6000, Sweep = 240, Start = 150;
    private static readonly IPen Track = new Pen(new SolidColorBrush(Color.Parse("#2B3036")), 10, lineCap: PenLineCap.Round);
    private static readonly IPen Red = new Pen(new SolidColorBrush(Color.Parse("#D23B2F")), 10, lineCap: PenLineCap.Flat);
    private static readonly IPen Needle = new Pen(new SolidColorBrush(Color.Parse("#F2A900")), 10, lineCap: PenLineCap.Round);
    private static readonly IPen Tick = new Pen(new SolidColorBrush(Color.Parse("#8C96A0")), 2);
    private static readonly IBrush Numeral = new SolidColorBrush(Color.Parse("#8C96A0"));

    static Tachometer() => AffectsRender<Tachometer>(ValueProperty);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var r = Math.Min(Bounds.Width / 2, Bounds.Height * 0.62) - 8;
        var c = new Point(Bounds.Width / 2, r + 8);
        Point At(double rpm, double radius)
        {
            var a = (Start + Sweep * rpm / Max) * Math.PI / 180;
            return new Point(c.X + radius * Math.Cos(a), c.Y + radius * Math.Sin(a));
        }
        void Arc(IPen pen, double from, double to)
        {
            if (to <= from) return;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(At(from, r), false);
                ctx.ArcTo(At(to, r), new Size(r, r), 0, Sweep * (to - from) / Max > 180, SweepDirection.Clockwise);
            }
            context.DrawGeometry(null, pen, g);
        }
        Arc(Track, 0, Max);
        Arc(Red, Redline, Max);
        Arc(Needle, 0, Math.Clamp(Value, 1, Max));
        for (var k = 0; k <= 8; k++)
        {
            context.DrawLine(Tick, At(k * 1000, r - 9), At(k * 1000, r - 17));
            var text = new FormattedText(k.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Numeral);
            var p = At(k * 1000, r - 29);
            context.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
        }
    }
}

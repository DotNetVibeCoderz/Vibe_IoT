using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class MeteringDemo
{
    // The meter's liquid-crystal display: grey-green glass, near-black segments, a faint ghost of unlit digits.
    private static readonly IBrush LcdGlass = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#C7D1AE"), 0), new GradientStop(Color.Parse("#B4BF98"), 1) },
    };
    private static readonly IBrush LcdInk = new SolidColorBrush(Color.Parse("#1D261B"));
    private static readonly IBrush LcdGhost = new SolidColorBrush(Color.FromArgb(28, 29, 38, 27));
    private static readonly IBrush Housing = new SolidColorBrush(Color.Parse("#E9EAE5"));
    private static readonly IBrush HousingLine = new SolidColorBrush(Color.Parse("#C4C6BF"));

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        // ---- signature: the meter faceplate with its LCD -----------------------------------------------------
        TextBlock Lcd(string path, double size, FontWeight weight = FontWeight.Normal)
        {
            var t = new TextBlock { FontFamily = mono, FontSize = size, FontWeight = weight, Foreground = LcdInk };
            t.Bind(TextBlock.TextProperty, new Binding(path));
            return t;
        }

        var ghost = new TextBlock { Text = "888888.88", FontFamily = mono, FontSize = 54, FontWeight = FontWeight.Bold, Foreground = LcdGhost, HorizontalAlignment = HorizontalAlignment.Right };
        var value = Lcd(nameof(LcdValue), 54, FontWeight.Bold);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        var unit = Lcd(nameof(LcdUnit), 22, FontWeight.Bold);
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        unit.Margin = new Thickness(8, 0, 0, 10);
        unit.MinWidth = 54;
        var code = Lcd(nameof(LcdCode), 18, FontWeight.Bold);
        var peak = new TextBlock { Text = "WBP", FontFamily = mono, FontSize = 13, FontWeight = FontWeight.Bold };
        peak.Bind(TextBlock.ForegroundProperty, new Binding(nameof(LcdTariffPeak)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(p => p ? LcdInk : LcdGhost) });
        var offPeak = new TextBlock { Text = "LWBP", FontFamily = mono, FontSize = 13, FontWeight = FontWeight.Bold };
        offPeak.Bind(TextBlock.ForegroundProperty, new Binding(nameof(LcdTariffPeak)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(p => p ? LcdGhost : LcdInk) });
        var relay = new TextBlock { FontFamily = mono, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = LcdInk };
        relay.Bind(TextBlock.TextProperty, new Binding(nameof(RelayConnected)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, string>(on => on ? "⏚ ON" : "⏚ OFF") });
        var lcd = new Border
        {
            Background = LcdGlass, CornerRadius = new CornerRadius(4), Padding = new Thickness(16, 10), BorderBrush = new SolidColorBrush(Color.Parse("#7E876A")), BorderThickness = new Thickness(2),
            Child = Stack(2,
                Columns("auto,*,auto,10,auto,10,auto", code, new Border(), offPeak, new Border(), peak, new Border(), relay),
                new Grid { Children = { ghost, value } }.With(g => g.Margin = new Thickness(0, 2, 0, 0)),
                Columns("*,auto", Lcd(nameof(LcdLabel), 12.5), unit)),
        };
        var meterTime = new TextBlock { FontFamily = mono, FontSize = 12, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Bottom };
        meterTime.Bind(TextBlock.TextProperty, new Binding(nameof(MeterTime)));
        var phases = new TextBlock { FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        phases.Bind(TextBlock.TextProperty, new Binding(nameof(Phases)));
        var faceplate = new Border
        {
            Background = Housing, BorderBrush = HousingLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(22, 18),
            Child = Stack(12,
                Columns("*,auto",
                    Stack(0, new TextBlock { Text = "IOTCOM · 3×230/400 V · 5(100) A", FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 15, Foreground = Palette.Ink, LetterSpacing = 1 },
                        new TextBlock { Text = "IEC 62056 · DLMS/COSEM · SN IOT2026000017", FontFamily = mono, FontSize = 10.5, Foreground = Palette.Grey }),
                    OpticalPort()),
                lcd,
                Columns("*,auto", phases, meterTime)),
        };

        // ---- the last two days: import above, solar export below, WBP shaded ----------------------------------
        var ribbon = new ProfileRibbon(this) { Height = 236 };
        var profileCard = Card(Stack(8, ribbon,
            new TextBlock { Text = L("Each bar is one 15-minute load-profile row (1-0:99.1.0), read with selective access by date.", "Setiap bar adalah satu baris load profile 15 menit (1-0:99.1.0), dibaca dengan selective access berdasarkan tanggal."), Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap }),
            L("Two days at this address", "Dua hari di alamat ini"));

        // ---- relay and access levels ----------------------------------------------------------------------------
        var allow = new CheckBox { Content = L("Allow writes (management client, password)", "Izinkan penulisan (management client, password)") };
        allow.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AllowWrite)));
        Button Act(string text, Func<Task> act, string cls = "ghost")
        {
            var b = new Button { Content = text, Classes = { cls } };
            b.Click += async (_, _) => await act();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        var lastAction = new TextBlock { FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        lastAction.Bind(TextBlock.TextProperty, new Binding(nameof(LastAction)));
        var relayCard = Card(Stack(10, allow,
            new WrapPanel
            {
                ItemSpacing = 8, LineSpacing = 6,
                Children =
                {
                    Act(L("Disconnect supply", "Putus suplai"), () => SwitchRelayAsync(false), "primary"),
                    Act(L("Reconnect", "Sambung kembali"), () => SwitchRelayAsync(true)),
                    Act(L("Try as public client", "Coba sebagai public client"), TryAsPublicAsync),
                },
            },
            lastAction), L("Supply relay · disconnect control 0-0:96.3.10", "Relay suplai · disconnect control 0-0:96.3.10"));

        // ---- M-Bus sub-meters ---------------------------------------------------------------------------------
        var subMeters = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Columns = 3 }),
            ItemTemplate = new FuncDataTemplate<SubMeterRow>((m, _) => new Border
            {
                Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = HousingLine,
                Child = Stack(3,
                    new TextBlock { Text = m.Title, FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 16 },
                    new TextBlock { Text = m.Identity, FontFamily = mono, FontSize = 10.5, Classes = { "muted" } },
                    new ItemsControl { ItemsSource = m.Lines, ItemTemplate = new FuncDataTemplate<string>((l, _) => new TextBlock { Text = l, FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }) }),
            }),
        };
        subMeters.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(SubMeters)));
        var mbusCard = Card(subMeters, L("Basement · M-Bus segment (REQ_UD2 every 4 s)", "Ruang bawah · segmen M-Bus (REQ_UD2 tiap 4 dtk)"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16,
                Columns("1*,16,1.25*", faceplate, new Border(), profileCard),
                Columns("1*,16,1.25*", relayCard, new Border(), mbusCard),
                status),
        };

        Control OpticalPort() => new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(Color.Parse("#2B3036")), VerticalAlignment = VerticalAlignment.Top,
            Child = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.Parse("#5B1F1A")), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        }.With(b => ToolTip.SetTip(b, L("Optical port (IEC 62056-21): HDLC at 9600 baud", "Port optik (IEC 62056-21): HDLC 9600 baud")));
    }
}

internal static class ControlExtensions
{
    public static T With<T>(this T control, Action<T> configure) where T : Control
    {
        configure(control);
        return control;
    }
}

/// <summary>Two days of 15-minute slots: grid import upward, solar export downward, the WBP peak window shaded.</summary>
internal sealed class ProfileRibbon : Control
{
    private static readonly IBrush ImportBrush = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush ExportBrush = new SolidColorBrush(Color.Parse("#2E9E5B"));
    private static readonly IBrush PeakBand = new SolidColorBrush(Color.FromArgb(44, 242, 169, 0));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#7C848C"));
    private static readonly IPen Axis = new Pen(new SolidColorBrush(Color.Parse("#8A9098")), 1);
    private static readonly Typeface Face = new("avares://IoTCom.Net.Gallery/Assets/Fonts#JetBrains Mono");
    private readonly MeteringDemo _demo;

    public ProfileRibbon(MeteringDemo demo)
    {
        _demo = demo;
        demo.Profile.CollectionChanged += (_, _) => InvalidateVisual();
    }

    private static FormattedText Text(string s, double size, IBrush brush) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);

    public override void Render(DrawingContext ctx)
    {
        var slots = _demo.Profile.ToArray();
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (slots.Length < 2) return;
        const double left = 6, bottomLabels = 18;
        var max = Math.Max(0.2, slots.Max(s => Math.Max(s.Import, s.Export)));
        var axisY = (h - bottomLabels) * 0.64;
        var up = axisY - 14;
        var down = h - bottomLabels - axisY - 6;
        var barW = (w - left) / slots.Length;

        // WBP peak (17:00–22:00) bands and day labels.
        for (var i = 0; i < slots.Length; i++)
        {
            var t = slots[i].Time;
            if (t.Hour is >= 17 and < 22) ctx.FillRectangle(PeakBand, new Rect(left + (i * barW), 0, Math.Ceiling(barW), h - bottomLabels));
            if (t.Minute == 0 && t.Hour % 6 == 0)
            {
                var label = Text(t.Hour == 0 ? t.ToString("ddd", CultureInfo.CurrentUICulture) : $"{t.Hour:00}", 9.5, Label);
                ctx.DrawText(label, new Point(left + (i * barW) - (label.Width / 2), h - bottomLabels + 3));
            }
        }

        for (var i = 0; i < slots.Length; i++)
        {
            var x = left + (i * barW);
            var hi = slots[i].Import / max * up;
            var lo = slots[i].Export / max * down;
            if (hi > 0.5) ctx.FillRectangle(ImportBrush, new Rect(x, axisY - hi, Math.Max(1, barW - 1), hi));
            if (lo > 0.5) ctx.FillRectangle(ExportBrush, new Rect(x, axisY + 1, Math.Max(1, barW - 1), lo));
        }

        ctx.DrawLine(Axis, new Point(left, axisY), new Point(w, axisY));
        var importLabel = Text($"▲ {Loc.L("grid import", "impor jaringan")} · {Loc.L("max", "maks")} {max:0.00} kWh / 15 min", 10, ImportBrush);
        ctx.DrawText(importLabel, new Point(left, 0));
        ctx.DrawText(Text($"▼ {Loc.L("solar export", "ekspor surya")}", 10, ExportBrush), new Point(left, axisY + down - 6));
        var peak = Text(Loc.L("WBP 17–22", "WBP 17–22"), 10, new SolidColorBrush(Color.Parse("#B07B00")));
        ctx.DrawText(peak, new Point(w - peak.Width - 4, 0));
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class ModbusDemo
{
    protected override Control CreateView()
    {
        var motor = new ToggleSwitch { OnContent = "RUN", OffContent = "STOP", FontFamily = (Avalonia.Media.FontFamily)Application.Current!.FindResource("DisplayFont")!, FontWeight = Avalonia.Media.FontWeight.Bold };
        motor.Bind(ToggleSwitch.IsCheckedProperty, new Binding(nameof(MotorRun)) { Mode = BindingMode.OneWay });
        motor.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        motor.IsCheckedChanged += async (_, _) =>
        {
            if (motor.IsChecked != MotorRun) await ToggleMotorAsync();
        };

        var setpoint = new Slider { Minimum = 15, Maximum = 35, TickFrequency = 0.5, IsSnapToTickEnabled = true, Value = 25, MinWidth = 220 };
        setpoint.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var spText = new TextBlock { Classes = { "value" }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 64 };
        spText.Bind(TextBlock.TextProperty, new Binding(nameof(Setpoint)) { StringFormat = "{0:0.0} °C" });
        setpoint.ValueChanged += async (_, e) =>
        {
            Setpoint = e.NewValue;
            await WriteSetpointAsync(e.NewValue);
        };

        var rust = new CheckBox { Content = L("Rust engine (NativeModbusClient)", "Mesin Rust (NativeModbusClient)"), IsEnabled = RustAvailable };
        rust.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(UseRustEngine)));
        if (!RustAvailable) ToolTip.SetTip(rust, "iotcom_modbus native library not found for this platform.");

        var temperatureCard = Card(Stack(12,
            Readout(L("Temperature", "Suhu"), nameof(Temperature), "°C"),
            Sparkline(TemperatureTrend, Palette.Amber, 70),
            Row(12, Eyebrow("Setpoint"), setpoint, spText)));

        var motorCard = Card(Stack(14,
            Readout(L("Main motor", "Motor utama"), nameof(Rpm), "rpm", "{0:0}"),
            Row(16, motor, Lamp(L("Running", "Berjalan"), nameof(MotorRun)))));

        var cells = Card(Columns("*,*,*,*",
            Cell(L("Humidity", "Kelembapan"), nameof(Humidity), "{0:0.0}", "%"),
            Cell(L("Power", "Daya"), nameof(Power), "{0:0.00}", "kW"),
            Cell(L("Energy", "Energi"), nameof(Energy), "{0:0.000}", "kWh"),
            Cell(L("Produced", "Produksi"), nameof(Counter), "{0:N0}", "pcs")));

        var interlocks = Row(24, Lamp(L("Cooling pump", "Pompa pendingin"), nameof(Pump), "warn"), Lamp(L("High temperature", "Suhu tinggi"), nameof(HighTemp), "fault"));
        var requests = new TextBlock { Classes = { "muted" } };
        requests.Bind(TextBlock.TextProperty, new Binding(nameof(Requests)) { StringFormat = L("Requests served by the PLC: {0:N0}", "Request dilayani PLC: {0:N0}") });
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        var grid = Columns("1.25*,16,*", temperatureCard, new Border(), motorCard);
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, rust, grid, cells, interlocks, requests, status),
        };
    }
}

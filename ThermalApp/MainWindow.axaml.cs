using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ThermalApp;

public partial class MainWindow : Window
{
    private readonly ThermalGridRenderer _gridRenderer;
    private readonly List<IFrameProvider> _providers;
    private IFrameProvider _activeProvider;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();

        _gridRenderer = new ThermalGridRenderer(ThermalGrid, 24, 32);
        _providers = new List<IFrameProvider>
        {
            new SimulationFrameProvider(),
            new MockI2cFrameProvider()
        };

        DataSourceCombo.ItemsSource = _providers.Select(p => p.Name).ToList();
        ScaleModeCombo.ItemsSource = Enum.GetValues<ScaleMode>();

        MinTempUpDown.Value = (decimal)20;
        MaxTempUpDown.Value = (decimal)40;
        RefreshUpDown.Value = (decimal)200;

        _activeProvider = _providers[0];

        ToggleButton.Click += (_, _) => ToggleStream();
        DataSourceCombo.SelectionChanged += (_, _) => UpdateProvider();
        ScaleModeCombo.SelectionChanged += (_, _) => _gridRenderer.ScaleMode = (ScaleMode)ScaleModeCombo.SelectedItem!;
        MinTempUpDown.ValueChanged += (_, _) => UpdateScale();
        MaxTempUpDown.ValueChanged += (_, _) => UpdateScale();
        RefreshUpDown.ValueChanged += (_, _) => UpdateRefresh();

        UpdateScale();
    }

    private void UpdateProvider()
    {
        var index = DataSourceCombo.SelectedIndex;
        if (index >= 0 && index < _providers.Count)
        {
            _activeProvider = _providers[index];
        }
    }

    private void UpdateScale()
    {
        var min = (double)(MinTempUpDown.Value ?? 0);
        var max = (double)(MaxTempUpDown.Value ?? 50);
        if (max <= min)
        {
            max = min + 1;
            MaxTempUpDown.Value = (decimal)max;
        }

        _gridRenderer.SetManualScale(min, max);
    }

    private void UpdateRefresh()
    {
        if (_cts == null) return;
        RestartStream();
    }

    private void ToggleStream()
    {
        if (_cts == null)
        {
            StartStream();
        }
        else
        {
            StopStream();
        }
    }

    private void StartStream()
    {
        _cts = new CancellationTokenSource();
        ToggleButton.Content = "Stop";
        _ = Task.Run(() => StreamLoop(_cts.Token));
    }

    private void StopStream()
    {
        _cts?.Cancel();
        _cts = null;
        ToggleButton.Content = "Start";
    }

    private void RestartStream()
    {
        StopStream();
        StartStream();
    }

    private async Task StreamLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var frame = await _activeProvider.GetFrameAsync(token);
            var stats = FrameStatistics.Calculate(frame);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _gridRenderer.Render(frame);
                AvgTempText.Text = $"{stats.Average:0.0}";
                MaxTempText.Text = $"{stats.Max:0.0}";
            });

            var delay = (int)(RefreshUpDown.Value ?? 200);
            await Task.Delay(delay, token);
        }
    }
}

public interface IFrameProvider
{
    string Name { get; }
    Task<double[,]> GetFrameAsync(CancellationToken token);
}

public class SimulationFrameProvider : IFrameProvider
{
    public string Name => "Simulation";
    private readonly Random _random = new();
    private double _phase;

    public Task<double[,]> GetFrameAsync(CancellationToken token)
    {
        var data = new double[24, 32];
        _phase += 0.1;

        for (var y = 0; y < 24; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var wave = Math.Sin((x + _phase) * 0.4) + Math.Cos((y - _phase) * 0.3);
                var noise = _random.NextDouble() * 0.6 - 0.3;
                data[y, x] = 26 + wave * 8 + noise * 2;
            }
        }

        return Task.FromResult(data);
    }
}

public class MockI2cFrameProvider : IFrameProvider
{
    MLX90640 sensor { set; get; }
    public MockI2cFrameProvider()
    {
        this.sensor=new MLX90640();
    }
    public string Name => "I2C (Mock)";

    public Task<double[,]> GetFrameAsync(CancellationToken token)
    {
        // Placeholder for real MLX90640 I2C logic.
        // In Raspberry Pi, replace with I2C read using System.Device.I2c or Iot.Device.Bindings.
        var data = this.sensor?.GetFrameData(); //new double[24, 32];
        /*
        var baseTemp = 28.0;
        for (var y = 0; y < 24; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                data[y, x] = baseTemp + (x * 0.05) + (y * 0.03);
            }
        }*/
        return Task.FromResult(data);
    }
}

public enum ScaleMode
{
    Manual,
    Auto
}

public static class FrameStatistics
{
    public static (double Average, double Min, double Max) Calculate(double[,] frame)
    {
        var total = 0.0;
        var count = 0;
        var min = double.MaxValue;
        var max = double.MinValue;

        foreach (var value in frame)
        {
            total += value;
            count++;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return (total / count, min, max);
    }
}

public class ThermalGridRenderer
{
    private readonly ItemsControl _target;
    private readonly int _rows;
    private readonly int _cols;
    private readonly Border[,] _cells;

    public ScaleMode ScaleMode { get; set; } = ScaleMode.Manual;
    private double _minTemp = 20;
    private double _maxTemp = 40;

    public ThermalGridRenderer(ItemsControl target, int rows, int cols)
    {
        _target = target;
        _rows = rows;
        _cols = cols;
        _cells = new Border[_rows, _cols];

        var panel = new UniformGrid
        {
            Columns = _cols,
            Rows = _rows,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4)
        };

        for (var y = 0; y < _rows; y++)
        {
            for (var x = 0; x < _cols; x++)
            {
                var cell = new Border
                {
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Background = Brushes.Black
                };
                _cells[y, x] = cell;
                panel.Children.Add(cell);
            }
        }

        _target.ItemsSource = new[] { panel };
    }

    public void SetManualScale(double minTemp, double maxTemp)
    {
        _minTemp = minTemp;
        _maxTemp = maxTemp;
    }

    public void Render(double[,] frame)
    {
        var stats = FrameStatistics.Calculate(frame);
        var min = ScaleMode == ScaleMode.Auto ? stats.Min : _minTemp;
        var max = ScaleMode == ScaleMode.Auto ? stats.Max : _maxTemp;
        var range = Math.Max(1e-6, max - min);

        for (var y = 0; y < _rows; y++)
        {
            for (var x = 0; x < _cols; x++)
            {
                var value = frame[y, x];
                var t = Math.Clamp((value - min) / range, 0, 1);
                _cells[y, x].Background = new SolidColorBrush(ColorGradient.Evaluate(t));
            }
        }
    }
}

public static class ColorGradient
{
    public static Color Evaluate(double t)
    {
        // Green (cool) -> Yellow -> Red (hot)
        t = Math.Clamp(t, 0, 1);
        if (t < 0.5)
        {
            var local = t / 0.5;
            return Blend(Colors.Green, Colors.Yellow, local);
        }

        var localHot = (t - 0.5) / 0.5;
        return Blend(Colors.Yellow, Colors.Red, localHot);
    }

    private static Color Blend(Color a, Color b, double t)
    {
        byte Lerp(byte x, byte y) => (byte)(x + (y - x) * t);
        return Color.FromRgb(Lerp(a.R, b.R), Lerp(a.G, b.G), Lerp(a.B, b.B));
    }
}

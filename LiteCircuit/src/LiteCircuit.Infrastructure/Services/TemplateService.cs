using LiteCircuit.Core.Pcb;

namespace LiteCircuit.Infrastructure.Services;

public record PcbTemplate(string Key, string Name, string Chip, string Description, string Icon,
    Func<(Schematic Sch, Board Board)> Build);

/// <summary>Ready-to-use starter designs built around popular chips.</summary>
public class TemplateService
{
    public IReadOnlyList<PcbTemplate> All { get; } = new List<PcbTemplate>
    {
        new("blank", "Blank board", "—", "Empty 80x60mm two-layer board with default rules.", "▦",
            () => (new Schematic(), Board.CreateBlank("New board", 80, 60))),

        new("esp32-clock-weather", "Clock & Weather Station", "ESP32-WROOM-32",
            "WiFi clock with OLED display, weather sync via API, two buttons and a buzzer.", "🕐",
            BuildClockWeather),

        new("led-running-text", "LED Running Text", "ESP32-WROOM-32",
            "Scrolling text display driving LED matrix modules through 74HC595 shift registers.", "📟",
            BuildRunningText),

        new("motor-controller", "DC Motor Controller", "STM32F103C8T6",
            "Dual DC motor driver with DRV8833, screw terminals and encoder inputs.", "⚙",
            BuildMotorController),

        new("robot-arm", "Robot Arm Controller", "ESP32-WROOM-32",
            "Six-servo robot arm controller with PCA9685 PWM driver and beefy 5V input.", "🦾",
            BuildRobotArm),

        new("fm-radio", "FM Radio", "ATmega328P",
            "RDA5807 FM receiver with class-D amplifier, volume pot and headphone jack.", "📻",
            BuildRadio),

        new("mp3-player", "MP3 Player", "ESP32-WROOM-32",
            "DFPlayer Mini based MP3 player with PAM8403 amplifier and control buttons.", "🎵",
            BuildMp3),

        new("arcade-tft", "Mini Arcade (TFT)", "ESP32-WROOM-32",
            "Handheld mini game console: ILI9341 TFT, D-pad + action buttons, buzzer.", "🕹",
            BuildArcade),

        new("pet-feeder", "Automatic Pet Feeder", "ESP32-WROOM-32",
            "Scheduled feeder: servo-driven dispenser, DS3231 RTC, load-cell portion check.", "🐾",
            BuildPetFeeder),

        new("plant-monitor", "Plant Monitoring", "ESP32-C3",
            "Battery powered soil moisture + BME280 climate monitor with LiPo charging.", "🌱",
            BuildPlantMonitor),

        new("ws2812-strip", "LED Strip Controller", "ESP32-WROOM-32",
            "WS2812/NeoPixel strip driver with level-shifted data line, fused 5V input and an on-board pixel.", "🌈",
            BuildLedStrip),

        new("smart-relay", "Smart Home Relay", "ESP32-WROOM-32",
            "4-channel WiFi relay board with optocoupled transistor drivers and screw terminals.", "🏠",
            BuildSmartRelay),

        new("gps-tracker", "GPS Tracker", "ESP32-WROOM-32",
            "Battery powered GPS logger: NEO-6M receiver, LiPo charging, status LED.", "📍",
            BuildGpsTracker),

        new("air-quality", "Air Quality Monitor", "ESP32-C3",
            "Indoor air monitor with SGP30 VOC/eCO2 sensor, BME280 and OLED readout.", "🌫",
            BuildAirQuality),

        new("rfid-door", "RFID Door Lock", "ESP32-WROOM-32",
            "RC522 RFID access control driving a relay strike with buzzer feedback.", "🔐",
            BuildRfidDoor),

        new("thermostat", "Digital Thermostat", "STM32G030F6P6",
            "DS18B20 thermostat with relay output, OLED display and rotary encoder.", "🌡",
            BuildThermostat),

        new("stepper-driver", "CNC Stepper Carrier", "RP2040",
            "Dual A4988 stepper driver carrier with endstop inputs for a small CNC or plotter.", "🛠",
            BuildStepperCarrier),

        new("usb-gamepad", "USB Gamepad", "RP2040",
            "USB-C game controller: D-pad, four action buttons, rumble motor driver.", "🎮",
            BuildGamepad),
    };

    public PcbTemplate? Find(string key) => All.FirstOrDefault(t => t.Key == key);

    // ---- helpers ----------------------------------------------------------

    private static PcbComponent P(string type, string r, string val, string fp, double x, double y, params string[] nets)
        => PcbFactory.Make(type, r, val, fp, x, y, nets);

    private static SchComponent S(string type, string r, string val, double x, double y, params string[] nets)
        => PcbFactory.MakeSch(type, r, val, x, y, nets);

    private static Board NewBoard(string name, double w, double h, IEnumerable<PcbComponent> comps)
    {
        var b = Board.CreateBlank(name, w, h);
        b.Components.AddRange(comps);
        b.Nets = b.Components.SelectMany(c => c.Pads.Select(p => p.Net))
            .Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n).ToList();
        return b;
    }

    private static Schematic NewSch(IEnumerable<SchComponent> comps, string notes = "")
        => new() { Components = comps.ToList(), Notes = notes };

    /// <summary>Standard power block: input decoupling + 3.3V LDO.</summary>
    private static IEnumerable<PcbComponent> PowerBlock(double x, double y, string vin = "5V")
    {
        yield return P("regulator", "U9", "AMS1117-3.3", "SOT-223", x, y, vin, "3V3", vin, "GND");
        yield return P("capacitor", "C91", "10uF", "C0805", x - 6, y + 6, vin, "GND");
        yield return P("capacitor", "C92", "10uF", "C0805", x + 6, y + 6, "3V3", "GND");
    }

    private static IEnumerable<SchComponent> PowerBlockSch(double x, double y, string vin = "5V")
    {
        yield return S("regulator", "U9", "AMS1117-3.3", x, y, vin, "3V3", vin, "GND");
        yield return S("capacitor", "C91", "10uF", x - 15, y + 12, vin, "GND");
        yield return S("capacitor", "C92", "10uF", x + 15, y + 12, "3V3", "GND");
        yield return S("battery", "PWR1", "5", x - 32, y, vin, "GND");
    }

    private static string[] Esp32Nets(params (int pin, string net)[] pins)
    {
        var nets = new string[38];
        Array.Fill(nets, "");
        nets[0] = "GND"; nets[1] = "3V3"; nets[14] = "GND"; nets[37] = "GND";
        foreach (var (pin, net) in pins) nets[pin] = net;
        return nets;
    }

    // ---- templates --------------------------------------------------------

    private static (Schematic, Board) BuildClockWeather()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-WROOM-32", "ESP32-MODULE", 30, 26,
                Esp32Nets((4, "SDA"), (5, "SCL"), (6, "BTN1"), (7, "BTN2"), (8, "BUZZ"))),
            P("connector", "J1", "OLED SSD1306", "HDR-1x4", 62, 10, "GND", "3V3", "SCL", "SDA"),
            P("button", "SW1", "SET", "TACT-6MM", 58, 30, "BTN1", "BTN1", "GND", "GND"),
            P("button", "SW2", "MODE", "TACT-6MM", 58, 42, "BTN2", "BTN2", "GND", "GND"),
            P("resistor", "R1", "10k", "R0805", 48, 30, "BTN1", "3V3"),
            P("resistor", "R2", "10k", "R0805", 48, 42, "BTN2", "3V3"),
            P("audio", "BZ1", "buzzer", "TH-2PIN", 70, 30, "BUZZ", "GND"),
            P("connector", "J2", "USB 5V", "USB-MICRO-B", 8, 52, "5V", "USBD-", "USBD+", "", "GND"),
        };
        comps.AddRange(PowerBlock(30, 52));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32", 60, 30, "GND", "3V3", "SDA", "SCL", "BTN1", "BTN2", "BUZZ"),
            S("display", "J1", "OLED I2C", 110, 15, "GND", "3V3", "SCL", "SDA"),
            S("button", "SW1", "SET", 110, 40, "BTN1", "GND"),
            S("button", "SW2", "MODE", 110, 55, "BTN2", "GND"),
            S("resistor", "R1", "10k", 90, 40, "BTN1", "3V3"),
            S("resistor", "R2", "10k", 90, 55, "BTN2", "3V3"),
            S("audio", "BZ1", "buzzer", 110, 70, "BUZZ", "GND"),
        }.Concat(PowerBlockSch(30, 70)),
            "NTP clock + OpenWeather sync. OLED on I2C (SDA/SCL), buttons active-low with pull-ups.");
        return (sch, NewBoard("Clock & Weather", 80, 62, comps));
    }

    private static (Schematic, Board) BuildRunningText()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-WROOM-32", "ESP32-MODULE", 26, 26,
                Esp32Nets((4, "DATA"), (5, "CLK"), (6, "LATCH"))),
            P("ic", "U2", "74HC595", "SOIC-16", 56, 14,
                "Q1", "Q2", "Q3", "Q4", "Q5", "Q6", "Q7", "GND", "SER2", "LATCH", "CLK", "", "", "DATA", "Q0", "3V3"),
            P("ic", "U3", "74HC595", "SOIC-16", 56, 34,
                "R1", "R2", "R3", "R4", "R5", "R6", "R7", "GND", "", "LATCH", "CLK", "", "", "SER2", "R0", "3V3"),
            P("connector", "J1", "LED matrix", "HDR-1x8", 78, 14, "Q0", "Q1", "Q2", "Q3", "Q4", "Q5", "Q6", "Q7"),
            P("connector", "J2", "LED matrix rows", "HDR-1x8", 78, 34, "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7"),
            P("capacitor", "C1", "100nF", "C0805", 56, 24, "3V3", "GND"),
            P("connector", "J3", "USB 5V", "USB-MICRO-B", 8, 52, "5V", "", "", "", "GND"),
        };
        comps.AddRange(PowerBlock(40, 52));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32", 50, 30, "GND", "3V3", "DATA", "CLK", "LATCH"),
            S("ic", "U2", "74HC595", 100, 20, "DATA", "CLK", "LATCH", "SER2", "GND", "3V3"),
            S("ic", "U3", "74HC595", 100, 50, "SER2", "CLK", "LATCH", "", "GND", "3V3"),
            S("capacitor", "C1", "100nF", 80, 65, "3V3", "GND"),
        }.Concat(PowerBlockSch(25, 70)),
            "Chained shift registers drive columns/rows of an 8x8 LED matrix. Scroll text arrives over WiFi.");
        return (sch, NewBoard("LED Running Text", 92, 62, comps));
    }

    private static (Schematic, Board) BuildMotorController()
    {
        var comps = new List<PcbComponent>
        {
            P("ic", "U1", "STM32F103C8T6", "LQFP-48", 40, 28),
            P("ic", "U2", "DRV8833", "TSSOP-16", 66, 18,
                "AIN1", "AIN2", "AOUT1", "AOUT2", "BOUT2", "BOUT1", "BIN2", "BIN1",
                "GND", "VM", "", "", "", "", "", "GND"),
            P("connector", "J1", "Motor A", "TERM-2", 84, 10, "AOUT1", "AOUT2"),
            P("connector", "J2", "Motor B", "TERM-2", 84, 28, "BOUT1", "BOUT2"),
            P("connector", "J3", "Power in", "TERM-2", 84, 46, "VM", "GND"),
            P("connector", "J4", "Encoders", "HDR-1x4", 12, 10, "ENCA1", "ENCA2", "ENCB1", "ENCB2"),
            P("capacitor", "C1", "100uF", "CP-RADIAL-5MM", 70, 40, "VM", "GND"),
            P("capacitor", "C2", "100nF", "C0805", 30, 40, "3V3", "GND"),
            P("crystal", "Y1", "8MHz", "HC49-SMD", 24, 18, "OSC1", "OSC2"),
            P("connector", "J5", "SWD", "HDR-1x4", 12, 46, "3V3", "SWDIO", "SWCLK", "GND"),
        };
        comps.AddRange(PowerBlock(50, 50, "VM"));
        var stmNets = new string[48]; Array.Fill(stmNets, "");
        stmNets[0] = "VM"; stmNets[7] = "GND"; stmNets[8] = "3V3";
        comps[0].Pads[10].Net = "AIN1"; comps[0].Pads[11].Net = "AIN2";
        comps[0].Pads[12].Net = "BIN1"; comps[0].Pads[13].Net = "BIN2";
        comps[0].Pads[20].Net = "ENCA1"; comps[0].Pads[21].Net = "ENCA2";
        comps[0].Pads[22].Net = "ENCB1"; comps[0].Pads[23].Net = "ENCB2";
        comps[0].Pads[46].Net = "3V3"; comps[0].Pads[47].Net = "GND";
        var sch = NewSch(new[]
        {
            S("ic", "U1", "STM32F103", 50, 30, "3V3", "GND", "AIN1", "AIN2", "BIN1", "BIN2"),
            S("ic", "U2", "DRV8833", 100, 25, "AIN1", "AIN2", "AOUT1", "AOUT2", "BIN1", "BIN2", "BOUT1", "BOUT2", "VM", "GND"),
            S("connector", "J1", "Motor A", 135, 15, "AOUT1", "AOUT2"),
            S("connector", "J2", "Motor B", 135, 35, "BOUT1", "BOUT2"),
            S("capacitor", "C1", "100uF", 100, 55, "VM", "GND"),
            S("crystal", "Y1", "8MHz", 25, 45, "OSC1", "OSC2"),
        }.Concat(PowerBlockSch(25, 70, "VM")),
            "PWM on AIN/BIN pins drives the DRV8833 dual H-bridge; encoder feedback on TIM channels.");
        return (sch, NewBoard("Motor Controller", 96, 60, comps));
    }

    private static (Schematic, Board) BuildRobotArm()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-WROOM-32", "ESP32-MODULE", 26, 28,
                Esp32Nets((4, "SDA"), (5, "SCL"))),
            P("ic", "U2", "PCA9685", "TSSOP-28", 58, 16),
            P("connector", "J3", "Power 5V", "TERM-2", 88, 52, "5V", "GND"),
            P("capacitor", "C1", "100uF", "CP-RADIAL-5MM", 76, 52, "5V", "GND"),
            P("capacitor", "C2", "100nF", "C0805", 58, 28, "3V3", "GND"),
        };
        for (var i = 0; i < 6; i++)
            comps.Add(P("connector", $"J{10 + i}", $"Servo {i + 1}", "HDR-1x3", 88, 8 + i * 7, $"PWM{i}", "5V", "GND"));
        var u2 = comps[1];
        u2.Pads[0].Net = "SDA"; u2.Pads[1].Net = "SCL"; u2.Pads[13].Net = "GND"; u2.Pads[27].Net = "3V3";
        for (var i = 0; i < 6; i++) u2.Pads[5 + i].Net = $"PWM{i}";
        comps.AddRange(PowerBlock(44, 52));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32", 40, 30, "GND", "3V3", "SDA", "SCL"),
            S("ic", "U2", "PCA9685", 90, 30, "SDA", "SCL", "PWM0", "PWM1", "PWM2", "PWM3", "PWM4", "PWM5", "3V3", "GND"),
            S("capacitor", "C1", "100uF", 120, 60, "5V", "GND"),
        }.Concat(Enumerable.Range(0, 6).Select(i => S("connector", $"J{10 + i}", $"Servo{i + 1}", 130, 10 + i * 10, $"PWM{i}", "5V", "GND")))
         .Concat(PowerBlockSch(20, 70)),
            "PCA9685 offloads 16-channel servo PWM over I2C. Give servos a thick 5V rail.");
        return (sch, NewBoard("Robot Arm Controller", 100, 60, comps));
    }

    private static (Schematic, Board) BuildRadio()
    {
        var comps = new List<PcbComponent>
        {
            P("ic", "U1", "ATmega328P", "TQFP-32", 30, 26),
            P("module", "U2", "RDA5807M", "MODULE-RDA5807", 62, 12, "3V3", "GND", "SDA", "SCL", "", "", "AUDL", "AUDR", "ANT", ""),
            P("ic", "U3", "PAM8403", "SOIC-16", 62, 34),
            P("connector", "J1", "Speaker L", "TERM-2", 86, 26, "SPKL+", "SPKL-"),
            P("connector", "J2", "Speaker R", "TERM-2", 86, 44, "SPKR+", "SPKR-"),
            P("connector", "J3", "Antenna", "TH-2PIN", 86, 8, "ANT", "GND"),
            P("resistor", "RV1", "10k pot", "TH-4PIN", 12, 44, "3V3", "VOL", "GND", ""),
            P("capacitor", "C1", "100nF", "C0805", 44, 26, "3V3", "GND"),
            P("crystal", "Y1", "8MHz", "HC49-SMD", 14, 16, "OSC1", "OSC2"),
        };
        var u1 = comps[0];
        u1.Pads[3].Net = "3V3"; u1.Pads[4].Net = "GND"; u1.Pads[26].Net = "SDA"; u1.Pads[27].Net = "SCL"; u1.Pads[22].Net = "VOL";
        var u3 = comps[2];
        u3.Pads[0].Net = "AUDL"; u3.Pads[1].Net = "AUDR"; u3.Pads[4].Net = "SPKL+"; u3.Pads[5].Net = "SPKL-";
        u3.Pads[10].Net = "SPKR+"; u3.Pads[11].Net = "SPKR-"; u3.Pads[7].Net = "GND"; u3.Pads[15].Net = "5V";
        comps.AddRange(PowerBlock(30, 50));
        var sch = NewSch(new[]
        {
            S("ic", "U1", "ATmega328P", 40, 30, "3V3", "GND", "SDA", "SCL", "VOL"),
            S("module", "U2", "RDA5807", 95, 15, "3V3", "GND", "SDA", "SCL", "AUDL", "AUDR", "ANT"),
            S("ic", "U3", "PAM8403", 95, 45, "AUDL", "AUDR", "SPKL+", "SPKL-", "SPKR+", "SPKR-", "5V", "GND"),
            S("resistor", "RV1", "10k pot", 15, 45, "3V3", "VOL", "GND"),
            S("crystal", "Y1", "8MHz", 15, 20, "OSC1", "OSC2"),
        }.Concat(PowerBlockSch(40, 70)),
            "I2C-tuned FM receiver feeding a 3W class-D stereo amplifier. Volume pot on ADC.");
        return (sch, NewBoard("FM Radio", 94, 58, comps));
    }

    private static (Schematic, Board) BuildMp3()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-WROOM-32", "ESP32-MODULE", 26, 26,
                Esp32Nets((4, "TX2"), (5, "RX2"), (6, "BTN_PLAY"), (7, "BTN_NEXT"))),
            P("audio", "U2", "DFPlayer Mini", "MODULE-DFPLAYER", 60, 22,
                "5V", "RX2", "TX2", "", "", "", "SPK1", "GND", "", "", "", "", "", "", "SPK2", ""),
            P("ic", "U3", "PAM8403", "SOIC-16", 60, 44),
            P("connector", "J1", "Speaker", "TERM-2", 88, 44, "SPK+", "SPK-"),
            P("button", "SW1", "PLAY", "TACT-6MM", 10, 12, "BTN_PLAY", "BTN_PLAY", "GND", "GND"),
            P("button", "SW2", "NEXT", "TACT-6MM", 10, 26, "BTN_NEXT", "BTN_NEXT", "GND", "GND"),
            P("connector", "J2", "USB 5V", "USB-MICRO-B", 8, 52, "5V", "", "", "", "GND"),
        };
        var u3 = comps[2];
        u3.Pads[0].Net = "SPK1"; u3.Pads[1].Net = "SPK2";
        u3.Pads[4].Net = "SPK+"; u3.Pads[5].Net = "SPK-"; u3.Pads[7].Net = "GND"; u3.Pads[15].Net = "5V";
        comps.AddRange(PowerBlock(40, 52));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32", 40, 30, "GND", "3V3", "TX2", "RX2", "BTN_PLAY", "BTN_NEXT"),
            S("audio", "U2", "DFPlayer", 95, 20, "5V", "RX2", "TX2", "SPK1", "SPK2", "GND"),
            S("ic", "U3", "PAM8403", 95, 50, "SPK1", "SPK2", "SPK+", "SPK-", "5V", "GND"),
            S("button", "SW1", "PLAY", 15, 15, "BTN_PLAY", "GND"),
            S("button", "SW2", "NEXT", 15, 30, "BTN_NEXT", "GND"),
        }.Concat(PowerBlockSch(40, 70)),
            "DFPlayer streams MP3 from microSD over UART; PAM8403 drives the speaker.");
        return (sch, NewBoard("MP3 Player", 96, 60, comps));
    }

    private static (Schematic, Board) BuildArcade()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-WROOM-32", "ESP32-MODULE", 28, 40,
                Esp32Nets((4, "TFT_CS"), (5, "TFT_DC"), (6, "MOSI"), (7, "SCK"), (8, "BUZZ"),
                          (9, "BTN_A"), (10, "BTN_B"), (16, "UP"), (17, "DOWN"), (18, "LEFT"), (19, "RIGHT"))),
            P("display", "J1", "TFT ILI9341", "HDR-1x14", 66, 12,
                "3V3", "GND", "TFT_CS", "RST", "TFT_DC", "MOSI", "SCK", "3V3", "MISO", "", "", "", "", ""),
            P("button", "SW1", "UP", "TACT-6MM", 12, 14, "UP", "UP", "GND", "GND"),
            P("button", "SW2", "DOWN", "TACT-6MM", 12, 38, "DOWN", "DOWN", "GND", "GND"),
            P("button", "SW3", "LEFT", "TACT-6MM", 4, 26, "LEFT", "LEFT", "GND", "GND"),
            P("button", "SW4", "RIGHT", "TACT-6MM", 20, 26, "RIGHT", "RIGHT", "GND", "GND"),
            P("button", "SW5", "A", "TACT-6MM", 88, 40, "BTN_A", "BTN_A", "GND", "GND"),
            P("button", "SW6", "B", "TACT-6MM", 96, 28, "BTN_B", "BTN_B", "GND", "GND"),
            P("audio", "BZ1", "buzzer", "TH-2PIN", 88, 54, "BUZZ", "GND"),
            P("connector", "J2", "USB 5V", "USB-MICRO-B", 8, 56, "5V", "", "", "", "GND"),
        };
        comps.AddRange(PowerBlock(50, 56));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32", 50, 35, "GND", "3V3", "TFT_CS", "TFT_DC", "MOSI", "SCK", "BUZZ", "BTN_A", "BTN_B", "UP", "DOWN", "LEFT", "RIGHT"),
            S("display", "J1", "ILI9341 SPI", 110, 20, "3V3", "GND", "TFT_CS", "RST", "TFT_DC", "MOSI", "SCK"),
            S("audio", "BZ1", "buzzer", 110, 55, "BUZZ", "GND"),
        }.Concat(new[] { ("SW1", "UP"), ("SW2", "DOWN"), ("SW3", "LEFT"), ("SW4", "RIGHT"), ("SW5", "BTN_A"), ("SW6", "BTN_B") }
            .Select((sw, i) => S("button", sw.Item1, sw.Item2, 15, 10 + i * 10, sw.Item2, "GND")))
         .Concat(PowerBlockSch(50, 70)),
            "SPI TFT at 40MHz, D-pad + A/B buttons on GPIO with internal pull-ups, buzzer for SFX.");
        return (sch, NewBoard("Mini Arcade", 104, 66, comps));
    }

    private static (Schematic, Board) BuildPetFeeder()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-WROOM-32", "ESP32-MODULE", 28, 28,
                Esp32Nets((4, "SDA"), (5, "SCL"), (6, "SERVO"), (7, "HX_DT"), (8, "HX_SCK"), (9, "BTN"))),
            P("ic", "U2", "DS3231", "SOIC-16", 60, 12),
            P("ic", "U3", "HX711", "SOIC-16", 60, 32),
            P("connector", "J1", "Servo", "HDR-1x3", 88, 12, "SERVO", "5V", "GND"),
            P("connector", "J2", "Load cell", "HDR-1x4", 88, 32, "E+", "E-", "A+", "A-"),
            P("button", "SW1", "FEED", "TACT-6MM", 10, 48, "BTN", "BTN", "GND", "GND"),
            P("capacitor", "C1", "100nF", "C0805", 46, 12, "3V3", "GND"),
            P("connector", "J3", "USB 5V", "USB-MICRO-B", 8, 12, "5V", "", "", "", "GND"),
        };
        var u2 = comps[1];
        u2.Pads[0].Net = "3V3"; u2.Pads[7].Net = "GND"; u2.Pads[14].Net = "SDA"; u2.Pads[15].Net = "SCL";
        var u3 = comps[2];
        u3.Pads[0].Net = "3V3"; u3.Pads[7].Net = "GND"; u3.Pads[10].Net = "HX_DT"; u3.Pads[11].Net = "HX_SCK";
        u3.Pads[2].Net = "A+"; u3.Pads[3].Net = "A-"; u3.Pads[4].Net = "E+"; u3.Pads[5].Net = "E-";
        comps.AddRange(PowerBlock(46, 48));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32", 45, 30, "GND", "3V3", "SDA", "SCL", "SERVO", "HX_DT", "HX_SCK", "BTN"),
            S("ic", "U2", "DS3231 RTC", 100, 15, "3V3", "GND", "SDA", "SCL"),
            S("ic", "U3", "HX711", 100, 40, "3V3", "GND", "HX_DT", "HX_SCK", "A+", "A-", "E+", "E-"),
            S("connector", "J1", "Servo", 135, 15, "SERVO", "5V", "GND"),
            S("button", "SW1", "FEED", 15, 50, "BTN", "GND"),
        }.Concat(PowerBlockSch(45, 70)),
            "RTC-scheduled feeding; HX711 load cell verifies the dispensed portion weight.");
        return (sch, NewBoard("Pet Feeder", 96, 58, comps));
    }

    private static (Schematic, Board) BuildPlantMonitor()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-C3", "ESP32-MODULE", 28, 26,
                Esp32Nets((4, "SDA"), (5, "SCL"), (6, "SOIL"), (7, "VBAT_SENSE"))),
            P("sensor", "U2", "BME280", "LGA-8", 58, 12, "3V3", "GND", "SDA", "SCL", "", "", "", "GND"),
            P("connector", "J1", "Soil probe", "MODULE-3PIN", 82, 12, "3V3", "GND", "SOIL"),
            P("regulator", "U3", "TP4056", "MODULE-TP4056", 58, 44, "5V", "GND", "VBAT", "GND", "", ""),
            P("connector", "J2", "LiPo", "TH-2PIN", 82, 44, "VBAT", "GND"),
            P("resistor", "R1", "100k", "R0805", 44, 34, "VBAT", "VBAT_SENSE"),
            P("resistor", "R2", "100k", "R0805", 44, 40, "VBAT_SENSE", "GND"),
            P("connector", "J3", "USB-C", "USB-C-16P", 8, 50, "GND", "", "", "", "5V", "", "", "", "", "5V", "", "", "", "", "", "GND"),
        };
        comps.AddRange(PowerBlock(28, 50, "VBAT"));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32-C3", 45, 30, "GND", "3V3", "SDA", "SCL", "SOIL", "VBAT_SENSE"),
            S("sensor", "U2", "BME280", 100, 15, "3V3", "GND", "SDA", "SCL"),
            S("connector", "J1", "Soil probe", 135, 15, "3V3", "GND", "SOIL"),
            S("regulator", "U3", "TP4056", 100, 50, "5V", "GND", "VBAT"),
            S("resistor", "R1", "100k", 70, 60, "VBAT", "VBAT_SENSE"),
            S("resistor", "R2", "100k", 70, 72, "VBAT_SENSE", "GND"),
        }.Concat(PowerBlockSch(30, 80, "VBAT")),
            "Deep-sleep battery monitor: soil moisture ADC + BME280 climate, TP4056 LiPo charging, VBAT divider.");
        return (sch, NewBoard("Plant Monitor", 92, 60, comps));
    }

    private static (Schematic, Board) BuildLedStrip()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-WROOM-32", "ESP32-MODULE", 26, 26, Esp32Nets((4, "DATA_3V3"))),
            P("transistor", "Q1", "2N7002", "SOT-23", 50, 12, "DATA_3V3", "DATA_5V", "GND"),
            P("resistor", "R1", "470", "R0805", 58, 12, "DATA_5V", "DOUT"),
            P("led", "D1", "WS2812B", "SMD-5050", 68, 12, "5V", "DOUT", "GND", "DSTRIP"),
            P("connector", "J1", "Strip out", "TERM-3", 86, 12, "5V", "DSTRIP", "GND"),
            P("connector", "J2", "Power 5V", "TERM-2", 86, 44, "5V_IN", "GND"),
            P("diode", "F1", "SS34 fuse/rev", "SMA", 70, 44, "5V_IN", "5V"),
            P("capacitor", "C1", "100uF", "CP-RADIAL-5MM", 58, 44, "5V", "GND"),
        };
        comps.AddRange(PowerBlock(38, 48));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32", 40, 30, "GND", "3V3", "DATA_3V3"),
            S("transistor", "Q1", "2N7002", 85, 15, "DATA_3V3", "DATA_5V", "GND"),
            S("resistor", "R1", "470", 105, 15, "DATA_5V", "DOUT"),
            S("led", "D1", "WS2812B", 125, 15, "5V", "DOUT", "GND", "DSTRIP"),
            S("connector", "J1", "Strip", 145, 15, "5V", "DSTRIP", "GND"),
            S("capacitor", "C1", "100uF", 105, 45, "5V", "GND"),
        }.Concat(PowerBlockSch(30, 70)),
            "Level-shift the 3V3 data line to 5V, series 470Ω into the first pixel, bulk capacitance at the strip connector.");
        return (sch, NewBoard("LED Strip Controller", 96, 58, comps));
    }

    private static (Schematic, Board) BuildSmartRelay()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-WROOM-32", "ESP32-MODULE", 24, 32,
                Esp32Nets((4, "CH1"), (5, "CH2"), (6, "CH3"), (7, "CH4"))),
        };
        for (var i = 0; i < 4; i++)
        {
            var ch = $"CH{i + 1}";
            var y = 10.0 + i * 15;
            comps.Add(P("ic", $"OK{i + 1}", "PC817", "SOP-4", 44, y, ch, "GND", $"OPTO{i + 1}", "5V"));
            comps.Add(P("transistor", $"Q{i + 1}", "S8050", "SOT-23", 54, y, $"OPTO{i + 1}", $"COIL{i + 1}", "GND"));
            comps.Add(P("ic", $"K{i + 1}", "SRD-05VDC", "RELAY-SRD", 72, y, "5V", $"COIL{i + 1}", $"COM{i + 1}", $"NO{i + 1}", $"NC{i + 1}"));
            comps.Add(P("connector", $"J{i + 1}", $"Load {i + 1}", "TERM-3", 92, y, $"COM{i + 1}", $"NO{i + 1}", $"NC{i + 1}"));
        }
        comps.Add(P("connector", "J9", "Power 5V", "TERM-2", 10, 62, "5V", "GND"));
        comps.AddRange(PowerBlock(30, 62));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32", 30, 40, "GND", "3V3", "CH1", "CH2", "CH3", "CH4"),
        }.Concat(Enumerable.Range(1, 4).SelectMany(i => new[]
        {
            S("ic", $"OK{i}", "PC817", 75, 12 + i * 16, $"CH{i}", "GND", $"OPTO{i}", "5V"),
            S("transistor", $"Q{i}", "S8050", 100, 12 + i * 16, $"OPTO{i}", $"COIL{i}", "GND"),
            S("ic", $"K{i}", "Relay", 125, 12 + i * 16, "5V", $"COIL{i}", $"COM{i}", $"NO{i}", $"NC{i}"),
        })).Concat(PowerBlockSch(20, 95)),
            "Each channel: GPIO → PC817 optocoupler → NPN driver → SRD relay. Keep mains-side terminals clear of logic.");
        return (sch, NewBoard("Smart Home Relay", 104, 72, comps));
    }

    private static (Schematic, Board) BuildGpsTracker()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-WROOM-32", "ESP32-MODULE", 26, 26,
                Esp32Nets((4, "GPS_TX"), (5, "GPS_RX"), (6, "LED_FIX"))),
            P("module", "U2", "NEO-6M GPS", "HDR-1x4", 62, 10, "3V3", "GND", "GPS_TX", "GPS_RX"),
            P("regulator", "U3", "TP4056", "MODULE-TP4056", 62, 44, "5V", "GND", "VBAT", "GND", "", ""),
            P("connector", "J1", "LiPo", "TH-2PIN", 84, 44, "VBAT", "GND"),
            P("led", "D1", "green", "LED0805", 46, 12, "LED_FIX", "LED_K"),
            P("resistor", "R1", "220", "R0805", 46, 18, "LED_K", "GND"),
            P("connector", "J2", "USB-C", "USB-C-16P", 8, 52, "GND", "", "", "", "5V", "", "", "", "", "5V", "", "", "", "", "", "GND"),
        };
        comps.AddRange(PowerBlock(30, 52, "VBAT"));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32", 40, 30, "GND", "3V3", "GPS_TX", "GPS_RX", "LED_FIX"),
            S("module", "U2", "NEO-6M", 100, 15, "3V3", "GND", "GPS_TX", "GPS_RX"),
            S("regulator", "U3", "TP4056", 100, 50, "5V", "GND", "VBAT"),
            S("led", "D1", "fix LED", 60, 60, "LED_FIX", "LED_K"),
            S("resistor", "R1", "220", 80, 60, "LED_K", "GND"),
        }.Concat(PowerBlockSch(30, 80, "VBAT")),
            "NEO-6M on UART2; fix LED blinks per GPS PPS. Log to flash, sync over WiFi when home.");
        return (sch, NewBoard("GPS Tracker", 94, 60, comps));
    }

    private static (Schematic, Board) BuildAirQuality()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-C3", "ESP32-MODULE", 26, 26, Esp32Nets((4, "SDA"), (5, "SCL"))),
            P("sensor", "U2", "SGP30", "LGA-8", 56, 10, "3V3", "GND", "SDA", "SCL", "", "", "", "GND"),
            P("sensor", "U3", "BME280", "LGA-8", 68, 10, "3V3", "GND", "SDA", "SCL", "", "", "", "GND"),
            P("connector", "J1", "OLED SSD1306", "HDR-1x4", 84, 10, "GND", "3V3", "SCL", "SDA"),
            P("resistor", "R1", "4.7k", "R0805", 46, 16, "SDA", "3V3"),
            P("resistor", "R2", "4.7k", "R0805", 46, 22, "SCL", "3V3"),
            P("connector", "J2", "USB-C", "USB-C-16P", 8, 52, "GND", "", "", "", "5V", "", "", "", "", "5V", "", "", "", "", "", "GND"),
        };
        comps.AddRange(PowerBlock(40, 52));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32-C3", 40, 30, "GND", "3V3", "SDA", "SCL"),
            S("sensor", "U2", "SGP30", 95, 12, "3V3", "GND", "SDA", "SCL"),
            S("sensor", "U3", "BME280", 120, 12, "3V3", "GND", "SDA", "SCL"),
            S("display", "J1", "OLED", 145, 12, "GND", "3V3", "SCL", "SDA"),
            S("resistor", "R1", "4.7k", 70, 40, "SDA", "3V3"),
            S("resistor", "R2", "4.7k", 90, 40, "SCL", "3V3"),
        }.Concat(PowerBlockSch(40, 70)),
            "SGP30 (VOC/eCO2) and BME280 share the I2C bus with the OLED; 4.7k pull-ups on SDA/SCL.");
        return (sch, NewBoard("Air Quality Monitor", 96, 58, comps));
    }

    private static (Schematic, Board) BuildRfidDoor()
    {
        var comps = new List<PcbComponent>
        {
            P("module", "U1", "ESP32-WROOM-32", "ESP32-MODULE", 26, 28,
                Esp32Nets((4, "MOSI"), (5, "MISO"), (6, "SCK"), (7, "SS"), (8, "RST_RC"), (9, "RELAY"), (10, "BUZZ"))),
            P("module", "U2", "RC522 RFID", "HDR-1x8", 60, 10, "3V3", "RST_RC", "GND", "", "MISO", "MOSI", "SCK", "SS"),
            P("transistor", "Q1", "S8050", "SOT-23", 52, 40, "RELAY", "COIL", "GND"),
            P("ic", "K1", "SRD-05VDC", "RELAY-SRD", 70, 42, "5V", "COIL", "COM", "NO", "NC"),
            P("connector", "J1", "Strike", "TERM-2", 92, 42, "COM", "NO"),
            P("audio", "BZ1", "buzzer", "TH-2PIN", 44, 52, "BUZZ", "GND"),
            P("connector", "J2", "Power 5V", "TERM-2", 8, 52, "5V", "GND"),
        };
        comps.AddRange(PowerBlock(26, 52));
        var sch = NewSch(new[]
        {
            S("module", "U1", "ESP32", 40, 32, "GND", "3V3", "MOSI", "MISO", "SCK", "SS", "RST_RC", "RELAY", "BUZZ"),
            S("module", "U2", "RC522", 100, 15, "3V3", "RST_RC", "GND", "MISO", "MOSI", "SCK", "SS"),
            S("transistor", "Q1", "S8050", 95, 50, "RELAY", "COIL", "GND"),
            S("ic", "K1", "Relay", 120, 50, "5V", "COIL", "COM", "NO"),
            S("audio", "BZ1", "buzzer", 70, 65, "BUZZ", "GND"),
        }.Concat(PowerBlockSch(30, 80)),
            "RC522 on SPI; valid card → relay pulses the door strike, buzzer confirms. Flyback diode is inside the relay module footprint.");
        return (sch, NewBoard("RFID Door Lock", 100, 60, comps));
    }

    private static (Schematic, Board) BuildThermostat()
    {
        var comps = new List<PcbComponent>
        {
            P("ic", "U1", "STM32G030F6P6", "TSSOP-20", 30, 22),
            P("connector", "J1", "DS18B20", "TH-3PIN", 8, 10, "3V3", "TEMP", "GND"),
            P("resistor", "R1", "4.7k", "R0805", 18, 10, "TEMP", "3V3"),
            P("connector", "J2", "OLED SSD1306", "HDR-1x4", 56, 10, "GND", "3V3", "SCL", "SDA"),
            P("button", "ENC1", "EC11 encoder", "EC11", 56, 34, "ENC_A", "ENC_B", "GND", "ENC_SW", "GND"),
            P("transistor", "Q1", "S8050", "SOT-23", 20, 40, "RELAY", "COIL", "GND"),
            P("ic", "K1", "SRD-05VDC", "RELAY-SRD", 38, 46, "5V", "COIL", "COM", "NO", "NC"),
            P("connector", "J3", "Heater", "TERM-2", 78, 46, "COM", "NO"),
            P("capacitor", "C1", "100nF", "C0805", 40, 22, "3V3", "GND"),
        };
        var u1 = comps[0];
        u1.Pads[0].Net = "3V3"; u1.Pads[9].Net = "GND";
        u1.Pads[2].Net = "TEMP"; u1.Pads[3].Net = "SDA"; u1.Pads[4].Net = "SCL";
        u1.Pads[5].Net = "ENC_A"; u1.Pads[6].Net = "ENC_B"; u1.Pads[7].Net = "ENC_SW"; u1.Pads[8].Net = "RELAY";
        comps.AddRange(PowerBlock(64, 24));
        var sch = NewSch(new[]
        {
            S("ic", "U1", "STM32G030", 45, 30, "3V3", "GND", "TEMP", "SDA", "SCL", "ENC_A", "ENC_B", "ENC_SW", "RELAY"),
            S("sensor", "J1", "DS18B20", 15, 12, "3V3", "TEMP", "GND"),
            S("resistor", "R1", "4.7k", 30, 12, "TEMP", "3V3"),
            S("display", "J2", "OLED", 100, 12, "GND", "3V3", "SCL", "SDA"),
            S("button", "ENC1", "EC11", 100, 40, "ENC_A", "ENC_B", "GND", "ENC_SW"),
            S("transistor", "Q1", "S8050", 70, 60, "RELAY", "COIL", "GND"),
            S("ic", "K1", "Relay", 95, 60, "5V", "COIL", "COM", "NO"),
        }.Concat(PowerBlockSch(20, 80)),
            "1-Wire DS18B20 with 4.7k pull-up, hysteresis control on the relay, setpoint via rotary encoder.");
        return (sch, NewBoard("Digital Thermostat", 90, 60, comps));
    }

    private static (Schematic, Board) BuildStepperCarrier()
    {
        var comps = new List<PcbComponent>
        {
            P("ic", "U1", "RP2040", "QFN-56", 24, 30),
            P("module", "U2", "A4988 X", "MODULE-A4988", 56, 16),
            P("module", "U3", "A4988 Y", "MODULE-A4988", 56, 44),
            P("connector", "J1", "Motor X", "HDR-1x4", 82, 16, "X1A", "X1B", "X2A", "X2B"),
            P("connector", "J2", "Motor Y", "HDR-1x4", 82, 44, "Y1A", "Y1B", "Y2A", "Y2B"),
            P("connector", "J3", "Endstops", "HDR-1x4", 8, 10, "ENDX", "ENDY", "GND", "3V3"),
            P("connector", "J4", "VMOT 12V", "TERM-2", 8, 50, "VMOT", "GND"),
            P("capacitor", "C1", "100uF", "CP-RADIAL-5MM", 18, 50, "VMOT", "GND"),
            P("connector", "J5", "USB-C", "USB-C-16P", 8, 30, "GND", "", "", "", "5V", "", "", "", "", "5V", "", "", "", "", "", "GND"),
            P("crystal", "Y1", "12MHz", "FC-135", 36, 20, "OSC1", "OSC2"),
        };
        var u2 = comps[1];
        u2.Pads[0].Net = "X_EN"; u2.Pads[6].Net = "X_STEP"; u2.Pads[7].Net = "X_DIR";
        u2.Pads[8].Net = "GND"; u2.Pads[9].Net = "VMOT";
        u2.Pads[10].Net = "X2B"; u2.Pads[11].Net = "X2A"; u2.Pads[12].Net = "X1A"; u2.Pads[13].Net = "X1B";
        var u3 = comps[2];
        u3.Pads[0].Net = "Y_EN"; u3.Pads[6].Net = "Y_STEP"; u3.Pads[7].Net = "Y_DIR";
        u3.Pads[8].Net = "GND"; u3.Pads[9].Net = "VMOT";
        u3.Pads[10].Net = "Y2B"; u3.Pads[11].Net = "Y2A"; u3.Pads[12].Net = "Y1A"; u3.Pads[13].Net = "Y1B";
        var u1 = comps[0];
        u1.Pads[0].Net = "3V3"; u1.Pads[28].Net = "GND";
        u1.Pads[4].Net = "X_STEP"; u1.Pads[5].Net = "X_DIR"; u1.Pads[6].Net = "X_EN";
        u1.Pads[8].Net = "Y_STEP"; u1.Pads[9].Net = "Y_DIR"; u1.Pads[10].Net = "Y_EN";
        u1.Pads[12].Net = "ENDX"; u1.Pads[13].Net = "ENDY";
        comps.AddRange(PowerBlock(36, 56));
        var sch = NewSch(new[]
        {
            S("ic", "U1", "RP2040", 40, 35, "3V3", "GND", "X_STEP", "X_DIR", "X_EN", "Y_STEP", "Y_DIR", "Y_EN", "ENDX", "ENDY"),
            S("module", "U2", "A4988 X", 100, 18, "X_EN", "X_STEP", "X_DIR", "VMOT", "GND", "X1A", "X1B", "X2A", "X2B"),
            S("module", "U3", "A4988 Y", 100, 55, "Y_EN", "Y_STEP", "Y_DIR", "VMOT", "GND", "Y1A", "Y1B", "Y2A", "Y2B"),
            S("connector", "J3", "Endstops", 15, 15, "ENDX", "ENDY", "GND", "3V3"),
            S("capacitor", "C1", "100uF", 140, 35, "VMOT", "GND"),
            S("crystal", "Y1", "12MHz", 15, 55, "OSC1", "OSC2"),
        }.Concat(PowerBlockSch(20, 80)),
            "STEP/DIR/EN per axis from the RP2040 (PIO timing), bulk cap on VMOT close to the drivers, active-low endstops.");
        return (sch, NewBoard("CNC Stepper Carrier", 96, 66, comps));
    }

    private static (Schematic, Board) BuildGamepad()
    {
        var comps = new List<PcbComponent>
        {
            P("ic", "U1", "RP2040", "QFN-56", 46, 26),
            P("button", "SW1", "UP", "TACT-6MM", 14, 14, "UP", "UP", "GND", "GND"),
            P("button", "SW2", "DOWN", "TACT-6MM", 14, 38, "DOWN", "DOWN", "GND", "GND"),
            P("button", "SW3", "LEFT", "TACT-6MM", 5, 26, "LEFT", "LEFT", "GND", "GND"),
            P("button", "SW4", "RIGHT", "TACT-6MM", 23, 26, "RIGHT", "RIGHT", "GND", "GND"),
            P("button", "SW5", "A", "TACT-6MM", 84, 20, "BTN_A", "BTN_A", "GND", "GND"),
            P("button", "SW6", "B", "TACT-6MM", 92, 30, "BTN_B", "BTN_B", "GND", "GND"),
            P("button", "SW7", "X", "TACT-6MM", 76, 30, "BTN_X", "BTN_X", "GND", "GND"),
            P("button", "SW8", "Y", "TACT-6MM", 84, 40, "BTN_Y", "BTN_Y", "GND", "GND"),
            P("transistor", "Q1", "2N7002 rumble", "SOT-23", 60, 46, "RUMBLE", "MOT-", "GND"),
            P("connector", "J1", "Motor", "TH-2PIN", 74, 50, "5V", "MOT-"),
            P("connector", "J2", "USB-C", "USB-C-16P", 46, 52, "GND", "", "USB_D-", "", "5V", "", "USB_D+", "", "", "5V", "", "", "", "", "", "GND"),
            P("crystal", "Y1", "12MHz", "FC-135", 34, 16, "OSC1", "OSC2"),
        };
        var u1 = comps[0];
        u1.Pads[0].Net = "3V3"; u1.Pads[28].Net = "GND";
        string[] gp = { "UP", "DOWN", "LEFT", "RIGHT", "BTN_A", "BTN_B", "BTN_X", "BTN_Y", "RUMBLE" };
        for (var i = 0; i < gp.Length; i++) u1.Pads[4 + i].Net = gp[i];
        u1.Pads[46].Net = "USB_D+"; u1.Pads[47].Net = "USB_D-";
        comps.AddRange(PowerBlock(30, 46));
        var sch = NewSch(new[]
        {
            S("ic", "U1", "RP2040", 55, 35, new[] { "3V3", "GND", "USB_D+", "USB_D-" }.Concat(gp).ToArray()),
            S("transistor", "Q1", "rumble", 100, 60, "RUMBLE", "MOT-", "GND"),
            S("crystal", "Y1", "12MHz", 20, 60, "OSC1", "OSC2"),
        }.Concat(new[] { ("SW1","UP"), ("SW2","DOWN"), ("SW3","LEFT"), ("SW4","RIGHT"), ("SW5","BTN_A"), ("SW6","BTN_B"), ("SW7","BTN_X"), ("SW8","BTN_Y") }
            .Select((sw, i) => S("button", sw.Item1, sw.Item2, 15 + (i % 2) * 20, 10 + (i / 2) * 12, sw.Item2, "GND")))
         .Concat(PowerBlockSch(100, 15)),
            "RP2040 native USB HID gamepad; buttons on internal pull-ups, MOSFET-driven rumble motor.");
        return (sch, NewBoard("USB Gamepad", 100, 62, comps));
    }
}

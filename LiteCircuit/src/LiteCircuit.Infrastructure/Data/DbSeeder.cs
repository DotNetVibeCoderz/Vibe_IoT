using LiteCircuit.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiteCircuit.Infrastructure.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        // Upsert: add any catalog parts missing from the existing database
        var existingNames = (await db.Components.Select(c => c.Name).ToListAsync()).ToHashSet();
        var missing = ComponentCatalog().Where(c => !existingNames.Contains(c.Name)).ToList();
        if (missing.Count > 0)
        {
            db.Components.AddRange(missing);
            await db.SaveChangesAsync();
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByEmailAsync("admin@litecircuit.dev") is null)
        {
            var admin = new AppUser
            {
                UserName = "admin@litecircuit.dev",
                Email = "admin@litecircuit.dev",
                EmailConfirmed = true,
                DisplayName = "Admin",
            };
            await users.CreateAsync(admin, "Admin123$");
        }
    }

    private static IEnumerable<LibComponent> ComponentCatalog()
    {
        LibComponent P(string name, string cat, string val, string fp, string mfr, string src, int pins, decimal price, int stock, string ds = "") =>
            new() { Name = name, Category = cat, Value = val, Footprint = fp, Manufacturer = mfr, Source = src, Pins = pins, PriceUsd = price, Stock = stock, DatasheetUrl = ds };

        return new[]
        {
            // Passives (KiCad-sourced)
            P("R0805 10k", "Resistor", "10k", "R0805", "Yageo", "KiCad", 2, 0.004m, 125000),
            P("R0805 1k", "Resistor", "1k", "R0805", "Yageo", "KiCad", 2, 0.004m, 98000),
            P("R0805 4.7k", "Resistor", "4.7k", "R0805", "Yageo", "KiCad", 2, 0.004m, 76000),
            P("R0805 220", "Resistor", "220", "R0805", "Yageo", "KiCad", 2, 0.004m, 143000),
            P("R0805 100", "Resistor", "100", "R0805", "Yageo", "KiCad", 2, 0.004m, 81000),
            P("C0805 100nF", "Capacitor", "100nF", "C0805", "Samsung", "KiCad", 2, 0.006m, 210000),
            P("C0805 1uF", "Capacitor", "1uF", "C0805", "Samsung", "KiCad", 2, 0.009m, 96000),
            P("C0805 10uF", "Capacitor", "10uF", "C0805", "Murata", "KiCad", 2, 0.02m, 65000),
            P("Electrolytic 100uF/16V", "Capacitor", "100uF", "CP-RADIAL-5MM", "Rubycon", "KiCad", 2, 0.05m, 30000),
            P("Inductor 10uH", "Inductor", "10uH", "L0805", "TDK", "KiCad", 2, 0.03m, 22000),
            // LEDs & diodes
            P("LED Red 0805", "LED", "red", "LED0805", "Kingbright", "KiCad", 2, 0.02m, 88000),
            P("LED Green 0805", "LED", "green", "LED0805", "Kingbright", "KiCad", 2, 0.02m, 64000),
            P("LED Blue 0805", "LED", "blue", "LED0805", "Kingbright", "KiCad", 2, 0.03m, 41000),
            P("1N4148 SOD-123", "Diode", "1N4148", "SOD-123", "onsemi", "KiCad", 2, 0.01m, 120000),
            P("SS34 Schottky", "Diode", "SS34", "SMA", "MDD", "KiCad", 2, 0.03m, 54000),
            // Transistors
            P("2N7002 MOSFET", "Transistor", "2N7002", "SOT-23", "onsemi", "KiCad", 3, 0.02m, 91000),
            P("AO3401 P-MOSFET", "Transistor", "AO3401", "SOT-23", "AOS", "Vendor", 3, 0.04m, 47000),
            P("S8050 NPN", "Transistor", "S8050", "SOT-23", "CJ", "KiCad", 3, 0.01m, 130000),
            // Power
            P("AMS1117-3.3 LDO", "Regulator", "3.3V", "SOT-223", "AMS", "KiCad", 3, 0.08m, 74000),
            P("MP1584 Buck Module", "Regulator", "3A buck", "MODULE-MP1584", "MPS", "Vendor", 4, 0.9m, 8200),
            P("TP4056 Charger", "Regulator", "LiPo 1A", "MODULE-TP4056", "TopPower", "Vendor", 6, 0.35m, 15400),
            // MCUs & modules
            P("ESP32-WROOM-32", "MCU", "WiFi+BT", "ESP32-MODULE", "Espressif", "Vendor", 38, 2.9m, 15000, "https://www.espressif.com/sites/default/files/documentation/esp32-wroom-32_datasheet_en.pdf"),
            P("ESP32-C3 Mini", "MCU", "WiFi RISC-V", "ESP32-MODULE", "Espressif", "Vendor", 30, 1.9m, 12500),
            P("STM32F103C8T6", "MCU", "Cortex-M3 72MHz", "LQFP-48", "ST", "Vendor", 48, 2.1m, 9100, "https://www.st.com/resource/en/datasheet/stm32f103c8.pdf"),
            P("STM32G030F6P6", "MCU", "Cortex-M0+ 64MHz", "TSSOP-20", "ST", "Vendor", 20, 0.7m, 18800),
            P("ATmega328P-AU", "MCU", "AVR 16MHz", "TQFP-32", "Microchip", "Vendor", 32, 1.6m, 11000),
            P("RP2040", "MCU", "Dual M0+ 133MHz", "QFN-56", "Raspberry Pi", "Vendor", 56, 0.8m, 26000),
            // Interface / driver ICs
            P("74HC595 Shift Register", "IC", "8-bit SR", "SOIC-16", "TI", "KiCad", 16, 0.12m, 45000),
            P("PCA9685 PWM Driver", "IC", "16ch PWM", "TSSOP-28", "NXP", "Vendor", 28, 1.3m, 6800),
            P("L293D Motor Driver", "IC", "dual H-bridge", "SOIC-16", "TI", "Vendor", 16, 1.8m, 5200),
            P("DRV8833 Motor Driver", "IC", "dual H-bridge", "TSSOP-16", "TI", "Vendor", 16, 0.9m, 9800),
            P("PAM8403 Audio Amp", "IC", "3W class-D", "SOIC-16", "Diodes Inc", "Vendor", 16, 0.25m, 21000),
            P("DS3231 RTC", "IC", "RTC TCXO", "SOIC-16", "Analog Devices", "Vendor", 16, 2.4m, 4100),
            P("HX711 ADC", "IC", "24-bit load cell", "SOIC-16", "Avia", "Vendor", 16, 0.45m, 13200),
            P("CH340C USB-UART", "IC", "USB serial", "SOIC-16", "WCH", "Vendor", 16, 0.35m, 32000),
            // Sensors
            P("BME280 Sensor", "Sensor", "T/H/P", "LGA-8", "Bosch", "Vendor", 8, 2.2m, 7600),
            P("DHT22 Sensor", "Sensor", "T/H", "TH-4PIN", "Aosong", "Vendor", 4, 2.5m, 5900),
            P("Soil Moisture Probe", "Sensor", "capacitive", "MODULE-3PIN", "Generic", "Vendor", 3, 0.6m, 8800),
            P("LDR 5mm", "Sensor", "light", "TH-2PIN", "Generic", "KiCad", 2, 0.08m, 26000),
            // Displays & audio
            P("OLED 0.96\" SSD1306", "Display", "128x64 I2C", "HDR-1x4", "Solomon", "Vendor", 4, 1.5m, 9700),
            P("TFT 2.4\" ILI9341", "Display", "240x320 SPI", "HDR-1x14", "Ilitek", "Vendor", 14, 3.8m, 4200),
            P("Buzzer 5V", "Audio", "passive", "TH-2PIN", "Generic", "KiCad", 2, 0.12m, 19000),
            P("DFPlayer Mini", "Audio", "MP3 module", "MODULE-DFPLAYER", "DFRobot", "Vendor", 16, 1.9m, 6100),
            P("RDA5807M FM Radio", "IC", "FM tuner", "MODULE-RDA5807", "RDA", "Vendor", 10, 0.8m, 5300),
            // Electromechanical
            P("Tactile Switch 6mm", "Switch", "momentary", "TACT-6MM", "Omron", "KiCad", 4, 0.05m, 68000),
            P("Micro USB Connector", "Connector", "USB 2.0", "USB-MICRO-B", "Molex", "KiCad", 5, 0.15m, 34000),
            P("USB-C Connector 16P", "Connector", "USB 2.0", "USB-C-16P", "GCT", "Vendor", 16, 0.35m, 21000),
            P("Pin Header 1x4", "Connector", "2.54mm", "HDR-1x4", "Generic", "KiCad", 4, 0.04m, 90000),
            P("Pin Header 1x8", "Connector", "2.54mm", "HDR-1x8", "Generic", "KiCad", 8, 0.07m, 66000),
            P("Screw Terminal 2P", "Connector", "5.08mm", "TERM-2", "Phoenix", "KiCad", 2, 0.18m, 24000),
            P("Screw Terminal 3P", "Connector", "5.08mm", "TERM-3", "Phoenix", "KiCad", 3, 0.26m, 17000),
            P("Servo Header 3P", "Connector", "2.54mm", "HDR-1x3", "Generic", "KiCad", 3, 0.03m, 71000),
            P("Crystal 8MHz", "Crystal", "8MHz", "HC49-SMD", "Abracon", "KiCad", 2, 0.15m, 28000),
            P("Crystal 32.768kHz", "Crystal", "32.768kHz", "FC-135", "Epson", "KiCad", 2, 0.2m, 16000),
            P("Crystal 12MHz", "Crystal", "12MHz", "FC-135", "Abracon", "KiCad", 2, 0.18m, 21000),
            // Relays & isolation
            P("Relay SRD-05VDC 10A", "Relay", "5V coil 10A", "RELAY-SRD", "Songle", "Vendor", 5, 0.55m, 18500),
            P("Optocoupler PC817", "IC", "1ch opto", "SOP-4", "Sharp", "KiCad", 4, 0.08m, 52000),
            P("TVS SMAJ5.0A", "Diode", "5V clamp", "SMA", "Littelfuse", "KiCad", 2, 0.09m, 33000),
            P("USBLC6-2 ESD Array", "IC", "USB ESD", "SOT-23", "ST", "Vendor", 6, 0.15m, 27000),
            // Power & drivers
            P("IRLZ44N MOSFET", "Transistor", "logic-level 47A", "TO-220", "Infineon", "Vendor", 3, 0.6m, 14200),
            P("MT3608 Boost Module", "Regulator", "2A boost", "MODULE-MP1584", "Aerosemi", "Vendor", 4, 0.4m, 11800),
            P("A4988 Stepper Driver", "IC", "1/16 microstep", "MODULE-A4988", "Allegro", "Vendor", 16, 1.1m, 9400),
            P("ULN2003 Darlington", "IC", "7ch 500mA", "SOIC-16", "TI", "KiCad", 16, 0.2m, 30000),
            // Analog & timers
            P("LM358 Op-Amp", "IC", "dual op-amp", "SOIC-8", "TI", "KiCad", 8, 0.08m, 61000),
            P("LM393 Comparator", "IC", "dual comparator", "SOIC-8", "TI", "KiCad", 8, 0.07m, 48000),
            P("NE555 Timer", "IC", "timer", "SOIC-8", "TI", "KiCad", 8, 0.09m, 57000),
            P("24C32 EEPROM", "IC", "32Kb I2C", "SOIC-8", "Microchip", "KiCad", 8, 0.11m, 25000),
            P("TXS0102 Level Shifter", "IC", "2ch bidir", "SOIC-8", "TI", "Vendor", 8, 0.35m, 16800),
            P("ACS712-05B Current Sensor", "IC", "±5A hall", "SOIC-8", "Allegro", "Vendor", 8, 1.4m, 6300),
            // Wireless & positioning modules
            P("NEO-6M GPS Module", "Module", "GPS UART", "HDR-1x4", "u-blox", "Vendor", 4, 3.2m, 4800),
            P("NRF24L01+ Module", "Module", "2.4GHz SPI", "MODULE-NRF24", "Nordic", "Vendor", 8, 0.9m, 13600),
            P("SX1278 LoRa Module", "Module", "433MHz LoRa", "HDR-1x8", "Semtech", "Vendor", 8, 3.6m, 5100),
            P("SIM800L GSM Module", "Module", "GSM/GPRS", "HDR-1x8", "SIMCom", "Vendor", 8, 3.9m, 3700),
            P("RC522 RFID Module", "Module", "13.56MHz SPI", "HDR-1x8", "NXP", "Vendor", 8, 1.2m, 8900),
            P("HC-SR04 Ultrasonic", "Module", "distance", "HDR-1x4", "Generic", "Vendor", 4, 0.8m, 15200),
            P("HC-SR501 PIR Module", "Module", "motion", "MODULE-3PIN", "Generic", "Vendor", 3, 0.9m, 10400),
            // Sensors
            P("SGP30 VOC Sensor", "Sensor", "eCO2/TVOC I2C", "LGA-8", "Sensirion", "Vendor", 8, 5.8m, 2900),
            P("DS18B20 Temp Sensor", "Sensor", "1-Wire ±0.5°C", "TH-3PIN", "Analog Devices", "Vendor", 3, 1.1m, 12700),
            P("MPU6050 IMU", "Sensor", "6-axis I2C", "QFN-56", "TDK", "Vendor", 24, 1.6m, 8100),
            P("A3144 Hall Switch", "Sensor", "hall effect", "TH-3PIN", "Allegro", "KiCad", 3, 0.15m, 22000),
            P("Reed Switch", "Sensor", "magnetic NO", "TH-2PIN", "Generic", "KiCad", 2, 0.12m, 18000),
            P("MAX9814 Mic Amp", "Sensor", "mic + AGC", "MODULE-3PIN", "Analog Devices", "Vendor", 3, 1.3m, 5600),
            // LEDs & display extras
            P("WS2812B RGB LED", "LED", "addressable", "SMD-5050", "Worldsemi", "Vendor", 4, 0.05m, 240000),
            P("RGB LED 5050", "LED", "common anode", "SMD-5050", "Generic", "KiCad", 4, 0.06m, 74000),
            P("7-Segment 0.56\"", "Display", "1 digit CC", "HDR-1x10", "Generic", "KiCad", 10, 0.35m, 9600),
            // Electromechanical extras
            P("Rotary Encoder EC11", "Switch", "20 detent + push", "EC11", "Alps", "KiCad", 5, 0.45m, 12300),
            P("Slide Switch SPDT", "Switch", "SPDT", "TH-3PIN", "Generic", "KiCad", 3, 0.09m, 28000),
            P("MicroSD Socket", "Connector", "push-push", "HDR-1x8", "Molex", "Vendor", 8, 0.5m, 9800),
            P("CR2032 Holder", "Connector", "coin cell", "TH-2PIN", "Keystone", "KiCad", 2, 0.25m, 13500),
            P("Vibration Motor 1027", "Actuator", "coin 3V", "TH-2PIN", "Generic", "Vendor", 2, 0.55m, 7200),
            P("Fuse Holder 5x20", "Protection", "250V", "TH-2PIN", "Littelfuse", "KiCad", 2, 0.3m, 8600),
            P("PTC Fuse 1A", "Protection", "resettable", "R0805", "Bourns", "KiCad", 2, 0.12m, 26000),
        };
    }
}

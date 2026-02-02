# GHI FEZ HAT Driver & Dashboard for Raspberry Pi

This project is a .NET application utilizing **Avalonia UI** to provide a control dashboard for the GHI Electronics FEZ HAT. It has been ported from the legacy UWP driver to modern .NET (compatible with .NET 8/9/10).

## Features
- **Cross-Platform:** Runs on Raspberry Pi (Raspbian) and Windows.
- **Dashboard:** Visual interface to monitor Light, Temperature, Acceleration, and Buttons.
- **Control:** Control DC Motors, Servos, and RGB LEDs directly from the UI.
- **Modern .NET:** Uses `System.Device.Gpio` and `Iot.Device.Bindings`.

## Prerequisites
1. **Raspberry Pi** with Raspberry Pi OS (Raspbian).
2. **GHI FEZ HAT** installed on the GPIO header.
3. **.NET 8 SDK** (or newer) installed on the Pi.
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0
   echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
   echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
   source ~/.bashrc
   ```
4. **I2C Enabled:**
   Run `sudo raspi-config`, go to Interface Options, and enable I2C.

## How to Run
1. Navigate to the project folder.
2. Build the project:
   ```bash
   dotnet build
   ```
3. Run the project (Root privileges might be required for hardware access depending on system config):
   ```bash
   sudo dotnet run
   ```
   *Note: If running with `sudo`, you might need to specify the full path to `dotnet` or ensure root has dotnet in PATH.*

## Troubleshooting
- **I2C Error:** Ensure I2C is enabled and the HAT is seated correctly. Run `i2cdetect -y 1` to verify addresses (0x40, 0x48, 0x1C).
- **Display Issues:** If running via SSH, you need X11 forwarding or run directly on the Pi's desktop environment. If using DRM (no desktop), ensure Avalonia DRM support is configured (default `UsePlatformDetect` usually handles X11/Wayland/Win).

---

# (Bahasa Indonesia)
Proyek ini adalah aplikasi .NET menggunakan **Avalonia UI** sebagai dashboard kontrol untuk GHI Electronics FEZ HAT. Proyek ini merupakan porting dari driver UWP lama ke .NET modern.

## Fitur
- **Lintas Platform:** Berjalan di Raspberry Pi dan Windows.
- **Dashboard:** Antarmuka visual untuk memantau Cahaya, Suhu, Akselerasi, dan Tombol.
- **Kontrol:** Mengontrol Motor DC, Servo, dan LED RGB langsung dari UI.

## Persyaratan
1. **Raspberry Pi** dengan Raspberry Pi OS.
2. **GHI FEZ HAT** terpasang.
3. **.NET 8 SDK** (atau lebih baru).
4. **I2C Aktif:** Gunakan `sudo raspi-config` untuk mengaktifkan I2C.

## Cara Menjalankan
1. Masuk ke folder project.
2. Build:
   ```bash
   dotnet build
   ```
3. Jalankan (Gunakan sudo jika perlu akses hardware):
   ```bash
   sudo dotnet run
   ```

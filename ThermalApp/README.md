# ThermalApp - Visualisasi Termal MLX90640

Aplikasi desktop cross-platform berbasis Avalonia untuk visualisasi data dari sensor thermal array inframerah MLX90640.

## 🇮🇩 Versi Bahasa Indonesia

### Gambaran Umum
Aplikasi ini dirancang untuk menerima data suhu dari sensor thermal array MLX90640 dan menampilkannya secara real-time dalam bentuk grid warna yang representatif. Aplikasi ini mendukung antarmuka pengguna grafis modern yang bisa berjalan pada Windows, Linux, dan macOS.

### Fitur Utama
- Tampilan real-time data thermal 24x32 pixel dari sensor MLX90640
- Visualisasi warna yang intuitif (hijau = dingin, merah = panas)
- Opsi skalabilitas manual dan otomatis untuk rentang suhu
- Simulasi data thermal untuk pengujian tanpa perangkat keras
- Mendukung antarmuka I2C ke sensor asli
- Kontrol kecepatan refresh tampilan

### Teknologi yang Digunakan
- **Avalonia**: Framework UI cross-platform (setara WPF untuk .NET)
- **System.Device.Gpio**: Untuk komunikasi I2C ke perangkat hardware
- **C# dan .NET 8.0**
- **XAML**: Untuk desain antarmuka pengguna

### Kebutuhan Sistem
- .NET 8.0 SDK atau runtime
- Perangkat yang mendukung .NET 8.0 (Windows, Linux, atau macOS)
- Sensor Thermal MLX90640 (opsional, untuk pengujian real-device)

### Cara Penggunaan
1. Clone atau unduh proyek ini
2. Build solusi menggunakan `dotnet build`
3. Jalankan aplikasi menggunakan `dotnet run`
4. Pilih sumber data (Simulasi atau I2C) dari dropdown
5. Sesuaikan rentang suhu minimal/maksimal jika diperlukan
6. Klik tombol "Start" untuk memulai streaming data thermal

## 🌐 English Version

### Overview
This application is designed to receive temperature data from the MLX90640 thermal array sensor and display it in real-time as a color-coded grid visualization. The application provides a modern graphical user interface that runs cross-platform on Windows, Linux, and macOS.

### Key Features
- Real-time thermal grid display of 24x32 pixels from MLX90640 sensor
- Intuitive color-based visualization (green = cold, red = hot)
- Choice of manual and automatic scaling for temperature ranges
- Built-in simulation mode for testing without hardware
- Support for direct I2C interface to actual sensor
- Adjustable refresh rate control

### Technologies Used
- **Avalonia**: Cross-platform UI framework (similar to WPF for .NET)
- **System.Device.Gpio**: For I2C communication with hardware devices
- **C# and .NET 8.0**
- **XAML**: For user interface design

### System Requirements
- .NET 8.0 SDK or runtime
- Device supporting .NET 8.0 (Windows, Linux, or macOS)
- MLX90640 Thermal Sensor (optional, for real-device testing)

### Usage Instructions
1. Clone or download this project
2. Build the solution using `dotnet build`
3. Run the application using `dotnet run`
4. Select data source (Simulation or I2C) from the dropdown
5. Adjust min/max temperature ranges as needed
6. Click the "Start" button to begin thermal data streaming
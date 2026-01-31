# Ringkasan Proyek

## OBDReader

**OBDReader** adalah aplikasi desktop modern berbasis C# dan Avalonia UI untuk membaca data mobil melalui konektor OBD-II (ELM327) via USB/Serial.

### Fitur Utama
- **Dashboard Futuristik**: Menampilkan Kecepatan, RPM, Suhu Mesin, dan Voltase Aki dengan tampilan modern.
- **Koneksi ELM327**: Mendukung pembacaan command AT dasar (RPM, Speed, Temp).
- **Mode Simulasi**: Jika tidak ada alat terhubung, pilih "SIMULATION MODE" untuk melihat demo.
- **Analisa Realtime**: Memberikan peringatan jika RPM terlalu tinggi atau suhu mesin overheat.
- **Cross-Platform**: Dapat berjalan di Windows, Linux, dan macOS.

### Cara Menggunakan
1. Hubungkan alat ELM327 USB ke komputer.
2. Jalankan aplikasi.
3. Pilih Port COM yang sesuai dari dropdown.
4. Klik tombol "CONNECT".
5. Jika tidak punya alat, pilih "SIMULATION MODE" dan klik Connect.

### Persyaratan
- .NET 8.0 Runtime
- Driver ELM327 USB (jika menggunakan hardware asli)

---

## ThermalApp

Aplikasi desktop cross-platform berbasis Avalonia untuk visualisasi data dari sensor thermal array inframerah MLX90640.

### Gambaran Umum
Aplikasi ini dirancang untuk menerima data suhu dari sensor thermal array MLX90640 dan menampilkannya secara real-time dalam bentuk grid warna yang representatif. Aplikasi ini mendukung antarmuka pengguna grafis modern yang bisa berjalan pada Windows, Linux, dan macOS.

### Fitur Utama
- Tampilan real-time data thermal 24x32 pixel dari sensor MLX90640
- Visualisasi warna yang intuitif (hijau = dingin, merah = panas)
- Opsi skalabilitas manual dan otomatis untuk rentang suhu
- Simulasi data thermal untuk pengujian tanpa perangkat keras
- Mendukung antarmuka I2C ke sensor asli
- Kontrol kecepatan refresh tampilan

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

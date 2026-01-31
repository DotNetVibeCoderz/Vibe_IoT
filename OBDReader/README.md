# OBDReader (Jacky's Edition)

## 🇮🇩 Bahasa Indonesia

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

## 🇬🇧 English

**OBDReader** is a modern desktop application based on C# and Avalonia UI designed to read vehicle data via OBD-II (ELM327) connectors over USB/Serial.

### Key Features
- **Futuristic Dashboard**: Displays Speed, RPM, Coolant Temp, and Battery Voltage with a sleek UI.
- **ELM327 Connection**: Supports basic AT commands reading (RPM, Speed, Temp).
- **Simulation Mode**: If no hardware is attached, select "SIMULATION MODE" to see a demo.
- **Realtime Analysis**: Provides warnings for high RPM or overheating.
- **Cross-Platform**: Runs on Windows, Linux, and macOS.

### How to Use
1. Plug in your ELM327 USB device to your computer.
2. Run the application.
3. Select the appropriate COM Port from the dropdown.
4. Click "CONNECT".
5. If you don't have the hardware, select "SIMULATION MODE" and click Connect.

### Requirements
- .NET 8.0 Runtime
- ELM327 USB Drivers (if using real hardware)

---
*Created by Jacky the Code Bender - Gravicode Studios*

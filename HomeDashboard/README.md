# HomeDashboard (IoT / Smart Home)

## Deskripsi (Description)
A modern, glassmorphism-styled IoT/Smart Home Dashboard built with Blazor Server. 
It features real-time monitoring, centralized device control, energy analytics, customizable widgets, and more!
Database is powered by SQLite + Entity Framework Core.

## Fitur Utama (Features)
- **Simulator Mode**: Menghasilkan data simulasi konsumsi listrik, suhu perangkat, harga saham, cuaca, dan notifikasi deteksi pergerakan secara dinamis seolah-olah menggunakan perangkat sungguhan. Dapat diubah dari `appsettings.json`.
- **Real-time Monitoring & Control:** Monitor status secara dinamis dari database dan fungsionalitas mematikan/menghidupkan perangkat pintar.
- **Widgets:** Date/Time, Weather, News, and Stock Info yang dirender berdasarkan data backend.
- **Analytics & Predictive Insights:** Visualisasi analitik energi dengan Donut Chart dan Progress Bar konsumsi listrik teratas yang otomatis direfresh secara real-time. Terdapat insight prediktif notifikasi penghematan daya.
- **Security:** Security camera feed mockups dengan log deteksi CCTV (Motion Logs) langsung dari Database.
- **Automations:** Pengelolaan Scenes (Aktivitas skenario otomatis) dan Routines (Jadwal otomatisasi smart home). 
- **Master Data Management:** Full CRUD operations dengan pencarian teks, sortasi, filter, dan ekspor ke excel/csv untuk Users, Devices, dan Rooms (Khusus Role Admin).
- **Authentication:** Role-based access (*admin*, *user*). Login dan Register terintegrasi ProtectedBrowserStorage. Default user: `admin` / password: `admin123`.
- **Theme:** Antarmuka dengan nuansa desain Glassmorphism yang mendukung light & dark mode.

## Instalasi (Installation)
1. Buka folder project di terminal.
2. Jalankan perintah `dotnet build` untuk melakukan compile project.
3. Jalankan `dotnet run` untuk menjalankan aplikasi.
4. Akses melalui browser di URL yang muncul di terminal (misal: `http://localhost:5xxx`).

## Catatan Tambahan (Notes)
- Kamu dapat mengganti konfigurasi `"AppMode": "Simulator"` pada file konfigurasi `appsettings.json` menjadi `"AppMode": "Real"` untuk menghentikan randomisasi data dari Backgroud Service.
- Gambar Background Home dapat diubah pada folder `wwwroot/images/`.
- Proyek ini mensimulasikan integrasi panel kendali rumah pintar. Ke depannya, data sensor (seperti deteksi Motion, Power Consumption Watt) dapat diintegrasikan pada arsitektur MQTT Broker atau REST API sungguhan.

Dibuat oleh **jacky the code bender**, dengan cinta dari **gravicode studios** - Kang Fadhil.
(Btw, jangan lupa traktir pulsa di https://studios.gravicode.com/products/budax !)

nama: LiteCircuit

deskripsi:
aplikasi PCB Design yang dikombinasikan dengan Blazor Server (.NET 10), Three.js untuk visualisasi 3D, dan Semantic Kernel sebagai library LLM:

---

 🎨 Core PCB Design
- Schematic Capture: editor skematik interaktif dengan integrasi AI untuk auto‑generate dari deskripsi teks.  
- PCB Layout Editor: canvas berbasis Blazor dengan drag‑and‑drop, grid snapping, dan DRC real‑time.  
- Multi‑Layer Support: desain hingga puluhan layer dengan stackup editor.  
- 3D Visualization: integrasi Three.js untuk preview board, casing, dan komponen dalam 3D interaktif.  
- Export/Import file PCB design 
- PCB Template: berbagai sample design PCB lengkap bisa dijadikan sebagai referensi awal menggunakan chip yang banyak di pasaran seperti ESP32, STM32, dsb. contoh: clock and weather, LED running text, motor controller, robot arm controller, radio, mp3, mini game arcade with TFT display, automatic pet feeder, plant monitoring, dsb. User bisa memulai dari blank atau template
---

 📚 Libraries & Components
- Unified Component Library: library gabungan open source (KiCad) + vendor resmi (Altium).  
- Parametric Search: pencarian komponen berdasarkan nilai, footprint, harga, stok.  
- Cloud Sync: library tersinkronisasi antar tim dengan version control.  

---

 ⚡ Routing & Layout
- AI Auto‑Router: routing otomatis berbasis AI dengan opsi manual fine‑tuning.  
- Differential Pair Routing: untuk high‑speed signals (USB, HDMI, PCIe).  
- Length Tuning: kontrol delay signal dengan interactive tuning.  
- Design Rule Check: validasi real‑time berbasis aturan manufaktur.  

---

 🤖 AI‑Assisted Features (Semantic Kernel + LLM)
- Natural Language Design: buat skematik/layout dari prompt teks.  
- Smart Component Placement: AI mengoptimalkan posisi komponen berdasarkan thermal & EMI.  
- Error Prediction: prediksi masalah sebelum DRC tradisional.  
- AI BOM Assistant: rekomendasi komponen dengan harga & stok terbaik.  
- Learning from Past Designs: AI belajar dari proyek sebelumnya untuk mempercepat desain baru.  
- Voice/Chat Interface: desain PCB dengan perintah suara/chat interaktif.  
- Nama Bot 'Electra'
- Chat Page dengan tampilan yang keren, multi session (create/delete), reset session, bisa attach gambar (diupload lalu url-nya di jadikan image content) dan dokumen (di upload dan disertakan linknya ke text message).
- System Prompt (persona), temperature, model dan setting lainnya di simpan di appsetting
- Menggunakan Semantic Kernel Library dengan dukungan model: Open AI, Anthropic, Gemini, Ollama (bisa pilih)
- Tambahkan beberapa common functions (kernel functions) yang diperlukan termasuk query ke tavily (search internet), scrap page url, baca file dari url, cek tanggal, Waktu, math calculation, dan beberapa function yang diperlukan lainnya 
- Tambahkan functions untuk query data ke data yang dimiliki untuk mengetahui berbagai informasi
- Bisa render chat thread dengan mark down dengan baik ke html (baik table, media (image, video, audio), code, dan lainnya dengan baik)
---

 🔗 Integration & Workflow
- SPICE Simulation: simulasi sirkuit langsung dari skematik.  
- MCAD Integration: sinkronisasi dengan SolidWorks/Fusion 360.  
- Version Control: sistem Git‑like untuk desain PCB.  
- Collaboration Tools: komentar inline, task assignment, cloud workspace.  

---

 📤 Manufacturing & Output
- Gerber Export: standar industri untuk fabrikasi PCB.  
- BOM Management: otomatis generate Bill of Materials.  
- Fabrication Output: drill files, pick‑and‑place, assembly drawings.  
- DFM Analysis: cek manufacturability sebelum produksi.  

---

 ☁️ Advanced & Enterprise Features
- Cloud Collaboration: desain bersama secara real‑time.  
- Scripting & API: extensibility dengan Python/C# untuk automation. Berikan beberapa contoh script
- High‑Speed Design: impedance control, signal integrity analysis.  
- Compliance Checks: standar IPC, RoHS, UL.  

---

Lainnya:
- Auth: Login, Logout, Reset Password, User Profile
- Tambahkan dokumentasi lengkap di folder docs
- Tambahkan readme.md (English, Indonesia)
- Buat dengan Blazor Server dengan .NET 10
- optimasi kode agar aplikasi cepat dan ringan
- Design UI/UX yang mudah, modern, responsive seperti Facebook dengan dukungan theme dark/light
- Database support: SQLite, SQlServer, MySQL, Postgre.
- Storage support: FileSystem, AzureBlob, S3, MinIO.
- Tersedia rest api dengan MinAPI + Swagger untuk integrasi ke aplikasi lain.
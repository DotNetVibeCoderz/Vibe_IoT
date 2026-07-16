# LiteCircuit ⚡

**Desain PCB berbasis browser dengan kopilot AI.** Blazor Server (.NET 10) + Three.js + Semantic Kernel.

> English: [README.md](README.md)

## Fitur

- **Schematic capture** — editor SVG interaktif dengan komponen, wire, label net, dan generate otomatis dari deskripsi teks (AI).
- **PCB layout** — editor canvas dengan drag-and-drop, grid snapping, ratsnest, track multi-layer, via, dan DRC real-time.
- **Visualisasi 3D** — preview board, tembaga, dan komponen dengan Three.js; ekspor STL untuk SolidWorks / Fusion 360.
- **Electra, kopilot AI** — chat multi-sesi (lampiran gambar + dokumen), auto-router, prediksi error, saran penempatan, asisten sourcing BOM. Provider: **OpenAI, Anthropic, Gemini, Ollama** (bisa dipilih di konfigurasi).
- **Output manufaktur** — Gerber RS-274X, drill Excellon, pick-and-place CSV, BOM CSV, analisis DFM, cek compliance IPC/RoHS/UL.
- **SPICE** — ekspor netlist plus solver DC operating point bawaan.
- **Template** — 10 desain siap pakai: jam & cuaca ESP32, LED running text, motor controller, robot arm, radio FM, MP3 player, mini arcade (TFT), pet feeder, plant monitor.
- **Kolaborasi** — snapshot desain ala Git (commit / restore / diff) dan komentar inline.
- **Extensibility** — REST API (Minimal API + Swagger di `/swagger`), konsol scripting C# di aplikasi, contoh Python.
- **Library terpadu** — komponen KiCad + vendor dengan pencarian parametrik (nilai, footprint, harga, stok), plus **update online**: tarik simbol terbaru langsung dari library resmi KiCad (GitLab) atau CSV dari URL apa pun lewat halaman Library.

## Mulai cepat

```bash
# Butuh .NET 10 SDK
dotnet run --project src/LiteCircuit.Web
```

Buka http://localhost:5000 (atau URL yang tercetak) lalu login dengan akun demo:

| Email | Password |
|---|---|
| `admin@litecircuit.dev` | `Admin123$` |

Konfigurasi default memakai **SQLite** dan **storage filesystem** — tanpa dependensi eksternal.

## Mengaktifkan kopilot AI

Edit `src/LiteCircuit.Web/appsettings.json`:

```jsonc
"Ai": {
  "Provider": "Anthropic",          // OpenAI | Anthropic | Gemini | Ollama
  "ApiKey": "sk-...",               // tidak perlu untuk Ollama
  "Temperature": 0.7
},
"Tavily": { "ApiKey": "tvly-..." }  // opsional: pencarian internet untuk Electra
```

Semua provider diakses lewat endpoint yang kompatibel OpenAI, jadi ganti provider cukup satu baris. Model default per provider: `gpt-4o-mini`, `claude-sonnet-5`, `gemini-2.0-flash`, `llama3.2`.

## Ganti database / storage

```jsonc
"Database": { "Provider": "Postgres" },   // Sqlite | SqlServer | Postgres | MySql
"Storage":  { "Provider": "MinIO" }       // FileSystem | AzureBlob | S3 | MinIO
```

Connection string ada di `ConnectionStrings` dan `Storage:*`. Lihat [docs/configuration.md](docs/configuration.md).

## Dokumentasi

Semua dokumentasi (bahasa Inggris) ada di folder [docs/](docs/): mulai dari [getting-started](docs/getting-started.md), [arsitektur](docs/architecture.md), [editor](docs/editors.md), [fitur AI](docs/ai-features.md), [manufaktur](docs/manufacturing.md), [REST API](docs/api.md), [scripting](docs/scripting.md), sampai [konfigurasi](docs/configuration.md).

## Struktur solusi

```
src/
  LiteCircuit.Core/            Model domain (dokumen JSON board/skematik, entity)
  LiteCircuit.Infrastructure/  EF Core (4 provider DB), storage (4 provider),
                               DRC, auto-router, Gerber/drill, BOM, SPICE, DFM,
                               template, scripting, Semantic Kernel + Electra
  LiteCircuit.Web/             UI Blazor Server, auth Identity, Minimal API + Swagger
docs/                          Dokumentasi
scripts/examples/              Contoh otomasi Python + C#
```

## Kredit

Dibuat oleh **Gravicode Studios**, di-lead oleh **Kang Fadhil**.

## Lisensi

MIT — bikin sesuatu dan kirim ke pabrik PCB. 🛠

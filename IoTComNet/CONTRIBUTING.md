# Contributing to IoTCom.Net

Thank you for helping! IoTCom.Net is built by Gravicode Studios, led by Kang Fadhil, and welcomes contributions.
*Bahasa Indonesia: lihat bagian bawah halaman ini.*

## Before you start

- Read [solution-design.md](solution-design.md) and [docs/en/concepts/architecture.md](docs/en/concepts/architecture.md):
  the curation tiers decide whether a protocol is adapted, implemented in C#, or implemented in Rust.
- New protocols follow [docs/en/contributing/adding-a-protocol.md](docs/en/contributing/adding-a-protocol.md).

## Development loop

```bash
dotnet build IoTCom.Net.slnx
dotnet test tests/IoTCom.Net.Tests
dotnet test tests/IoTCom.Net.Tests --filter "FullyQualifiedName~Modbus"   # one area
cd rust && cargo test --workspace && cargo clippy --workspace --all-targets -- -D warnings
python conformance/generate.py          # after changing shared vectors
python build/generate_notebooks.py      # after changing notebook content
python build/check_docs_parity.py       # EN/ID parity + links
python build/check_notebooks.py         # run every notebook
```

## Rules of the house

- Libraries build with warnings as errors, nullable enabled, trimming/AOT analyzers on. Do not suppress a warning
  without a justification.
- Protocol logic is sans-I/O: codecs are pure functions; endpoints only move bytes.
- Every parser bounds its inputs. Treat network and device data as hostile.
- Writes to equipment must respect read-only modes.
- Every English docs page needs an Indonesian twin (`translation-status: synced`); CI enforces it.
- New dependencies: check the licence (no GPL/AGPL in core packages) and add versions to `Directory.Packages.props`
  or the Cargo workspace.

## Pull requests

Describe the change, link the issue, include tests, and update `Progress.md` when a component changes status.

---

## Berkontribusi (Bahasa Indonesia)

Terima kasih sudah membantu! Baca [solution-design.md](solution-design.md) dan
[docs/id/concepts/architecture.md](docs/id/concepts/architecture.md) terlebih dahulu; protokol baru mengikuti
[docs/id/contributing/adding-a-protocol.md](docs/id/contributing/adding-a-protocol.md). Perintah pengembangan sama
seperti di atas. Aturan utama: warning dianggap error pada library, logika protokol sans-I/O, setiap parser membatasi
input, penulisan ke peralatan menghormati mode read-only, dan setiap halaman dokumentasi English wajib punya kembaran
Bahasa Indonesia (dicek oleh CI).

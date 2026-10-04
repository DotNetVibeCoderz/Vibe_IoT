# Security policy

IoTCom.Net talks to equipment in the physical world, so we take reports seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Report privately to Gravicode Studios through the repository's private
vulnerability reporting (GitHub Security Advisories) or to the maintainers' contact listed on the NuGet package page.
Include the affected package and version, a description, and a proof of concept if you have one.

We aim to acknowledge reports within 3 working days and to publish a fix or mitigation within 30 days for high-severity
issues. We credit reporters unless they prefer to stay anonymous.

## Scope

- Parsers and codecs (C# and Rust): crashes, unbounded allocation, out-of-bounds reads, panics crossing the FFI boundary.
- Safety features: bypasses of read-only modes or the CLI write confirmation.
- Secrets: credentials appearing in logs or traffic captures.

Protocols without built-in security (Modbus, Art-Net, sACN, NMEA) are out of scope for "no authentication" reports;
see [docs/en/guides/security.md](docs/en/guides/security.md) for deployment guidance.

## Supported versions

Only the latest preview receives security fixes until 1.0.

---

**Bahasa Indonesia.** Jangan membuka issue publik untuk kerentanan. Laporkan secara privat lewat fitur private
vulnerability reporting (GitHub Security Advisories) atau kontak maintainer di halaman paket NuGet. Panduan deployment
aman ada di [docs/id/guides/security.md](docs/id/guides/security.md).

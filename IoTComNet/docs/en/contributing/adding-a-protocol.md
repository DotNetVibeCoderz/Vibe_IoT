---
title: Adding a protocol
translation-status: synced
---

# Adding a protocol

## 1. Decide the tier

Check the [curation tiers](../concepts/architecture.md#curation-tiers). If .NET or a healthy library already covers
the protocol, write an adapter (T1). Implement only real gaps (T2). Then apply the Rust-or-C# rule.

## 2. Write the codec first

Pure functions, no I/O: build/parse frames, compute checksums, describe fields (`FrameField`) for the frame lane.
Add spec examples to `/conformance/<protocol>.json` via `conformance/generate.py` so both languages test the same bytes.

## 3. Write the endpoint

Derive from `EndpointBase`. Accept transports through a builder implementing `ITransportBuilder<T>` (client) or
`IListenerBuilder<T>` (server) so `UseTcp`, `UseSerial` and `UseInMemory` work for free. Call `Tap(...)` for every
frame, use `SetState`, honour `CancellationToken`, and throw the IoTCom exception types.

For Rust (T2-R): implement `iotcom_core::Machine` in a `#![forbid(unsafe_code)]` crate, add a `cdylib` under
`crates/native/` that uses `export_common!()` and `ffi_guard`, and a .NET package with `LibraryImport` bindings,
a `SafeHandle` and a driver client.

## 4. Simulator, sample, notebook, demo

Every protocol ships a simulator and a console sample (≤ 150 lines, `--simulate`), a notebook pair generated from
`build/generate_notebooks.py`, and a Gallery demo when it makes sense.

## 5. Documentation in both languages

Add `docs/en/protocols/<protocol>.md` using the protocol page template (summary → when to use → roles → transports →
installation → quickstart → configuration → examples → simulator → testing → security → limitations → learn more) and
its `docs/id/` twin. CI fails when a page has no translation (`python build/check_docs_parity.py`).

## Definition of done

- Public API documented (XML docs + EN/ID page)
- Unit, conformance and (for Rust) fuzz-style tests
- Simulator, console sample, notebook pair, Gallery demo when relevant
- Entry in the support matrix and in `Progress.md`

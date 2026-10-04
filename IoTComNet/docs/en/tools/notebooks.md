---
title: Notebooks
translation-status: synced
---

# Notebooks

Polyglot (.NET Interactive) notebooks live in `notebooks/`, one per protocol, in English (`.en.ipynb`) and Bahasa
Indonesia (`.id.ipynb`) with identical code. Open them in VS Code with the *Polyglot Notebooks* extension, or in
Jupyter with the .NET kernel. Every notebook runs against in-process simulators.

| Notebook | Topic |
|---|---|
| `00-start-here` | the endpoint model, in-memory links, the traffic tap |
| `industrial/01-modbus` | server, simulator, client, word orders, read-only mode, Rust engine, troubleshooting |
| `transport/02-framing-crc` | the CRC catalogue, SLIP/COBS/HDLC, streaming decode |
| `navigation/03-nmea` | parsing, a simulated receiver, the GNSS fix |
| `messaging/04-mqtt-senml` | broker, wildcard subscriptions, SenML JSON vs CBOR |
| `99-protocol-chooser` | which protocol for which job |

Each notebook follows the same structure: what the protocol is → setup → client → server → pub/sub (when relevant) →
experiments with the simulator → troubleshooting → exercise.

The notebooks are generated from one spec (`build/generate_notebooks.py`) so the two languages never drift; CI
extracts every code cell and runs it as a program.

Working from a clone? Pack the libraries locally and point the notebook at them:

```
#i "nuget: <repo>/artifacts/packages"
#r "nuget: IoTCom.Net, 0.1.0-preview.1"
```

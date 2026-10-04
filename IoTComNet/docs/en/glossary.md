---
title: Glossary
translation-status: synced
---

# Glossary

Technical terms are kept in English in the Indonesian documentation; this glossary maps them.

| Term | Meaning | Bahasa Indonesia |
|---|---|---|
| ADU | Application Data Unit: a complete Modbus frame (addressing + PDU + checksum/header) | ADU (unit data aplikasi) |
| Broker | MQTT server routing messages between clients | broker |
| Client / master | the side that sends requests | client / master |
| Coil | Modbus read/write bit | coil |
| CRC | Cyclic Redundancy Check, a checksum | CRC |
| Endpoint | an IoTCom object that talks a protocol (client, server, publisher, subscriber) | endpoint |
| Frame | a unit of bytes on the wire | frame |
| Frame lane | IoTCom's view of a frame as byte tiles coloured by field | frame lane (jalur frame) |
| Holding register | Modbus read/write 16-bit register | holding register |
| Input register | Modbus read-only 16-bit register | input register |
| Payload | the data carried by a frame or message | payload |
| PDU | Protocol Data Unit: function code + data | PDU |
| Publisher / subscriber | pub/sub roles | publisher / subscriber |
| RID | .NET Runtime Identifier, e.g. `linux-arm64` | RID |
| Sans-I/O | protocol logic that performs no I/O itself | sans-I/O (tanpa I/O) |
| Server / slave | the side that answers requests | server / slave |
| Simulator | an in-process stand-in for a device | simulator |
| Tap | a hook receiving every raw frame | tap (penyadap trafik) |
| Topic / filter | MQTT address / subscription pattern with `+` and `#` | topik / filter |
| Transport | the link carrying bytes (TCP, serial, in-memory) | transport |
| Unit id | Modbus slave address | unit id |
| Universe | 512 DMX channels | universe |

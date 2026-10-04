---
title: Glosarium
translation-status: synced
---

# Glosarium

Istilah teknis dipertahankan dalam bahasa Inggris di dokumentasi Bahasa Indonesia; glosarium ini memetakannya.

| Istilah | Arti | Padanan |
|---|---|---|
| ADU | Application Data Unit: frame Modbus lengkap (pengalamatan + PDU + checksum/header) | unit data aplikasi |
| Broker | server MQTT yang meneruskan pesan antar-client | broker |
| Client / master | pihak yang mengirim request | client / master |
| Coil | bit baca/tulis Modbus | coil |
| CRC | Cyclic Redundancy Check, sebuah checksum | CRC |
| Endpoint | objek IoTCom yang berbicara sebuah protokol (client, server, publisher, subscriber) | endpoint |
| Frame | satuan byte di jalur | frame |
| Frame lane | tampilan IoTCom untuk frame berupa ubin byte yang diwarnai per field | jalur frame |
| Holding register | register 16-bit baca/tulis Modbus | holding register |
| Input register | register 16-bit baca saja Modbus | input register |
| Payload | data yang dibawa frame atau pesan | muatan |
| PDU | Protocol Data Unit: kode fungsi + data | PDU |
| Publisher / subscriber | peran pub/sub | penerbit / pelanggan |
| RID | Runtime Identifier .NET, mis. `linux-arm64` | RID |
| Sans-I/O | logika protokol yang tidak melakukan I/O sendiri | tanpa I/O |
| Server / slave | pihak yang menjawab request | server / slave |
| Simulator | pengganti perangkat di dalam proses | simulator |
| Tap | kait yang menerima setiap frame mentah | penyadap trafik |
| Topic / filter | alamat MQTT / pola subscription dengan `+` dan `#` | topik / filter |
| Transport | link pembawa byte (TCP, serial, in-memory) | transport |
| Unit id | alamat slave Modbus | unit id |
| Universe | 512 kanal DMX | universe |

---
title: DICOM (adapter over fo-dicom)
translation-status: synced
---

# DICOM (adapter over fo-dicom)

**Summary.** DICOM is the standard for medical images: CT, MR, X-ray, ultrasound. Modalities send images to a PACS
with **C-STORE** and check connectivity with **C-ECHO**. DICOM is large and mature, so IoTCom.Net does not
reimplement it. `IoTCom.Net.Adapters.Dicom` wraps [fo-dicom](https://github.com/fo-dicom/fo-dicom) (MS-PL) in the
IoTCom endpoint model: lifecycle, traffic tap, metrics and subscriptions. It also adds a windowed renderer and a
synthetic study generator.

> **Not a medical device.** The renderer is for previews and pipelines, not for diagnostic display. Synthetic studies
> are schematic phantoms with a planted finding; they are not real anatomy.

## When to use it

- Receiving images from a modality or a router at the edge, for example to forward them, anonymise them or run an AI
  pre-read.
- Sending studies to a PACS or an AI service from a gateway.
- Testing DICOM pipelines and AI prompts with deterministic images whose correct answer is known.

## Roles

| Role | Type |
|---|---|
| Storage SCP (server / subscriber) | `DicomStoreServer` — C-STORE and C-ECHO, `ImageReceived`, `ReceiveAsync`, `SubscribeAsync("CT")` (filter by modality) |
| Storage SCU (client) | `DicomStoreClient` — `EchoAsync`, `StoreAsync` (throws `DeviceException` on a failure status) |
| Rendering | `DicomRenderer.Render(dataset, window)` → `GrayImage` → `ToPng()`; `DicomRenderer.Presets` |
| Synthetic studies | `SyntheticImaging.Generate(modality, finding, …)` — chest CT, brain MR, chest X-ray |

## Installation

```bash
dotnet add package IoTCom.Net.Adapters.Dicom --prerelease
```

This package is separate from the `IoTCom.Net` meta-package because fo-dicom is not trimmable or NativeAOT-compatible.

## Quickstart

```csharp
using IoTCom.Net.Adapters.Dicom;

await using var pacs = DicomStoreServer.Create(o => { o.Port = 11112; o.AeTitle = "EDGE-PACS"; });
pacs.ImageReceived += r =>
{
    Console.WriteLine($"{r.Modality} {r.StudyDescription} for {r.PatientName} from {r.CallingAe}");
    File.WriteAllBytes($"{r.SopInstanceUid}.png", DicomRenderer.Render(r.File.Dataset).ToPng());
};
await pacs.StartAsync();

await using var modality = DicomStoreClient.Create(o => { o.Port = 11112; o.CalledAe = "EDGE-PACS"; });
await modality.EchoAsync();
await modality.StoreAsync(await FellowOakDicom.DicomFile.OpenAsync("image.dcm"));
```

## Rendering and windowing

`Render` handles 8- and 16-bit signed and unsigned pixel data and applies the rescale slope and intercept (Hounsfield
units for CT). It inverts MONOCHROME1 images and applies a VOI window: the one you pass, otherwise the dataset's
window, otherwise min/max. Compressed transfer syntaxes and colour images throw `NotSupportedException`; decode them
with fo-dicom codecs first.

| Preset | Center / width |
|---|---|
| `CT lung` | −600 / 1500 |
| `CT mediastinum` | 40 / 400 |
| `CT bone` | 400 / 1800 |
| `CT brain` | 40 / 80 |

## Synthetic studies

`SyntheticImaging.Generate(modality, finding, patientName, patientId, seed)` returns a valid DICOM file with
schematic anatomy and an optional planted finding. Use `FindingsFor(modality)` to list the findings that each
modality supports:

| Modality | Findings |
|---|---|
| Chest CT (512², HU) | lung nodule, pneumothorax, consolidation, pleural effusion |
| Brain MR (256²) | mass, infarct |
| Chest X-ray (768²) | lung nodule, pneumothorax, consolidation, cardiomegaly, pleural effusion |

Every modality also supports `None` (a normal study). `ImageComments` marks the object as SYNTHETIC and records the
ground truth, so an AI pre-read can be scored automatically (see the [medical AI guide](../guides/medical-ai.md)).

```bash
iotcom dicom listen --output received/            # Storage SCP saving .dcm + .png preview
iotcom dicom send --synthetic ct --finding Pneumothorax
iotcom dicom echo --port 11112 --aec ANY-SCP
```

## Testing and interoperability

Tests run C-ECHO and C-STORE over real TCP, render synthetic studies and check that every modality/finding pair
produces a valid object. fo-dicom itself is tested against the DICOM conformance suite and many vendors' PACS.

## Security

DICOM associations are unauthenticated except for AE titles, and DICOM headers contain personal health
information. Restrict the SCP to known AE titles (`AcceptAnyCalledAe = false`) and to clinical networks or a VPN.
Never send identifiable studies to an external AI service without a data-processing agreement and de-identification.

## Limitations

Storage only (C-STORE, C-ECHO). Query/retrieve (C-FIND, C-MOVE), worklists and DICOMweb are available directly in
fo-dicom. The renderer shows one frame and grayscale only.

## Learn more

Notebook `notebooks/medical/05-hl7-dicom.en.ipynb` · Gallery demo *Imaging AI pre-read* · [HL7 v2](hl7.md) ·
[Medical AI guide](../guides/medical-ai.md) · `iotcom dicom --help`

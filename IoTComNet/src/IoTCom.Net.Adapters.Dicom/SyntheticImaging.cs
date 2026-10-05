using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

namespace IoTCom.Net.Adapters.Dicom;

/// <summary>Synthetic study types.</summary>
public enum SyntheticModality
{
    /// <summary>Axial chest CT (512×512, Hounsfield units).</summary>
    ChestCt,
    /// <summary>Axial brain MRI, T2-weighted look (256×256).</summary>
    BrainMr,
    /// <summary>PA chest radiograph (768×768).</summary>
    ChestXray,
}

/// <summary>A finding planted into a synthetic image (the ground truth for evaluating analysis pipelines).</summary>
public enum SyntheticFinding
{
    /// <summary>No abnormality.</summary>
    None,
    /// <summary>Solitary pulmonary nodule (CT, X-ray).</summary>
    LungNodule,
    /// <summary>Right-sided pneumothorax (CT, X-ray).</summary>
    Pneumothorax,
    /// <summary>Lobar consolidation (CT, X-ray).</summary>
    Consolidation,
    /// <summary>Enlarged cardiac silhouette (X-ray).</summary>
    Cardiomegaly,
    /// <summary>Pleural effusion with blunted costophrenic angle (X-ray).</summary>
    PleuralEffusion,
    /// <summary>Enhancing mass with surrounding edema (MRI).</summary>
    BrainMass,
    /// <summary>Wedge-shaped territorial infarct (MRI).</summary>
    Infarct,
}

/// <summary>A generated study and its ground truth.</summary>
/// <param name="File">The DICOM file (Secondary Capture–style dataset with real modality tags).</param>
/// <param name="Modality">Synthetic modality.</param>
/// <param name="Finding">Planted finding.</param>
/// <param name="FindingDescription">Human description of the planted finding (ground truth).</param>
public sealed record SyntheticStudy(DicomFile File, SyntheticModality Modality, SyntheticFinding Finding, string FindingDescription);

/// <summary>
/// Procedural phantom generator for imaging pipelines (PACS routing, viewers, AI triage). The anatomy is schematic:
/// these images are for software testing and demos only and must never be used for training or validating
/// diagnostic models intended for patients.
/// </summary>
public static class SyntheticImaging
{
    private static readonly decimal[] PixelSpacing = [0.7m, 0.7m];

    /// <summary>Findings that make sense for a modality.</summary>
    public static IReadOnlyList<SyntheticFinding> FindingsFor(SyntheticModality m) => m switch
    {
        SyntheticModality.ChestCt => [SyntheticFinding.None, SyntheticFinding.LungNodule, SyntheticFinding.Pneumothorax, SyntheticFinding.Consolidation],
        SyntheticModality.ChestXray => [SyntheticFinding.None, SyntheticFinding.LungNodule, SyntheticFinding.Cardiomegaly, SyntheticFinding.PleuralEffusion, SyntheticFinding.Consolidation],
        _ => [SyntheticFinding.None, SyntheticFinding.BrainMass, SyntheticFinding.Infarct],
    };

    /// <summary>Generates a study.</summary>
    public static SyntheticStudy Generate(SyntheticModality modality, SyntheticFinding finding, string patientName = "SYNTHETIC^PATIENT", string patientId = "SYN-0001", int seed = 7)
    {
        var rng = new Random(seed);
        var (pixels, size, desc, signed, window) = modality switch
        {
            SyntheticModality.ChestCt => ChestCt(finding, rng),
            SyntheticModality.BrainMr => BrainMr(finding, rng),
            _ => ChestXray(finding, rng),
        };
        var file = BuildFile(modality, pixels, size, signed, window, patientName, patientId, desc);
        return new SyntheticStudy(file, modality, finding, desc);
    }

    private static DicomFile BuildFile(SyntheticModality modality, short[] pixels, int size, bool signed, (double Center, double Width) window,
        string patientName, string patientId, string findingDescription)
    {
        var (mod, body, description, sopClass) = modality switch
        {
            SyntheticModality.ChestCt => ("CT", "CHEST", "CT CHEST AXIAL (SYNTHETIC)", DicomUID.CTImageStorage),
            SyntheticModality.BrainMr => ("MR", "HEAD", "MR BRAIN T2 AXIAL (SYNTHETIC)", DicomUID.MRImageStorage),
            _ => ("DX", "CHEST", "XR CHEST PA (SYNTHETIC)", DicomUID.DigitalXRayImageStorageForPresentation),
        };
        var studyUid = DicomUIDGenerator.GenerateDerivedFromUUID();
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, sopClass },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.PatientName, patientName },
            { DicomTag.PatientID, patientId },
            { DicomTag.Modality, mod },
            { DicomTag.BodyPartExamined, body },
            { DicomTag.StudyDescription, description },
            { DicomTag.SeriesDescription, description },
            { DicomTag.StudyDate, DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) },
            { DicomTag.StudyTime, DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture) },
            { DicomTag.Manufacturer, "IoTCom.Net SyntheticImaging" },
            { DicomTag.InstitutionName, "Synthetic data - not for clinical use" },
            { DicomTag.ImageComments, "SYNTHETIC PHANTOM. Ground truth: " + findingDescription },
            { DicomTag.BurnedInAnnotation, "NO" },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, PhotometricInterpretation.Monochrome2.Value },
            { DicomTag.Rows, (ushort)size },
            { DicomTag.Columns, (ushort)size },
            { DicomTag.BitsAllocated, (ushort)16 },
            { DicomTag.BitsStored, (ushort)16 },
            { DicomTag.HighBit, (ushort)15 },
            { DicomTag.PixelRepresentation, (ushort)(signed ? 1 : 0) },
            { DicomTag.WindowCenter, (decimal)window.Center },
            { DicomTag.WindowWidth, (decimal)window.Width },
            { DicomTag.RescaleIntercept, 0m },
            { DicomTag.RescaleSlope, 1m },
            { DicomTag.PixelSpacing, PixelSpacing },
        };
        var bytes = new byte[pixels.Length * 2];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        var pixelData = DicomPixelData.Create(ds, true);
        pixelData.AddFrame(new MemoryByteBuffer(bytes));
        return new DicomFile(ds);
    }

    // ---- phantoms ------------------------------------------------------------------------------------------

    private static (short[], int, string, bool, (double, double)) ChestCt(SyntheticFinding finding, Random rng)
    {
        const int n = 512;
        var img = Fill(n, -1000);
        Ellipse(img, n, 256, 270, 225, 170, -100);                 // subcutaneous fat
        Ellipse(img, n, 256, 270, 212, 158, 40);                   // soft tissue / muscle
        for (var r = 0; r < 9; r++)                                 // ribs
        {
            var a = Math.PI * (0.15 + r * 0.085);
            Disc(img, n, 256 + 200 * Math.Cos(a), 270 - 145 * Math.Sin(a) + 30, 7, 650);
            Disc(img, n, 256 - 200 * Math.Cos(a), 270 - 145 * Math.Sin(a) + 30, 7, 650);
        }
        var collapse = finding == SyntheticFinding.Pneumothorax;
        if (collapse)
        {
            Ellipse(img, n, 165, 255, 80, 120, -1000);             // pleural air: black, no markings
            Ellipse(img, n, 196, 255, 49, 84, -300);               // visceral pleural line (drawn as a thin rim)
            Ellipse(img, n, 196, 255, 47, 82, -780);               // collapsed, denser right lung
        }
        else
        {
            Ellipse(img, n, 165, 255, 78, 118, -850);              // right lung (image left)
        }
        Ellipse(img, n, 347, 255, 78, 118, -850);                  // left lung
        Ellipse(img, n, 268, 248, 58, 62, 45);                     // heart / mediastinum
        Disc(img, n, 256, 215, 14, 30);                            // aorta
        Disc(img, n, 256, 372, 26, 700);                           // vertebral body
        Disc(img, n, 256, 372, 13, 300);
        Disc(img, n, 256, 178, 9, -950);                           // trachea
        var desc = "No planted abnormality.";
        switch (finding)
        {
            case SyntheticFinding.LungNodule:
                Disc(img, n, 150, 210, 11, 35);
                desc = "Solitary ~15 mm soft-tissue nodule in the right upper lung (image left).";
                break;
            case SyntheticFinding.Pneumothorax:
                desc = "Large right-sided pneumothorax: lateral pleural air without lung markings, visible visceral pleural line, partially collapsed right lung.";
                break;
            case SyntheticFinding.Consolidation:
                Blob(img, n, 345, 315, 46, 34, 25, rng);
                for (var b = 0; b < 4; b++)                         // air bronchograms: dark branching tubes inside
                {
                    var ang = 0.35 + b * 0.32;
                    for (var s = 0; s < 46; s++)
                        Disc(img, n, 318 + Math.Cos(ang) * s, 296 + Math.Sin(ang) * s, 1.6, -900);
                }
                desc = "Left lower lobe consolidation (image right): soft-tissue attenuation with air bronchograms.";
                break;
        }
        Vessels(img, n, 212, 252, -1, 14, 95, rng, aeratedMin: -950, aeratedMax: -700);   // right hilum → periphery
        Vessels(img, n, 302, 252, +1, 14, 95, rng, aeratedMin: -950, aeratedMax: -700);   // left hilum → periphery
        Blur(img, n, 1);
        Noise(img, n, 12, rng);
        return (img, n, desc, true, (-600, 1500));
    }

    private static (short[], int, string, bool, (double, double)) BrainMr(SyntheticFinding finding, Random rng)
    {
        const int n = 256;
        var img = Fill(n, 0);
        Ellipse(img, n, 128, 130, 98, 116, 260);                   // scalp
        Ellipse(img, n, 128, 130, 90, 108, 60);                    // skull (dark on T2)
        Ellipse(img, n, 128, 130, 84, 102, 900);                   // CSF rim (bright)
        Ellipse(img, n, 128, 130, 80, 98, 520);                    // grey matter
        Ellipse(img, n, 128, 130, 64, 82, 380);                    // white matter
        Ellipse(img, n, 110, 122, 9, 26, 1000);                    // lateral ventricles
        Ellipse(img, n, 146, 122, 9, 26, 1000);
        Disc(img, n, 128, 168, 6, 980);                            // 4th ventricle-ish
        var desc = "No planted abnormality.";
        switch (finding)
        {
            case SyntheticFinding.BrainMass:
                Blob(img, n, 170, 100, 30, 26, 700, rng);          // edema
                Disc(img, n, 170, 100, 13, 950);                   // mass
                Disc(img, n, 170, 100, 6, 450);                    // necrotic core
                desc = "Left frontal-parietal (image right) ~25 mm mass with central necrosis and surrounding T2-bright edema.";
                break;
            case SyntheticFinding.Infarct:
                Wedge(img, n, 128, 130, -2.6, -2.0, 30, 78, 820);
                desc = "Wedge-shaped T2 hyperintensity in the right MCA territory (image left), consistent with an infarct pattern.";
                break;
        }
        Blur(img, n, 1);
        Noise(img, n, 18, rng, floor: 0);
        return (img, n, desc, false, (500, 1000));
    }

    private static (short[], int, string, bool, (double, double)) ChestXray(SyntheticFinding finding, Random rng)
    {
        const int n = 768;
        var img = Fill(n, 300);                                    // background (bright = attenuating)
        Ellipse(img, n, 384, 420, 330, 360, 1700);                 // thorax soft tissue
        var effusion = finding == SyntheticFinding.PleuralEffusion;
        Ellipse(img, n, 255, 380, 115, 230, 700);                  // right lung (image left), dark = air
        Ellipse(img, n, 515, 380, 115, 230, 700);                  // left lung
        var heartWidth = finding == SyntheticFinding.Cardiomegaly ? 225 : 110;   // CTR ≈ 0.68 vs ≈ 0.33
        Ellipse(img, n, 410, 470, heartWidth, 115, 1900);          // cardiac silhouette
        Ellipse(img, n, 384, 300, 42, 200, 2000);                  // mediastinum / spine
        for (var r = 0; r < 9; r++)                                // posterior ribs, curving down laterally
        {
            Rib(img, n, 384 - 40, 175 + r * 46, -1, 250, 1450);
            Rib(img, n, 384 + 40, 175 + r * 46, +1, 250, 1450);
        }
        Rib(img, n, 384 - 30, 150, -1, 190, 2000);                 // clavicles
        Rib(img, n, 384 + 30, 150, +1, 190, 2000);
        for (var k = 0; k < 120; k++)                              // vascular markings, denser near the hila
        {
            var right = k % 2 == 0;
            var x = (right ? 300 : 468) + (right ? -1 : 1) * Math.Abs(rng.NextDouble() - rng.NextDouble()) * 190;
            var y = 380 + (rng.NextDouble() - 0.5) * 380;
            if (img[(int)y * n + (int)x] < 1000) Disc(img, n, x, y, 1.5 + rng.NextDouble() * 2.5, 950);
        }
        var desc = "No planted abnormality.";
        switch (finding)
        {
            case SyntheticFinding.LungNodule:
                Disc(img, n, 250, 290, 16, 1650);
                desc = "Round ~2 cm opacity in the right upper zone (image left).";
                break;
            case SyntheticFinding.Cardiomegaly:
                desc = "Enlarged cardiac silhouette, cardiothoracic ratio well above 0.5.";
                break;
            case SyntheticFinding.PleuralEffusion:
                desc = "Left pleural effusion (image right): homogeneous opacity at the left base with a blunted costophrenic angle.";
                break;
            case SyntheticFinding.Consolidation:
                Blob(img, n, 250, 480, 70, 55, 1550, rng);
                desc = "Patchy airspace opacity in the right lower zone (image left), consistent with consolidation.";
                break;
        }
        if (effusion) Meniscus(img, n, 515, 380, 115, 230, 1750);
        Blur(img, n, 2);
        Noise(img, n, 15, rng, floor: 0);
        return (img, n, desc, false, (1200, 1800));
    }

    // ---- drawing primitives -----------------------------------------------------------------------------------

    private static short[] Fill(int n, short v)
    {
        var a = new short[n * n];
        Array.Fill(a, v);
        return a;
    }

    private static void Ellipse(short[] a, int n, double cx, double cy, double rx, double ry, short v)
    {
        for (var y = (int)Math.Max(0, cy - ry); y < Math.Min(n, cy + ry + 1); y++)
            for (var x = (int)Math.Max(0, cx - rx); x < Math.Min(n, cx + rx + 1); x++)
            {
                var dx = (x - cx) / rx;
                var dy = (y - cy) / ry;
                if (dx * dx + dy * dy <= 1) a[y * n + x] = v;
            }
    }

    private static void Disc(short[] a, int n, double cx, double cy, double r, short v) => Ellipse(a, n, cx, cy, r, r, v);

    private static void Crescent(short[] a, int n, double cx, double cy, double rx, double ry, double irx, double iry, short v)
    {
        for (var y = (int)Math.Max(0, cy - ry); y < Math.Min(n, cy + ry + 1); y++)
            for (var x = (int)Math.Max(0, cx - rx); x < Math.Min(n, cx + rx + 1); x++)
            {
                double Q(double ex, double ey) => ((x - cx) / ex) * ((x - cx) / ex) + ((y - cy) / ey) * ((y - cy) / ey);
                if (Q(rx, ry) <= 1 && Q(irx, iry) > 1) a[y * n + x] = v;
            }
    }

    private static void Blob(short[] a, int n, double cx, double cy, double rx, double ry, short v, Random rng)
    {
        for (var k = 0; k < 14; k++)
        {
            var ox = (rng.NextDouble() - 0.5) * rx;
            var oy = (rng.NextDouble() - 0.5) * ry;
            Ellipse(a, n, cx + ox, cy + oy, rx * (0.4 + rng.NextDouble() * 0.4), ry * (0.4 + rng.NextDouble() * 0.4), v);
        }
    }

    private static void Wedge(short[] a, int n, double cx, double cy, double a0, double a1, double r0, double r1, short v)
    {
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var r = Math.Sqrt(dx * dx + dy * dy);
                var ang = Math.Atan2(dy, dx);
                if (r >= r0 && r <= r1 && ang >= a0 && ang <= a1 && a[y * n + x] is > 300 and < 600) a[y * n + x] = v;
            }
    }

    /// <summary>
    /// Branching, tapering vessel trees from a hilum (hx, hy) towards the periphery (dir ±1). Only aerated lung pixels
    /// (between <paramref name="aeratedMin"/> and <paramref name="aeratedMax"/>) are painted, so pleural air stays empty.
    /// </summary>
    private static void Vessels(short[] a, int n, double hx, double hy, int dir, int trees, double length, Random rng, short aeratedMin, short aeratedMax)
    {
        void Branch(double x, double y, double angle, double len, double width, int depth)
        {
            var steps = (int)len;
            for (var s = 0; s < steps; s++)
            {
                angle += (rng.NextDouble() - 0.5) * 0.12;
                x += Math.Cos(angle);
                y += Math.Sin(angle);
                var w = width * (1 - 0.6 * s / steps);
                for (var oy = -2; oy <= 2; oy++)
                    for (var ox = -2; ox <= 2; ox++)
                    {
                        if (ox * ox + oy * oy > w * w) continue;
                        int px = (int)(x + ox), py = (int)(y + oy);
                        if (px < 0 || py < 0 || px >= n || py >= n) continue;
                        var idx = py * n + px;
                        if (a[idx] >= aeratedMin && a[idx] <= aeratedMax) a[idx] = (short)(-150 - 250 * s / steps);
                    }
                if (depth < 3 && s == steps / 2) Branch(x, y, angle + (rng.NextDouble() < 0.5 ? -0.5 : 0.5), len * 0.6, w, depth + 1);
            }
        }
        for (var t = 0; t < trees; t++)
        {
            var spread = (t / (double)(trees - 1) - 0.5) * 2.6;   // fan from −75° to +75°
            var baseAngle = dir > 0 ? spread : Math.PI - spread;
            Branch(hx, hy, baseAngle, length * (0.6 + rng.NextDouble() * 0.5), 2.2, 0);
        }
    }

    /// <summary>A rib-like band starting at (x0, y0) running laterally (dir ±1), rising slightly then curving down.</summary>
    private static void Rib(short[] a, int n, double x0, double y0, int dir, double length, short v)
    {
        for (var i = 0; i < length; i++)
        {
            var t = i / length;
            var x = (int)(x0 + dir * i);
            var y = (int)(y0 - 22 * t + 95 * t * t);
            if (x < 0 || x >= n) continue;
            for (var k = -5; k <= 5; k++)
            {
                var yy = y + k;
                if (yy < 0 || yy >= n) continue;
                var idx = yy * n + x;
                if (a[idx] < v) a[idx] = (short)(a[idx] + (v - a[idx]) * (1 - Math.Abs(k) / 6.0) * 0.55);
            }
        }
    }

    /// <summary>Fluid layering at the base of a lung with a meniscus rising laterally.</summary>
    private static void Meniscus(short[] a, int n, double cx, double cy, double rx, double ry, short v)
    {
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var dx = (x - cx) / rx;
                var dy = (y - cy) / ry;
                if (dx * dx + dy * dy > 1.05) continue;
                var lateral = Math.Clamp((x - (cx - rx)) / (2 * rx), 0, 1);
                var surface = cy + ry * 0.42 - 70 * lateral * lateral; // meniscus: fluid climbs the lateral chest wall
                if (y >= surface && a[y * n + x] < 1200) a[y * n + x] = v; // only replaces aerated lung, not the heart
            }
    }

    private static void Noise(short[] a, int n, double sd, Random rng, short floor = short.MinValue)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var g = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * rng.NextDouble());
            a[i] = (short)Math.Clamp(a[i] + g * sd, floor, short.MaxValue);
        }
    }

    private static void Blur(short[] a, int n, int radius)
    {
        var tmp = new short[a.Length];
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                int sum = 0, count = 0;
                for (var k = -radius; k <= radius; k++)
                    if (x + k is >= 0 && x + k < n) { sum += a[y * n + x + k]; count++; }
                tmp[y * n + x] = (short)(sum / count);
            }
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                int sum = 0, count = 0;
                for (var k = -radius; k <= radius; k++)
                    if (y + k is >= 0 && y + k < n) { sum += tmp[(y + k) * n + x]; count++; }
                a[y * n + x] = (short)(sum / count);
            }
    }
}

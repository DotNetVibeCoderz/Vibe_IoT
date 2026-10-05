using System.Buffers.Binary;
using System.IO.Compression;
using FellowOakDicom;
using FellowOakDicom.Imaging;

namespace IoTCom.Net.Adapters.Dicom;

/// <summary>An 8-bit grayscale rendering.</summary>
/// <param name="Width">Columns.</param>
/// <param name="Height">Rows.</param>
/// <param name="Pixels">Row-major 8-bit luminance.</param>
public sealed record GrayImage(int Width, int Height, byte[] Pixels)
{
    /// <summary>Encodes as PNG (8-bit grayscale).</summary>
    public byte[] ToPng() => PngEncoder.EncodeGray8(Width, Height, Pixels);
}

/// <summary>
/// Renders single-frame monochrome DICOM images (8/16-bit, signed or unsigned, uncompressed) to grayscale with the
/// VOI LUT window (center/width) and the modality rescale. Dependency-free (no imaging codec packages needed).
/// </summary>
public static class DicomRenderer
{
    /// <summary>Common window presets (center, width).</summary>
    public static IReadOnlyDictionary<string, (double Center, double Width)> Presets { get; } = new Dictionary<string, (double, double)>
    {
        ["CT lung"] = (-600, 1500),
        ["CT mediastinum"] = (40, 400),
        ["CT bone"] = (400, 1800),
        ["CT brain"] = (40, 80),
    };

    /// <summary>Renders frame 0. When <paramref name="window"/> is null the dataset's window (or the full range) is used.</summary>
    /// <exception cref="NotSupportedException">Compressed or colour images.</exception>
    public static GrayImage Render(DicomDataset dataset, (double Center, double Width)? window = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (dataset.InternalTransferSyntax.IsEncapsulated)
            throw new NotSupportedException($"Compressed transfer syntax {dataset.InternalTransferSyntax} is not supported by the built-in renderer; transcode first.");
        var pixelData = DicomPixelData.Create(dataset);
        if (pixelData.SamplesPerPixel != 1) throw new NotSupportedException("Only monochrome images are supported.");
        var w = pixelData.Width;
        var h = pixelData.Height;
        var raw = pixelData.GetFrame(0).Data;
        var slope = (double)dataset.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1m);
        var intercept = (double)dataset.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0m);
        var signed = pixelData.PixelRepresentation == PixelRepresentation.Signed;
        var bits = pixelData.BitsAllocated;

        var values = new double[w * h];
        for (var i = 0; i < values.Length; i++)
        {
            double v = bits switch
            {
                8 => raw[i],
                16 => signed ? BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(i * 2)) : BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2)),
                _ => throw new NotSupportedException($"BitsAllocated {bits} is not supported."),
            };
            values[i] = v * slope + intercept;
        }

        var (center, width) = window ?? DatasetWindow(dataset) ?? FullRange(values);
        var low = center - width / 2;
        var invert = pixelData.PhotometricInterpretation == PhotometricInterpretation.Monochrome1;
        var pixels = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var g = (byte)Math.Clamp((values[i] - low) / width * 255.0, 0, 255);
            pixels[i] = invert ? (byte)(255 - g) : g;
        }
        return new GrayImage(w, h, pixels);
    }

    private static (double, double)? DatasetWindow(DicomDataset ds) =>
        ds.TryGetValue<decimal>(DicomTag.WindowCenter, 0, out var c) && ds.TryGetValue<decimal>(DicomTag.WindowWidth, 0, out var wd) && wd > 0
            ? ((double)c, (double)wd)
            : null;

    private static (double, double) FullRange(double[] v)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var x in v)
        {
            if (x < min) min = x;
            if (x > max) max = x;
        }
        return ((min + max) / 2, Math.Max(1, max - min));
    }
}

/// <summary>Minimal PNG encoder for 8-bit grayscale images.</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes row-major 8-bit grayscale pixels.</summary>
    public static byte[] EncodeGray8(int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length < width * height) throw new ArgumentException("Pixel buffer is smaller than width × height.", nameof(pixels));
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 0;   // grayscale
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(ms, "IHDR"u8, ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(pixels.Slice(y * width, width));
            }
        }
        Chunk(ms, "IDAT"u8, raw.ToArray());
        Chunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        var crc = Update(Update(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}

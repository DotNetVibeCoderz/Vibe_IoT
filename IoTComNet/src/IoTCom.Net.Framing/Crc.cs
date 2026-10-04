namespace IoTCom.Net.Framing;

/// <summary>
/// Rocksoft™ model parameters of a CRC (the same notation as the "Catalogue of parametrised CRC algorithms").
/// </summary>
/// <param name="Name">Catalogue name, e.g. <c>CRC-16/MODBUS</c>.</param>
/// <param name="Width">Register width in bits (8..64).</param>
/// <param name="Polynomial">Generator polynomial, normal (MSB-first) form, without the top bit.</param>
/// <param name="Init">Initial register value (normal form).</param>
/// <param name="ReflectIn">Reflect each input byte.</param>
/// <param name="ReflectOut">Reflect the final register.</param>
/// <param name="XorOut">Value XORed into the final result.</param>
/// <param name="Check">Expected CRC of ASCII <c>"123456789"</c> (used by self tests).</param>
public sealed record CrcParameters(string Name, int Width, ulong Polynomial, ulong Init, bool ReflectIn, bool ReflectOut, ulong XorOut, ulong Check);

/// <summary>
/// Table-driven CRC engine for any width from 8 to 64 bits. Instances are immutable and thread-safe.
/// Use the presets in <see cref="CrcCatalog"/> or build your own from <see cref="CrcParameters"/>.
/// </summary>
public sealed class CrcAlgorithm
{
    private readonly ulong[] _table = new ulong[256];
    private readonly ulong _mask;
    private readonly ulong _init;
    private readonly int _shift;

    /// <summary>Creates an engine for <paramref name="parameters"/>.</summary>
    public CrcAlgorithm(CrcParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Width is < 8 or > 64) throw new ArgumentOutOfRangeException(nameof(parameters), "Width must be between 8 and 64 bits.");
        Parameters = parameters;
        _mask = parameters.Width == 64 ? ulong.MaxValue : (1UL << parameters.Width) - 1;
        _shift = parameters.Width - 8;

        if (parameters.ReflectIn)
        {
            var rpoly = Reflect(parameters.Polynomial, parameters.Width);
            for (ulong i = 0; i < 256; i++)
            {
                var crc = i;
                for (var b = 0; b < 8; b++) crc = (crc & 1) != 0 ? (crc >> 1) ^ rpoly : crc >> 1;
                _table[i] = crc & _mask;
            }
            _init = Reflect(parameters.Init, parameters.Width);
        }
        else
        {
            var top = 1UL << (parameters.Width - 1);
            for (ulong i = 0; i < 256; i++)
            {
                var crc = i << _shift;
                for (var b = 0; b < 8; b++) crc = (crc & top) != 0 ? (crc << 1) ^ parameters.Polynomial : crc << 1;
                _table[i] = crc & _mask;
            }
            _init = parameters.Init & _mask;
        }
    }

    /// <summary>Model parameters.</summary>
    public CrcParameters Parameters { get; }

    /// <summary>Width in bits.</summary>
    public int Width => Parameters.Width;

    /// <summary>Computes the CRC of <paramref name="data"/> in one shot.</summary>
    public ulong Compute(ReadOnlySpan<byte> data) => Finish(Update(Start(), data));

    /// <summary>Returns the initial register for incremental use.</summary>
    public ulong Start() => _init;

    /// <summary>Feeds <paramref name="data"/> into <paramref name="register"/> and returns the new register.</summary>
    public ulong Update(ulong register, ReadOnlySpan<byte> data)
    {
        var crc = register;
        var table = _table;
        if (Parameters.ReflectIn)
        {
            foreach (var b in data) crc = table[(byte)(crc ^ b)] ^ (crc >> 8);
        }
        else
        {
            var shift = _shift;
            var mask = _mask;
            foreach (var b in data) crc = (table[(byte)((crc >> shift) ^ b)] ^ (crc << 8)) & mask;
        }
        return crc;
    }

    /// <summary>Applies output reflection and XOR to a register.</summary>
    public ulong Finish(ulong register)
    {
        var crc = register;
        if (Parameters.ReflectIn != Parameters.ReflectOut) crc = Reflect(crc, Parameters.Width);
        return (crc ^ Parameters.XorOut) & _mask;
    }

    /// <summary>True when <see cref="Compute"/> of <c>"123456789"</c> equals <see cref="CrcParameters.Check"/>.</summary>
    public bool SelfTest() => Compute("123456789"u8) == Parameters.Check;

    /// <inheritdoc />
    public override string ToString() => Parameters.Name;

    internal static ulong Reflect(ulong value, int width)
    {
        ulong r = 0;
        for (var i = 0; i < width; i++)
        {
            r = (r << 1) | (value & 1);
            value >>= 1;
        }
        return r;
    }
}

/// <summary>Well known CRC presets. Every preset passes its catalogue check value.</summary>
public static class CrcCatalog
{
    /// <summary>CRC-8/SMBUS (a.k.a. CRC-8). Used by PMBus/SMBus PEC.</summary>
    public static readonly CrcAlgorithm Crc8 = new(new("CRC-8/SMBUS", 8, 0x07, 0x00, false, false, 0x00, 0xF4));
    /// <summary>CRC-8/MAXIM-DOW. Dallas/Maxim 1-Wire ROM codes.</summary>
    public static readonly CrcAlgorithm Crc8Maxim = new(new("CRC-8/MAXIM-DOW", 8, 0x31, 0x00, true, true, 0x00, 0xA1));
    /// <summary>CRC-8/SAE-J1850. Automotive (AUTOSAR E2E profile 1).</summary>
    public static readonly CrcAlgorithm Crc8SaeJ1850 = new(new("CRC-8/SAE-J1850", 8, 0x1D, 0xFF, false, false, 0xFF, 0x4B));
    /// <summary>CRC-16/ARC (a.k.a. CRC-16/IBM, CRC-16/LHA).</summary>
    public static readonly CrcAlgorithm Crc16Arc = new(new("CRC-16/ARC", 16, 0x8005, 0x0000, true, true, 0x0000, 0xBB3D));
    /// <summary>CRC-16/MODBUS. Modbus RTU.</summary>
    public static readonly CrcAlgorithm Crc16Modbus = new(new("CRC-16/MODBUS", 16, 0x8005, 0xFFFF, true, true, 0x0000, 0x4B37));
    /// <summary>CRC-16/IBM-3740 (CCITT-FALSE).</summary>
    public static readonly CrcAlgorithm Crc16CcittFalse = new(new("CRC-16/IBM-3740", 16, 0x1021, 0xFFFF, false, false, 0x0000, 0x29B1));
    /// <summary>CRC-16/XMODEM.</summary>
    public static readonly CrcAlgorithm Crc16Xmodem = new(new("CRC-16/XMODEM", 16, 0x1021, 0x0000, false, false, 0x0000, 0x31C3));
    /// <summary>CRC-16/KERMIT (CCITT true).</summary>
    public static readonly CrcAlgorithm Crc16Kermit = new(new("CRC-16/KERMIT", 16, 0x1021, 0x0000, true, true, 0x0000, 0x2189));
    /// <summary>CRC-16/IBM-SDLC (X.25). HDLC/PPP FCS-16, DLMS HDLC.</summary>
    public static readonly CrcAlgorithm Crc16X25 = new(new("CRC-16/IBM-SDLC", 16, 0x1021, 0xFFFF, true, true, 0xFFFF, 0x906E));
    /// <summary>CRC-16/MCRF4XX. MAVLink checksum (before CRC_EXTRA).</summary>
    public static readonly CrcAlgorithm Crc16Mcrf4xx = new(new("CRC-16/MCRF4XX", 16, 0x1021, 0xFFFF, true, true, 0x0000, 0x6F91));
    /// <summary>CRC-16/DNP. DNP3 link layer.</summary>
    public static readonly CrcAlgorithm Crc16Dnp = new(new("CRC-16/DNP", 16, 0x3D65, 0x0000, true, true, 0xFFFF, 0xEA82));
    /// <summary>CRC-16/EN-13757. Wireless M-Bus.</summary>
    public static readonly CrcAlgorithm Crc16En13757 = new(new("CRC-16/EN-13757", 16, 0x3D65, 0x0000, false, false, 0xFFFF, 0xC2B7));
    /// <summary>CRC-16/USB.</summary>
    public static readonly CrcAlgorithm Crc16Usb = new(new("CRC-16/USB", 16, 0x8005, 0xFFFF, true, true, 0xFFFF, 0xB4C8));
    /// <summary>CRC-16/MAXIM-DOW.</summary>
    public static readonly CrcAlgorithm Crc16Maxim = new(new("CRC-16/MAXIM-DOW", 16, 0x8005, 0x0000, true, true, 0xFFFF, 0x44C2));
    /// <summary>CRC-24/OPENPGP. RTCM 3 uses CRC-24Q (see <see cref="Crc24Lte"/>).</summary>
    public static readonly CrcAlgorithm Crc24OpenPgp = new(new("CRC-24/OPENPGP", 24, 0x864CFB, 0xB704CE, false, false, 0x000000, 0x21CF02));
    /// <summary>CRC-24/LTE-A (CRC-24Q, RTCM 3 / Qualcomm).</summary>
    public static readonly CrcAlgorithm Crc24Lte = new(new("CRC-24/LTE-A", 24, 0x864CFB, 0x000000, false, false, 0x000000, 0xCDE703));
    /// <summary>CRC-24/BLE. Bluetooth Low Energy link layer.</summary>
    public static readonly CrcAlgorithm Crc24Ble = new(new("CRC-24/BLE", 24, 0x00065B, 0x555555, true, true, 0x000000, 0xC25A56));
    /// <summary>CRC-32/ISO-HDLC (the common "CRC-32"). Same as System.IO.Hashing.Crc32.</summary>
    public static readonly CrcAlgorithm Crc32 = new(new("CRC-32/ISO-HDLC", 32, 0x04C11DB7, 0xFFFFFFFF, true, true, 0xFFFFFFFF, 0xCBF43926));
    /// <summary>CRC-32/ISCSI (CRC-32C, Castagnoli).</summary>
    public static readonly CrcAlgorithm Crc32C = new(new("CRC-32/ISCSI", 32, 0x1EDC6F41, 0xFFFFFFFF, true, true, 0xFFFFFFFF, 0xE3069283));
    /// <summary>CRC-32/MPEG-2. STM32 hardware CRC unit default.</summary>
    public static readonly CrcAlgorithm Crc32Mpeg2 = new(new("CRC-32/MPEG-2", 32, 0x04C11DB7, 0xFFFFFFFF, false, false, 0x00000000, 0x0376E6E7));
    /// <summary>CRC-32/BZIP2.</summary>
    public static readonly CrcAlgorithm Crc32Bzip2 = new(new("CRC-32/BZIP2", 32, 0x04C11DB7, 0xFFFFFFFF, false, false, 0xFFFFFFFF, 0xFC891918));
    /// <summary>CRC-64/XZ.</summary>
    public static readonly CrcAlgorithm Crc64Xz = new(new("CRC-64/XZ", 64, 0x42F0E1EBA9EA3693, ulong.MaxValue, true, true, ulong.MaxValue, 0x995DC9BBDF1939FA));
    /// <summary>CRC-64/ECMA-182.</summary>
    public static readonly CrcAlgorithm Crc64Ecma182 = new(new("CRC-64/ECMA-182", 64, 0x42F0E1EBA9EA3693, 0, false, false, 0, 0x6C40DF5F0B497347));

    /// <summary>All presets.</summary>
    public static IReadOnlyList<CrcAlgorithm> All { get; } =
    [
        Crc8, Crc8Maxim, Crc8SaeJ1850,
        Crc16Arc, Crc16Modbus, Crc16CcittFalse, Crc16Xmodem, Crc16Kermit, Crc16X25, Crc16Mcrf4xx, Crc16Dnp, Crc16En13757, Crc16Usb, Crc16Maxim,
        Crc24OpenPgp, Crc24Lte, Crc24Ble,
        Crc32, Crc32C, Crc32Mpeg2, Crc32Bzip2,
        Crc64Xz, Crc64Ecma182,
    ];

    /// <summary>Finds a preset by catalogue name or common alias (case-insensitive), e.g. <c>"modbus"</c>, <c>"CRC-16/X-25"</c>.</summary>
    public static CrcAlgorithm? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var n = Normalize(name);
        foreach (var a in All)
        {
            var full = Normalize(a.Parameters.Name);
            if (full == n || full.EndsWith("/" + n, StringComparison.Ordinal)) return a;
        }
        return n switch
        {
            "CRC16" or "MODBUS" => Crc16Modbus,
            "X25" or "CRC16/X25" or "HDLC" or "FCS16" => Crc16X25,
            "CCITT" or "CRC16/CCITT" or "CCITT-FALSE" or "CRC16/CCITT-FALSE" => Crc16CcittFalse,
            "MAVLINK" => Crc16Mcrf4xx,
            "CRC32" or "ISO-HDLC" => Crc32,
            "CRC32C" or "CASTAGNOLI" => Crc32C,
            "CRC24Q" or "RTCM" => Crc24Lte,
            "CRC8" => Crc8,
            "ONEWIRE" or "1-WIRE" => Crc8Maxim,
            "CRC64" => Crc64Xz,
            _ => null,
        };

        static string Normalize(string s) => s.Trim().ToUpperInvariant().Replace("CRC-", "CRC", StringComparison.Ordinal).Replace("_", "-", StringComparison.Ordinal);
    }
}

/// <summary>CRC-16 convenience helpers.</summary>
public static class Crc16
{
    /// <summary>Computes CRC-16/MODBUS. Append it to a frame little-endian (low byte first).</summary>
    /// <remarks>Uses the shared table engine, which benchmarks faster than a dedicated 16-bit table (see benchmarks/).</remarks>
    public static ushort Modbus(ReadOnlySpan<byte> data) => (ushort)CrcCatalog.Crc16Modbus.Compute(data);
}

/// <summary>Longitudinal redundancy check (two's complement of the byte sum), used by Modbus ASCII.</summary>
public static class Lrc
{
    /// <summary>Computes the Modbus LRC.</summary>
    public static byte Compute(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        foreach (var b in data) sum += b;
        return (byte)-sum;
    }
}

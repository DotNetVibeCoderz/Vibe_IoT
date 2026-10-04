namespace IoTCom.Net;

/// <summary>Role of a field inside a wire frame. Tools colour fields by kind (the "frame lane").</summary>
public enum FrameFieldKind
{
    /// <summary>Framing header (MBAP, preamble, sync, start char).</summary>
    Header,
    /// <summary>Addressing (unit id, node, universe).</summary>
    Address,
    /// <summary>Function / command / op code.</summary>
    Function,
    /// <summary>Length or count field.</summary>
    Length,
    /// <summary>Payload data.</summary>
    Data,
    /// <summary>Checksum / CRC / LRC.</summary>
    Checksum,
    /// <summary>Delimiter or terminator.</summary>
    Delimiter,
    /// <summary>Error / exception information.</summary>
    Error,
}

/// <summary>A named byte range of a frame.</summary>
/// <param name="Name">Field name, e.g. "Unit", "CRC".</param>
/// <param name="Offset">Start offset in the frame.</param>
/// <param name="Length">Number of bytes.</param>
/// <param name="Kind">Field role.</param>
/// <param name="Value">Decoded value, e.g. "ReadHoldingRegisters".</param>
public readonly record struct FrameField(string Name, int Offset, int Length, FrameFieldKind Kind, string? Value = null);

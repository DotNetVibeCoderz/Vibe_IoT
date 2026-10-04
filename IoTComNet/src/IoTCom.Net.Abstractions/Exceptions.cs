namespace IoTCom.Net;

/// <summary>Base type of every exception thrown by IoTCom.Net.</summary>
public class IoTComException : Exception
{
    /// <summary>Creates an exception.</summary>
    public IoTComException() { }
    /// <summary>Creates an exception with a message.</summary>
    public IoTComException(string message) : base(message) { }
    /// <summary>Creates an exception with a message and inner exception.</summary>
    public IoTComException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>The underlying link failed (socket closed, port unplugged, connect refused...).</summary>
public class TransportException : IoTComException
{
    /// <summary>Creates an exception.</summary>
    public TransportException() { }
    /// <summary>Creates an exception with a message.</summary>
    public TransportException(string message) : base(message) { }
    /// <summary>Creates an exception with a message and inner exception.</summary>
    public TransportException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>The peer sent bytes that violate the protocol (bad checksum, malformed frame, unexpected response).</summary>
public class ProtocolException : IoTComException
{
    /// <summary>Creates an exception.</summary>
    public ProtocolException() { }
    /// <summary>Creates an exception with a message.</summary>
    public ProtocolException(string message) : base(message) { }
    /// <summary>Creates an exception with a message and inner exception.</summary>
    public ProtocolException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>The peer did not answer within the configured timeout.</summary>
public class IoTComTimeoutException : IoTComException
{
    /// <summary>Creates an exception.</summary>
    public IoTComTimeoutException() { }
    /// <summary>Creates an exception with a message.</summary>
    public IoTComTimeoutException(string message) : base(message) { }
    /// <summary>Creates an exception with a message and inner exception.</summary>
    public IoTComTimeoutException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>The device answered with a protocol-level error (e.g. Modbus exception, UDS negative response).</summary>
public class DeviceException : IoTComException
{
    /// <summary>Creates an exception.</summary>
    public DeviceException() { }
    /// <summary>Creates an exception with a message.</summary>
    public DeviceException(string message) : base(message) { }
    /// <summary>Creates an exception with a message and inner exception.</summary>
    public DeviceException(string message, Exception? inner) : base(message, inner) { }
    /// <summary>Creates an exception with a protocol-specific error code.</summary>
    public DeviceException(string message, int code) : base(message) => Code = code;

    /// <summary>Protocol-specific error code (preserved verbatim from the device).</summary>
    public int Code { get; }
}

/// <summary>A write/actuation was attempted on an endpoint configured as read-only.</summary>
public class ReadOnlyModeException : IoTComException
{
    /// <summary>Creates an exception.</summary>
    public ReadOnlyModeException() : base("The endpoint is in read-only mode. Enable writes explicitly to change device state.") { }
    /// <summary>Creates an exception with a message.</summary>
    public ReadOnlyModeException(string message) : base(message) { }
    /// <summary>Creates an exception with a message and inner exception.</summary>
    public ReadOnlyModeException(string message, Exception? inner) : base(message, inner) { }
}

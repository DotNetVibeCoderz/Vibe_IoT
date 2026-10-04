using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IoTCom.Net;

/// <summary>
/// OpenTelemetry-ready instrumentation. Subscribe with
/// <c>.AddSource("IoTCom.Net")</c> (tracing) and <c>.AddMeter("IoTCom.Net")</c> (metrics).
/// </summary>
public static class IoTComDiagnostics
{
    /// <summary>Name of the <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string SourceName = "IoTCom.Net";

    /// <summary>Activity source for request/response operations.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, IoTComInfo.Version);

    /// <summary>Meter for all IoTCom.Net instruments.</summary>
    public static readonly Meter Meter = new(SourceName, IoTComInfo.Version);

    /// <summary>Frames received.</summary>
    public static readonly Counter<long> FramesIn = Meter.CreateCounter<long>("iotcom.frames.in", "{frame}", "Frames received");
    /// <summary>Frames sent.</summary>
    public static readonly Counter<long> FramesOut = Meter.CreateCounter<long>("iotcom.frames.out", "{frame}", "Frames sent");
    /// <summary>Bytes received.</summary>
    public static readonly Counter<long> BytesIn = Meter.CreateCounter<long>("iotcom.bytes.in", "By", "Bytes received");
    /// <summary>Bytes sent.</summary>
    public static readonly Counter<long> BytesOut = Meter.CreateCounter<long>("iotcom.bytes.out", "By", "Bytes sent");
    /// <summary>Protocol, transport and device errors.</summary>
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("iotcom.errors", "{error}", "Errors");
    /// <summary>Reconnect attempts.</summary>
    public static readonly Counter<long> Reconnects = Meter.CreateCounter<long>("iotcom.reconnects", "{attempt}", "Reconnect attempts");
    /// <summary>Request/response round-trip latency.</summary>
    public static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("iotcom.request.duration", "ms", "Request round-trip time");
}

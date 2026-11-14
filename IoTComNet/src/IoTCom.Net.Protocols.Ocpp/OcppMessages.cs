using System.Text.Json.Serialization;

namespace IoTCom.Net.Protocols.Ocpp;

/// <summary>
/// A typed OCPP request whose answer is <typeparamref name="TResponse"/>. The action name, version and command flag are
/// explicit interface members, so they never appear in the JSON payload.
/// </summary>
/// <typeparam name="TResponse">The confirmation/response type.</typeparam>
public interface IOcppRequest<TResponse> where TResponse : class
{
    /// <summary>Action name, e.g. <c>BootNotification</c>.</summary>
    string Action { get; }

    /// <summary>Version this message belongs to.</summary>
    OcppVersion Version { get; }

    /// <summary>
    /// True when the request changes the state of a charger or a vehicle's charging (remote start/stop, reset, unlock,
    /// configuration, charging limits). A CSMS refuses these in read-only mode.
    /// </summary>
    bool IsCommand { get; }
}

/// <summary>Accepted/Rejected style result shared by several messages.</summary>
public enum OcppGenericStatus
{
    /// <summary>Accepted.</summary>
    Accepted,
    /// <summary>Rejected.</summary>
    Rejected,
}

/// <summary>One sampled value (both versions use the same names for the measurands).</summary>
public static class OcppMeasurand
{
    /// <summary>Energy delivered to the EV, Wh register.</summary>
    public const string EnergyActiveImportRegister = "Energy.Active.Import.Register";
    /// <summary>Instantaneous power to the EV, W.</summary>
    public const string PowerActiveImport = "Power.Active.Import";
    /// <summary>Instantaneous current to the EV, A.</summary>
    public const string CurrentImport = "Current.Import";
    /// <summary>Voltage, V.</summary>
    public const string Voltage = "Voltage";
    /// <summary>State of charge, percent.</summary>
    public const string SoC = "SoC";
    /// <summary>Temperature, Celsius.</summary>
    public const string Temperature = "Temperature";
}

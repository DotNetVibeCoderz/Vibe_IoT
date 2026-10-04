using System.Reflection;

namespace IoTCom.Net;

/// <summary>Product information shown by tools, samples and the Gallery.</summary>
public static class IoTComInfo
{
    /// <summary>Product name.</summary>
    public const string Product = "IoTCom.Net";

    /// <summary>Credit line (English).</summary>
    public const string CreditEn = "Built by Gravicode Studios, led by Kang Fadhil";

    /// <summary>Credit line (Bahasa Indonesia).</summary>
    public const string CreditId = "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil";

    /// <summary>Informational version of IoTCom.Net (without build metadata).</summary>
    public static string Version { get; } =
        typeof(IoTComInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";
}

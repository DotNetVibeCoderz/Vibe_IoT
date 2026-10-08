using Opc.Ua;

namespace IoTCom.Net.Adapters.OpcUa;

/// <summary>Builds OPC Foundation application configurations with a directory PKI under one root.</summary>
internal static class OpcUaApplication
{
    /// <summary>Default PKI root: %LOCALAPPDATA%/IoTCom.Net/opcua/pki (~/.local/share on Linux).</summary>
    public static string DefaultPkiRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IoTCom.Net", "opcua", "pki");

    public static ITelemetryContext Telemetry { get; } = DefaultTelemetry.Create(_ => { });

    public static ApplicationConfiguration Create(ApplicationType type, string name, string uri, string? pkiRoot, bool autoAccept)
    {
        var root = pkiRoot ?? DefaultPkiRoot;
        var config = new ApplicationConfiguration
        {
            ApplicationName = name,
            ApplicationUri = uri,
            ApplicationType = type,
            ProductUri = "https://github.com/DotNetVibeCoderz/Vibe_IoT/IoTComNet",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(root, "own"),
                    SubjectName = $"CN={name}, O=Gravicode Studios, DC={System.Net.Dns.GetHostName()}",
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(root, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(root, "issuer") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(root, "rejected") },
                AutoAcceptUntrustedCertificates = autoAccept,
                AddAppCertToTrustedStore = true,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000, MaxMessageSize = 4 * 1024 * 1024 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
        };
        return config;
    }
}

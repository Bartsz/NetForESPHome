using Makaretu.Dns;

namespace NetForESPHome.ServerMock;

/// <summary>Advertises the mock server using the ESPHome _esphomelib._tcp DNS-SD service and standard TXT metadata.</summary>
internal sealed class EspHomeMdnsAdvertiser : IDisposable
{
    private readonly ServiceDiscovery _serviceDiscovery;
    private readonly ServiceProfile _serviceProfile;
    private bool _disposed;

    /// <summary>Creates and immediately announces an ESPHome DNS-SD profile for the supplied listening port.</summary>
    public EspHomeMdnsAdvertiser(EspHomeMockServerConfiguration configuration, int port)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (port is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port), port, "mDNS service port must be between 1 and 65535.");

        var serviceProfile = new ServiceProfile(configuration.Name, "_esphomelib._tcp", (ushort)port);
        NormalizeHostName(serviceProfile, configuration.Name);
        AddEspHomeProperties(serviceProfile, configuration);

        var serviceDiscovery = new ServiceDiscovery();
        _serviceProfile = serviceProfile;
        _serviceDiscovery = serviceDiscovery;

        try
        {
            serviceDiscovery.Advertise(serviceProfile);
            serviceDiscovery.Announce(serviceProfile);
        }
        catch
        {
            serviceDiscovery.Dispose();
            throw;
        }
    }

    /// <summary>Uses the same node.local target ESPHome advertises instead of the library's service-derived default hostname.</summary>
    private static void NormalizeHostName(ServiceProfile profile, string nodeName)
    {
        DomainName hostName = $"{nodeName}.local";
        profile.HostName = hostName;

        foreach (var resource in profile.Resources)
        {
            if (resource is SRVRecord serviceRecord)
            {
                serviceRecord.Target = hostName;
                continue;
            }

            if (resource is TXTRecord textRecord)
            {
                textRecord.Strings.Clear();
                continue;
            }

            resource.Name = hostName;
        }
    }

    /// <summary>Adds the TXT fields ESPHome discovery consumers use for device identity and API encryption detection.</summary>
    private static void AddEspHomeProperties(ServiceProfile profile, EspHomeMockServerConfiguration configuration)
    {
        profile.AddProperty("friendly_name", configuration.FriendlyName);
        profile.AddProperty("version", configuration.EspHomeVersion);
        if (configuration.ConfigHash.Length > 0) profile.AddProperty("config_hash", configuration.ConfigHash);
        profile.AddProperty("mac", configuration.MacAddress.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant());
        profile.AddProperty("platform", "ESP32");
        profile.AddProperty("board", configuration.Board);
        profile.AddProperty("network", configuration.Network);

        if (configuration.NoisePsk is not null)
            profile.AddProperty("api_encryption", "Noise_NNpsk0_25519_ChaChaPoly_SHA256");
    }

    /// <summary>Sends an mDNS goodbye for the service and releases multicast resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        try
        {
            _serviceDiscovery.Unadvertise(_serviceProfile);
        }
        finally
        {
            _serviceDiscovery.Dispose();
        }
    }
}
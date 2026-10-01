using Makaretu.Dns;

namespace NetForESPHome.ServerMock;

/// <summary>Advertises the mock server using the ESPHome _esphomelib._tcp DNS-SD service and standard TXT metadata.</summary>
internal sealed class EspHomeMdnsAdvertiser : IDisposable
{
    private readonly ServiceDiscovery _serviceDiscovery;
    private readonly ServiceProfile _serviceProfile;
    private int _disposed;

    /// <summary>Creates and immediately announces an ESPHome DNS-SD profile for the supplied listening port.</summary>
    public EspHomeMdnsAdvertiser(EspHomeMockServerConfiguration configuration, int port)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port),port,"mDNS service port must be between 1 and 65535.");
        }

        _serviceProfile = CreateServiceProfile(configuration, port);
        _serviceDiscovery = new ServiceDiscovery();

        try
        {
            _serviceDiscovery.Advertise(_serviceProfile);
            _serviceDiscovery.Announce(_serviceProfile);
        }
        catch
        {
            _serviceDiscovery.Dispose();
            throw;
        }
    }

    private static ServiceProfile CreateServiceProfile(EspHomeMockServerConfiguration configuration, int port)
    {
        var profile = new ServiceProfile(configuration.Name, "_esphomelib._tcp", (ushort)port);
        NormalizeHostName(profile, configuration.Name);
        AddEspHomeProperties(profile, configuration);
        return profile;
    }

    private static void NormalizeHostName(ServiceProfile profile, string nodeName)
    {
        DomainName hostName = $"{nodeName}.local";
        profile.HostName = hostName;

        foreach (var resource in profile.Resources)
        {
            switch (resource)
            {
                case SRVRecord serviceRecord:
                    serviceRecord.Target = hostName;
                    break;
                case TXTRecord textRecord:
                    textRecord.Strings.Clear();
                    break;
                default:
                    resource.Name = hostName;
                    break;
            }
        }
    }

    private static void AddEspHomeProperties(
        ServiceProfile profile,
        EspHomeMockServerConfiguration configuration)
    {
        profile.AddProperty("friendly_name", configuration.FriendlyName);
        profile.AddProperty("version", configuration.EspHomeVersion);

        if (configuration.ConfigHash.Length > 0)
        {
            profile.AddProperty("config_hash", configuration.ConfigHash);
        }

        var normalizedMacAddress = configuration.MacAddress
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        profile.AddProperty("mac", normalizedMacAddress);
        profile.AddProperty("platform", "ESP32");
        profile.AddProperty("board", configuration.Board);
        profile.AddProperty("network", configuration.Network);

        if (configuration.NoisePsk is not null)
        {
            profile.AddProperty("api_encryption", "Noise_NNpsk0_25519_ChaChaPoly_SHA256");
        }
    }

    /// <summary>Sends an mDNS goodbye for the service and releases multicast resources.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
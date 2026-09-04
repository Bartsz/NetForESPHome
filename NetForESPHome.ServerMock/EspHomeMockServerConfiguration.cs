using Google.Protobuf;
using System.Net;
using System.Security.Cryptography;

namespace NetForESPHome.ServerMock;

/// <summary>Defines the identity, transport and entity behavior exposed by an ESPHome mock server.</summary>
public sealed class EspHomeMockServerConfiguration
{
    /// <summary>Gets the node name returned by HelloResponse and advertised through mDNS.</summary>
    public string Name { get; }

    /// <summary>Gets the human-readable ESPHome node name used in device information and mDNS TXT records.</summary>
    public string FriendlyName { get; }

    /// <summary>Gets the local address accepted by the TCP listener.</summary>
    public IPAddress ListenAddress { get; }

    /// <summary>Gets the configured TCP port; zero requests an operating-system assigned port.</summary>
    public int Port { get; }

    /// <summary>Gets the optional base64-encoded 32-byte Noise PSK; null selects plaintext framing.</summary>
    public string? NoisePsk { get; }

    /// <summary>Gets the optional legacy API password accepted for compatibility with pre-2026 clients.</summary>
    public string? Password { get; }

    /// <summary>Gets the Native API major version returned during Hello.</summary>
    public uint ApiVersionMajor { get; }

    /// <summary>Gets the Native API minor version returned during Hello.</summary>
    public uint ApiVersionMinor { get; }

    /// <summary>Gets the ESPHome version exposed by Hello, device information and mDNS.</summary>
    public string EspHomeVersion { get; }

    /// <summary>Gets the mock ESP32 MAC address exposed through the Native API and Noise ServerHello.</summary>
    public string MacAddress { get; }

    /// <summary>Gets the board identifier exposed by device information and mDNS.</summary>
    public string Board { get; }

    /// <summary>Gets the network name advertised through ESPHome mDNS metadata.</summary>
    public string Network { get; }

    /// <summary>Gets the optional ESPHome configuration hash advertised through mDNS.</summary>
    public string ConfigHash { get; }

    /// <summary>Gets whether the server should advertise _esphomelib._tcp over multicast DNS.</summary>
    public bool AdvertiseMdns { get; }

    /// <summary>Gets the time source used by scheduled entity producers.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Gets the configured entity descriptions, retained states and producers.</summary>
    public EspHomeEntityConfiguration[] Entities { get; }

    /// <summary>Gets user-defined service descriptions returned as part of ListEntities.</summary>
    public ListEntitiesServicesResponse[] Services { get; }

    /// <summary>Gets an optional factory used to customize DeviceInfoResponse for every request.</summary>
    public Func<DeviceInfoResponse>? GenerateDeviceInfo { get; }

    /// <summary>Gets an optional factory used to customize DeviceCapabilitiesResponse for every request.</summary>
    public Func<DeviceCapabilitiesResponse>? GenerateDeviceCapabilities { get; }

    /// <summary>Gets an optional callback invoked after every entity command is received and any built-in retained-state update is applied.</summary>
    public Func<IMessage, CancellationToken, ValueTask>? CommandReceived { get; }

    /// <summary>Creates a complete ESPHome mock configuration while keeping entity behavior in the supplied entity wrappers.</summary>
    public EspHomeMockServerConfiguration(string name, EspHomeEntityConfiguration[] entities, int port = 6053, IPAddress? listenAddress = null, string? noisePsk = null, string? password = null, string? friendlyName = null, uint apiVersionMajor = 1, uint apiVersionMinor = 16, string espHomeVersion = "2026.8.0", string macAddress = "02:00:00:00:00:01", string board = "esp32dev", string network = "wifi", string configHash = "", bool advertiseMdns = true, TimeProvider? timeProvider = null, ListEntitiesServicesResponse[]? services = null, Func<DeviceInfoResponse>? generateDeviceInfo = null, Func<DeviceCapabilitiesResponse>? generateDeviceCapabilities = null, Func<IMessage, CancellationToken, ValueTask>? commandReceived = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentException.ThrowIfNullOrEmpty(espHomeVersion);
        ArgumentException.ThrowIfNullOrEmpty(macAddress);
        ArgumentException.ThrowIfNullOrEmpty(board);
        ArgumentException.ThrowIfNullOrEmpty(network);

        if (name.Length > 31) throw new ArgumentOutOfRangeException(nameof(name), name, "ESPHome node name must not exceed 31 characters.");
        if (port is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 0 and 65535.");

        ValidateNoisePsk(noisePsk);
        ValidateEntities(entities);

        Name = name;
        FriendlyName = friendlyName ?? name;
        ListenAddress = listenAddress ?? IPAddress.Any;
        Port = port;
        NoisePsk = noisePsk;
        Password = password;
        ApiVersionMajor = apiVersionMajor;
        ApiVersionMinor = apiVersionMinor;
        EspHomeVersion = espHomeVersion;
        MacAddress = macAddress;
        Board = board;
        Network = network;
        ConfigHash = configHash;
        AdvertiseMdns = advertiseMdns;
        TimeProvider = timeProvider ?? TimeProvider.System;
        Entities = [.. entities];
        Services = services is null ? [] : [.. services];
        GenerateDeviceInfo = generateDeviceInfo;
        GenerateDeviceCapabilities = generateDeviceCapabilities;
        CommandReceived = commandReceived;
    }

    /// <summary>Creates the device information returned by the server when no custom response factory is configured.</summary>
    internal DeviceInfoResponse CreateDeviceInfoResponse()
    {
        var customResponse = GenerateDeviceInfo?.Invoke();
        if (customResponse is not null) return customResponse;

        var response = new DeviceInfoResponse
        {
            Name = Name,
            FriendlyName = FriendlyName,
            MacAddress = MacAddress,
            EsphomeVersion = EspHomeVersion,
            Model = Board,
            Manufacturer = "Espressif",
            ApiEncryptionSupported = NoisePsk is not null
        };

#pragma warning disable CS0612, CS0618
        response.UsesPassword = Password is not null;
#pragma warning restore CS0612, CS0618

        return response;
    }

    /// <summary>Creates the capabilities response returned by the server when no custom response factory is configured.</summary>
    internal DeviceCapabilitiesResponse CreateDeviceCapabilitiesResponse()
    {
        var customResponse = GenerateDeviceCapabilities?.Invoke();
        if (customResponse is not null) return customResponse;

        return new DeviceCapabilitiesResponse();
    }

    /// <summary>Validates a configured Noise PSK before the server starts accepting connections.</summary>
    private static void ValidateNoisePsk(string? noisePsk)
    {
        if (noisePsk is null) return;
        if (noisePsk.Length == 0) throw new ArgumentException("Noise PSK cannot be empty.", nameof(noisePsk));

        byte[] psk;

        try
        {
            psk = Convert.FromBase64String(noisePsk);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Noise PSK must be valid base64.", nameof(noisePsk), exception);
        }

        try
        {
            if (psk.Length != 32) throw new ArgumentException("ESPHome Noise PSK must decode to exactly 32 bytes.", nameof(noisePsk));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }

    /// <summary>Rejects duplicate entity addresses before they can make command and state routing ambiguous.</summary>
    private static void ValidateEntities(EspHomeEntityConfiguration[] entities)
    {
        var addresses = new HashSet<EntityAddress>();

        foreach (var entity in entities)
        {
            ArgumentNullException.ThrowIfNull(entity);

            var address = new EntityAddress(entity.DeviceId, entity.Key);
            if (!addresses.Add(address)) throw new ArgumentException($"ESPHome entity address {address} is configured more than once.", nameof(entities));
        }
    }
}
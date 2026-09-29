using Google.Protobuf;
using NetForESPHome.DomainModel.Native;

namespace NetForESPHome.ServerMock;

/// <summary>Defines one simulated Bluetooth LE device periodically observed by the ESPHome Bluetooth proxy.</summary>
public sealed class EspHomeBluetoothDeviceConfiguration : IMessageProducer
{
    private const int MaximumAdvertisementDataLength = 62;

    private readonly Func<byte[]> _generateAdvertisementData;
    private readonly Func<int> _generateRssi;
    private readonly Func<TimeSpan> _generateDelay;

    /// <summary>Gets the 48-bit Bluetooth device address encoded as an unsigned integer.</summary>
    public ulong Address { get; }

    /// <summary>Gets the Bluetooth address type reported by the scanner.</summary>
    public uint AddressType { get; }

    /// <summary>Creates one simulated Bluetooth advertiser with configurable payload, signal strength and advertisement interval.</summary>
    public EspHomeBluetoothDeviceConfiguration(ulong address, Func<byte[]> generateAdvertisementData, Func<int>? generateRssi = null, Func<TimeSpan>? generateDelay = null, uint addressType = 0)
    {
        ArgumentNullException.ThrowIfNull(generateAdvertisementData);

        if (address > 0xFFFFFFFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(address), address, "Bluetooth address must fit into 48 bits.");

        Address = address;
        AddressType = addressType;
        _generateAdvertisementData = generateAdvertisementData;
        _generateRssi = generateRssi ?? (() => -60);
        _generateDelay = generateDelay ?? (() => TimeSpan.FromSeconds(1));
    }

    /// <summary>Waits until the next simulated Bluetooth advertisement should be observed.</summary>
    public ValueTask WaitForDelayAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var delay = _generateDelay();
        if (delay < TimeSpan.Zero) throw new InvalidOperationException("Bluetooth advertisement delay cannot be negative.");

        var task = Task.Delay(delay, timeProvider, cancellationToken);
        return new ValueTask(task);
    }

    /// <summary>Creates one raw Bluetooth advertisement batch containing the configured simulated device.</summary>
    public IESPHomeMessage GenerateMessage()
    {
        var data = _generateAdvertisementData();
        if (data.Length > MaximumAdvertisementDataLength)
            throw new InvalidOperationException($"Bluetooth advertisement data cannot exceed {MaximumAdvertisementDataLength} bytes.");

        var advertisement = new BluetoothLERawAdvertisement
        {
            Address = Address,
            AddressType = AddressType,
            Rssi = _generateRssi(),
            Data = ByteString.CopyFrom(data)
        };

        var response = new BluetoothLERawAdvertisementsResponse();
        response.Advertisements.Add(advertisement);

        return response;
    }
}
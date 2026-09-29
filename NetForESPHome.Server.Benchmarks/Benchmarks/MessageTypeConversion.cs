using BenchmarkDotNet.Jobs;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using NetForESPHome.DomainModel.Native;

namespace NetForESPHome.Server.Benchmarks.Benchmarks;

//to choose which framework to use, add [SimpleJob(RuntimeMoniker.Net60)] or similar over the class
[SimpleJob(RuntimeMoniker.Net90)]
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
public class MessageTypeConversion
{
    //          BenchmarkDotNet v0.15.2,  
    //          12th Gen Intel Core i7-1280P 2.00GHz, 1 CPU, 20 logical and 14 physical cores
    //          .NET SDK 9.0.302   
    //      | Method                   | Mean        | Error     | StdDev    | Gen0   | Gen1   | Allocated |
    //      |------------------------- |------------:|----------:|----------:|-------:|-------:|----------:|
    //      | UsingOptionsGetExtension | 14,102.4 ns | 202.00 ns | 168.68 ns | 5.8289 |      - |   73200 B |
    //      | UsingIfs                 |  1,236.8 ns |  19.16 ns |  17.92 ns |      - |      - |         - |
    //      | UsingDictionary          |    965.8 ns |  15.59 ns |  14.58 ns |      - |      - |         - |
    //      | UsingList                |  5,498.9 ns | 107.44 ns | 173.50 ns |      - |      - |         - |
    //      | UsingProtobufReflections | 26,543.5 ns | 477.97 ns | 771.84 ns | 5.8594 | 2.9297 |   74133 B |

    List<IMessage> allMessages = new()
    {
        new HelloRequest(),
        new HelloResponse(),
        new DisconnectRequest(),
        new DisconnectResponse(),
        new PingRequest(),
        new PingResponse(),
        new DeviceInfoRequest(),
        new DeviceInfoResponse(),
        new ListEntitiesRequest(),
        new ListEntitiesBinarySensorResponse(),
        new ListEntitiesDoneResponse(),
        new SubscribeStatesRequest(),
        new BinarySensorStateResponse(),
        new CoverStateResponse(),
        new FanStateResponse(),
        new LightStateResponse(),
        new SensorStateResponse(),
        new SwitchStateResponse(),
        new TextSensorStateResponse(),
        new SubscribeLogsRequest(),
        new SubscribeLogsResponse(),
        new CoverCommandRequest(),
        new FanCommandRequest(),
        new LightCommandRequest(),
        new SwitchCommandRequest(),
        new SubscribeHomeassistantServicesRequest(),
        new GetTimeRequest(),
        new GetTimeResponse(),
        new SubscribeHomeAssistantStatesRequest(),
        new SubscribeHomeAssistantStateResponse(),
        new HomeAssistantStateResponse(),
        new ListEntitiesServicesResponse(),
        new ExecuteServiceRequest(),
        new ListEntitiesCameraResponse(),
        new CameraImageResponse(),
        new CameraImageRequest(),
        new ListEntitiesClimateResponse(),
        new ClimateStateResponse(),
        new ClimateCommandRequest(),
        new ListEntitiesNumberResponse(),
        new NumberStateResponse(),
        new NumberCommandRequest(),
        new ListEntitiesSelectResponse(),
        new SelectStateResponse(),
        new SelectCommandRequest(),
        new ListEntitiesSirenResponse(),
        new SirenStateResponse(),
        new SirenCommandRequest(),
        new ListEntitiesLockResponse(),
        new LockStateResponse(),
        new LockCommandRequest(),
        new ListEntitiesButtonResponse(),
        new ButtonCommandRequest(),
        new ListEntitiesMediaPlayerResponse(),
        new MediaPlayerStateResponse(),
        new MediaPlayerCommandRequest(),
        new SubscribeBluetoothLEAdvertisementsRequest(),
        new BluetoothLEAdvertisementResponse(),
        new BluetoothDeviceRequest(),
        new BluetoothDeviceConnectionResponse(),
        new BluetoothGATTGetServicesRequest(),
        new BluetoothGATTGetServicesResponse(),
        new BluetoothGATTGetServicesDoneResponse(),
        new BluetoothGATTReadRequest(),
        new BluetoothGATTReadResponse(),
        new BluetoothGATTWriteRequest(),
        new BluetoothGATTReadDescriptorRequest(),
        new BluetoothGATTWriteDescriptorRequest(),
        new BluetoothGATTNotifyRequest(),
        new BluetoothGATTNotifyDataResponse(),
        new SubscribeBluetoothConnectionsFreeRequest(),
        new BluetoothConnectionsFreeResponse(),
        new BluetoothGATTErrorResponse(),
        new BluetoothGATTWriteResponse(),
        new BluetoothGATTNotifyResponse(),
        new BluetoothDevicePairingResponse(),
        new BluetoothDeviceUnpairingResponse(),
        new UnsubscribeBluetoothLEAdvertisementsRequest(),
        new BluetoothDeviceClearCacheResponse(),
        new SubscribeVoiceAssistantRequest(),
        new VoiceAssistantRequest(),
        new VoiceAssistantResponse(),
        new VoiceAssistantEventResponse(),
        new BluetoothLERawAdvertisementsResponse(),
        new ListEntitiesAlarmControlPanelResponse(),
        new AlarmControlPanelStateResponse(),
        new AlarmControlPanelCommandRequest(),
        new ListEntitiesTextResponse(),
        new TextStateResponse(),
        new TextCommandRequest(),
        new ListEntitiesDateResponse(),
        new DateStateResponse(),
        new DateCommandRequest(),
        new ListEntitiesTimeResponse(),
        new TimeStateResponse(),
        new TimeCommandRequest(),
        new VoiceAssistantAudio(),
        new ListEntitiesEventResponse(),
        new EventResponse(),
        new ListEntitiesValveResponse(),
        new ValveStateResponse(),
        new ValveCommandRequest(),
        new ListEntitiesUpdateResponse(),
        new UpdateStateResponse(),
        new UpdateCommandRequest(),
        new VoiceAssistantTimerEventResponse(),
        new ListEntitiesUpdateResponse(),
        new UpdateStateResponse(),
        new UpdateCommandRequest(),
        new VoiceAssistantAnnounceRequest(),
        new VoiceAssistantAnnounceFinished(),
        new VoiceAssistantConfigurationRequest(),
        new VoiceAssistantConfigurationResponse(),
        new VoiceAssistantSetConfiguration(),
        new NoiseEncryptionSetKeyRequest(),
        new NoiseEncryptionSetKeyResponse(),
        new BluetoothScannerStateResponse(),
        new BluetoothScannerSetModeRequest(),
    };

    private Dictionary<Type, uint> _messageTypeNoByType = new Dictionary<Type, uint>();
    private List<Type> _typesList;

    [GlobalSetup]
    public void Setup()
    {
        GenerateTypeNoByTypeDictionary();
        var typesArray = new Type[_messageTypeNoByType.Count + 1];

        foreach (var kvp in _messageTypeNoByType)
        {
            typesArray[kvp.Value] = kvp.Key;
        }

        _typesList = [.. typesArray];
    }

    [Benchmark]
    public void UsingOptionsGetExtension()
    {
        var count = 0u;
        foreach (var message in allMessages)
        {
            count += GetMessageTypeNoFromOptionExtension(message);
        }
    }

    private uint GetMessageTypeNoFromOptionExtension(IMessage message)
    {
        return message.Descriptor.GetOptions().GetExtension(ApiOptionsExtensions.Id);
    }


    [Benchmark]
    public void UsingIfs()
    {
        var count = 0u;
        foreach (var message in allMessages)
        {
            count += GetMessageTypeNoIfs(message);
        }
    }

    [Benchmark]
    public void UsingDictionary()
    {
        var count = 0u;
        foreach (var message in allMessages)
        {
            count += GetMessageTypeNoDict(message);
        }
    }

    [Benchmark]
    public void UsingList()
    {
        var count = 0u;
        foreach (var message in allMessages)
        {
            count += GetMessageTypeNoArrayLookup(message);
        }
    }

    [Benchmark]
    public void UsingProtobufReflections()
    {
        var count = 0u;
        foreach (var message in allMessages)
        {
            count += GetMessageTypeNoFromDescriptor(message);
        }
    }

    private uint GetMessageTypeNoIfs(IMessage message)
    {
        Type messageType = message.GetType();

        return GetMessageType(messageType);
    }


    private uint GetMessageTypeNoDict(IMessage message)
    {
        var typeNo = _messageTypeNoByType.GetValueOrDefault(message.GetType());

        return typeNo;
    }

    private uint GetMessageTypeNoArrayLookup(IMessage message)
    {
        Type messageType = message.GetType();
        int typeNo = _typesList.IndexOf(messageType);

        if (typeNo < 0)
        {
            return 0;
        }

        return (uint)typeNo;
    }

    private uint GetMessageTypeNoFromDescriptor(IMessage message)
    {
        Type messageType = message.GetType();

        var descriptorProperty = messageType.GetProperty("Descriptor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        if (descriptorProperty == null)
            throw new InvalidOperationException($"Type {messageType.Name} does not have a static 'Descriptor' property.");

        var messageDescriptor = (MessageDescriptor?)descriptorProperty.GetValue(null);

        if (messageDescriptor == null) return 0;

        var options = messageDescriptor.GetOptions();

        return options.HasExtension(ApiOptionsExtensions.Id)
            ? options.GetExtension(ApiOptionsExtensions.Id)
            : 0;
    }

    private void GenerateTypeNoByTypeDictionary()
    {
        try
        {
            FileDescriptor messagesFileDescriptor = ApiReflection.Descriptor;

            foreach (MessageDescriptor messageDescriptor in messagesFileDescriptor.MessageTypes)
            {
                Type messageClrType = messageDescriptor.ClrType;
                if (messageClrType == null || !typeof(IMessage).IsAssignableFrom(messageClrType)) continue;

                MessageOptions options = messageDescriptor.GetOptions();
                if (options == null || !options.HasExtension(ApiOptionsExtensions.Id)) continue;

                uint messageId = options.GetExtension(ApiOptionsExtensions.Id);
                _messageTypeNoByType[messageClrType] = messageId;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error pre-populating message IDs: {ex.Message}");
        }
    }

    private uint GetMessageType(Type type)
    {
        if (type == typeof(HelloRequest)) return 1;
        if (type == typeof(HelloResponse)) return 2;
        if (type == typeof(DisconnectRequest)) return 5;
        if (type == typeof(DisconnectResponse)) return 6;
        if (type == typeof(PingRequest)) return 7;
        if (type == typeof(PingResponse)) return 8;
        if (type == typeof(DeviceInfoRequest)) return 9;
        if (type == typeof(DeviceInfoResponse)) return 10;
        if (type == typeof(ListEntitiesRequest)) return 11;
        if (type == typeof(ListEntitiesBinarySensorResponse)) return 12;
        if (type == typeof(ListEntitiesDoneResponse)) return 19;
        if (type == typeof(SubscribeStatesRequest)) return 20;
        if (type == typeof(BinarySensorStateResponse)) return 21;
        if (type == typeof(CoverStateResponse)) return 22;
        if (type == typeof(FanStateResponse)) return 23;
        if (type == typeof(LightStateResponse)) return 24;
        if (type == typeof(SensorStateResponse)) return 25;
        if (type == typeof(SwitchStateResponse)) return 26;
        if (type == typeof(TextSensorStateResponse)) return 27;
        if (type == typeof(SubscribeLogsRequest)) return 28;
        if (type == typeof(SubscribeLogsResponse)) return 29;
        if (type == typeof(CoverCommandRequest)) return 30;
        if (type == typeof(FanCommandRequest)) return 31;
        if (type == typeof(LightCommandRequest)) return 32;
        if (type == typeof(SwitchCommandRequest)) return 33;
        if (type == typeof(SubscribeHomeassistantServicesRequest)) return 34;
        if (type == typeof(GetTimeRequest)) return 36;
        if (type == typeof(GetTimeResponse)) return 37;
        if (type == typeof(SubscribeHomeAssistantStatesRequest)) return 38;
        if (type == typeof(SubscribeHomeAssistantStateResponse)) return 39;
        if (type == typeof(HomeAssistantStateResponse)) return 40;
        if (type == typeof(ListEntitiesServicesResponse)) return 41;
        if (type == typeof(ExecuteServiceRequest)) return 42;
        if (type == typeof(ListEntitiesCameraResponse)) return 43;
        if (type == typeof(CameraImageResponse)) return 44;
        if (type == typeof(CameraImageRequest)) return 45;
        if (type == typeof(ListEntitiesClimateResponse)) return 46;
        if (type == typeof(ClimateStateResponse)) return 47;
        if (type == typeof(ClimateCommandRequest)) return 48;
        if (type == typeof(ListEntitiesNumberResponse)) return 49;
        if (type == typeof(NumberStateResponse)) return 50;
        if (type == typeof(NumberCommandRequest)) return 51;
        if (type == typeof(ListEntitiesSelectResponse)) return 52;
        if (type == typeof(SelectStateResponse)) return 53;
        if (type == typeof(SelectCommandRequest)) return 54;
        if (type == typeof(ListEntitiesSirenResponse)) return 55;
        if (type == typeof(SirenStateResponse)) return 56;
        if (type == typeof(SirenCommandRequest)) return 57;
        if (type == typeof(ListEntitiesLockResponse)) return 58;
        if (type == typeof(LockStateResponse)) return 59;
        if (type == typeof(LockCommandRequest)) return 60;
        if (type == typeof(ListEntitiesButtonResponse)) return 61;
        if (type == typeof(ButtonCommandRequest)) return 62;
        if (type == typeof(ListEntitiesMediaPlayerResponse)) return 63;
        if (type == typeof(MediaPlayerStateResponse)) return 64;
        if (type == typeof(MediaPlayerCommandRequest)) return 65;
        if (type == typeof(SubscribeBluetoothLEAdvertisementsRequest)) return 66;
        if (type == typeof(BluetoothLEAdvertisementResponse)) return 67;
        if (type == typeof(BluetoothDeviceRequest)) return 68;
        if (type == typeof(BluetoothDeviceConnectionResponse)) return 69;
        if (type == typeof(BluetoothGATTGetServicesRequest)) return 70;
        if (type == typeof(BluetoothGATTGetServicesResponse)) return 71;
        if (type == typeof(BluetoothGATTGetServicesDoneResponse)) return 72;
        if (type == typeof(BluetoothGATTReadRequest)) return 73;
        if (type == typeof(BluetoothGATTReadResponse)) return 74;
        if (type == typeof(BluetoothGATTWriteRequest)) return 75;
        if (type == typeof(BluetoothGATTReadDescriptorRequest)) return 76;
        if (type == typeof(BluetoothGATTWriteDescriptorRequest)) return 77;
        if (type == typeof(BluetoothGATTNotifyRequest)) return 78;
        if (type == typeof(BluetoothGATTNotifyDataResponse)) return 79;
        if (type == typeof(SubscribeBluetoothConnectionsFreeRequest)) return 80;
        if (type == typeof(BluetoothConnectionsFreeResponse)) return 81;
        if (type == typeof(BluetoothGATTErrorResponse)) return 82;
        if (type == typeof(BluetoothGATTWriteResponse)) return 83;
        if (type == typeof(BluetoothGATTNotifyResponse)) return 84;
        if (type == typeof(BluetoothDevicePairingResponse)) return 85;
        if (type == typeof(BluetoothDeviceUnpairingResponse)) return 86;
        if (type == typeof(UnsubscribeBluetoothLEAdvertisementsRequest)) return 87;
        if (type == typeof(BluetoothDeviceClearCacheResponse)) return 88;
        if (type == typeof(SubscribeVoiceAssistantRequest)) return 89;
        if (type == typeof(VoiceAssistantRequest)) return 90;
        if (type == typeof(VoiceAssistantResponse)) return 91;
        if (type == typeof(VoiceAssistantEventResponse)) return 92;
        if (type == typeof(BluetoothLERawAdvertisementsResponse)) return 93;
        if (type == typeof(ListEntitiesAlarmControlPanelResponse)) return 94;
        if (type == typeof(AlarmControlPanelStateResponse)) return 95;
        if (type == typeof(AlarmControlPanelCommandRequest)) return 96;
        if (type == typeof(ListEntitiesTextResponse)) return 97;
        if (type == typeof(TextStateResponse)) return 98;
        if (type == typeof(TextCommandRequest)) return 99;
        if (type == typeof(ListEntitiesDateResponse)) return 100;
        if (type == typeof(DateStateResponse)) return 101;
        if (type == typeof(DateCommandRequest)) return 102;
        if (type == typeof(ListEntitiesTimeResponse)) return 103;
        if (type == typeof(TimeStateResponse)) return 104;
        if (type == typeof(TimeCommandRequest)) return 105;
        if (type == typeof(VoiceAssistantAudio)) return 106;
        if (type == typeof(ListEntitiesEventResponse)) return 107;
        if (type == typeof(EventResponse)) return 108;
        if (type == typeof(ListEntitiesValveResponse)) return 109;
        if (type == typeof(ValveStateResponse)) return 110;
        if (type == typeof(ValveCommandRequest)) return 111;
        if (type == typeof(ListEntitiesUpdateResponse)) return 112;
        if (type == typeof(UpdateStateResponse)) return 113;
        if (type == typeof(UpdateCommandRequest)) return 114;
        if (type == typeof(VoiceAssistantTimerEventResponse)) return 115;
        if (type == typeof(ListEntitiesUpdateResponse)) return 116;
        if (type == typeof(UpdateStateResponse)) return 117;
        if (type == typeof(UpdateCommandRequest)) return 118;
        if (type == typeof(VoiceAssistantAnnounceRequest)) return 119;
        if (type == typeof(VoiceAssistantAnnounceFinished)) return 120;
        if (type == typeof(VoiceAssistantConfigurationRequest)) return 121;
        if (type == typeof(VoiceAssistantConfigurationResponse)) return 122;
        if (type == typeof(VoiceAssistantSetConfiguration)) return 123;
        if (type == typeof(NoiseEncryptionSetKeyRequest)) return 124;
        if (type == typeof(NoiseEncryptionSetKeyResponse)) return 125;
        if (type == typeof(BluetoothScannerStateResponse)) return 126;
        if (type == typeof(BluetoothScannerSetModeRequest)) return 127;

        throw new ArgumentOutOfRangeException(type.Name, $"Unknown message type: {type.FullName}");
    }

}

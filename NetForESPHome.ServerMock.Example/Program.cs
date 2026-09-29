using Microsoft.Extensions.Logging;
using NetForESPHome.DomainModel.Native;

namespace NetForESPHome.ServerMock.Example;

internal static class Program
{
    private const uint LivingRoomLightKey = 1;
    private const uint KitchenLightKey = 2;

    private const uint PumpSwitchKey = 10;
    private const uint HeaterSwitchKey = 11;

    private const uint TemperatureSensorKey = 20;
    private const uint HumiditySensorKey = 21;
    private const uint Co2SensorKey = 22;
    private const uint VoltageSensorKey = 23;

    private const uint ErrorMessageSensorKey = 30;

    /// <summary>Starts a mock ESPHome device and keeps it running until Ctrl+C is pressed.</summary>
    public static async Task Main()
    {
        ILogger<EspHomeMockServer> logger = CreateLogger(Microsoft.Extensions.Logging.LogLevel.Trace);
        var entities = CreateEntities();


        var closeProgram = AskUserAboutNoise(out var useNoise);
        if (!closeProgram) return;

        string? pskBase64 = null;

        if (useNoise)
        {
            var pskBytes = new byte[32];
            new Random().NextBytes(pskBytes);
            pskBase64 = Convert.ToBase64String(pskBytes);
            Console.WriteLine();
            Console.WriteLine(pskBase64);
            Console.WriteLine();
        }

        var configuration = new EspHomeMockServerConfiguration(
            name: "mock-esp32",
            entities: entities,
            port: 6053,
            friendlyName: "Mock ESP32",
            board: "esp32dev",
            network: "wifi",
            noisePsk: pskBase64,
            macAddress: "02:00:00:00:00:01",
            advertiseMdns: true);

        var server = new EspHomeMockServer(configuration, logger);
        await using (server.ConfigureAwait(false))
        {
            RegisterBluetoothDevice(server);

            using var shutdownCancellation = new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdownCancellation.Cancel();
            };

            await server.StartAsync(shutdownCancellation.Token).ConfigureAwait(false);

            Console.WriteLine($"ESPHome mock server started on port {server.Port}.");
            Console.WriteLine("Press Ctrl+C to stop.");

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, configuration.TimeProvider, shutdownCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownCancellation.IsCancellationRequested)
            {
            }

            await server.StopAsync().ConfigureAwait(false);
        }
    }

    private static bool AskUserAboutNoise(out bool useNoise)
    {
        useNoise = false;
        Console.WriteLine("Do you want to use noise (Y/N)? Press ESC to exit. (also space for No)");

        while (true)
        {
            var key = Console.ReadKey();
            Console.WriteLine();
            switch (key.Key)
            {
                case ConsoleKey.Y:
                    useNoise = true;
                    return true;
                case ConsoleKey.N: return true;
                case ConsoleKey.Escape: return false;
            }
        }
    }

    private static ILogger<EspHomeMockServer> CreateLogger(Microsoft.Extensions.Logging.LogLevel logLevel)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(logLevel)
                .AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                    options.IncludeScopes = true;
                });
        });

        return loggerFactory.CreateLogger<EspHomeMockServer>();
    }

    /// <summary>Creates one valid unencrypted BTHome v2 advertisement containing temperature and humidity.</summary>
    private static byte[] CreateBluetoothAdvertisement()
    {
        return
        [
            // Flags
            0x02, 0x01, 0x06,

        // Complete local name: "Mock BTHome"
        0x0C, 0x09,
        (byte)'M',
        (byte)'o',
        (byte)'c',
        (byte)'k',
        (byte)' ',
        (byte)'B',
        (byte)'T',
        (byte)'H',
        (byte)'o',
        (byte)'m',
        (byte)'e',

        // Service Data - 16-bit UUID 0xFCD2 (BTHome)
        0x0A, 0x16,
        0xD2, 0xFC,

        // BTHome v2, unencrypted, regular advertisements
        0x40,

        // Temperature: object 0x02, 25.00 °C = 2500 = 0x09C4 little-endian
        0x02, 0xC4, 0x09,

        // Humidity: object 0x03, 50.00 % = 5000 = 0x1388 little-endian
        0x03, 0x88, 0x13
        ];
    }

    /// <summary>Registers one simulated BTHome Bluetooth sensor visible through the ESPHome Bluetooth proxy.</summary>
    private static void RegisterBluetoothDevice(EspHomeMockServer server)
    {
        var device = new EspHomeBluetoothDeviceConfiguration(
            address: 0xAABBCCDDEE01,
            generateAdvertisementData: CreateBluetoothAdvertisement,
            generateRssi: () => Random.Shared.Next(-65, -50),
            generateDelay: () => TimeSpan.FromMilliseconds(Random.Shared.Next(900, 1101)));

        server.RegisterBluetoothDevice(device);
    }

    /// <summary>Creates the entities exposed by the mock ESPHome device.</summary>
    private static EspHomeEntityConfiguration[] CreateEntities()
    {
        var livingRoomLightInfo = new ListEntitiesLightResponse
        {
            Key = LivingRoomLightKey,
            ObjectId = "living_room_light",
            Name = "Living Room Light",
            SupportedColorModes = { ColorMode.OnOff, ColorMode.Brightness }
        };

        var livingRoomLightState = new LightStateResponse
        {
            Key = LivingRoomLightKey,
            State = false,
            ColorMode = ColorMode.OnOff,
        };

        var kitchenLightInfo = new ListEntitiesLightResponse
        {
            Key = KitchenLightKey,
            ObjectId = "kitchen_light",
            Name = "Kitchen Light",
            SupportedColorModes = { ColorMode.OnOff }
        };

        var kitchenLightState = new LightStateResponse
        {
            Key = KitchenLightKey,
            State = true,
            ColorMode = ColorMode.OnOff
        };

        var pumpSwitchInfo = new ListEntitiesSwitchResponse
        {
            Key = PumpSwitchKey,
            ObjectId = "pump",
            Name = "Pump"
        };

        var pumpSwitchState = new SwitchStateResponse
        {
            Key = PumpSwitchKey,
            State = false
        };

        var heaterSwitchInfo = new ListEntitiesSwitchResponse
        {
            Key = HeaterSwitchKey,
            ObjectId = "heater",
            Name = "Heater"
        };

        var heaterSwitchState = new SwitchStateResponse
        {
            Key = HeaterSwitchKey,
            State = true
        };

        var temperatureSensorInfo = new ListEntitiesSensorResponse
        {
            Key = TemperatureSensorKey,
            ObjectId = "temperature",
            Name = "Temperature",
            UnitOfMeasurement = "°C",
            AccuracyDecimals = 1,
            DeviceClass = "temperature",
            StateClass = SensorStateClass.StateClassMeasurement
        };

        var temperatureSensorState = new SensorStateResponse
        {
            Key = TemperatureSensorKey,
            State = 22.5f
        };

        var humiditySensorInfo = new ListEntitiesSensorResponse
        {
            Key = HumiditySensorKey,
            ObjectId = "humidity",
            Name = "Humidity",
            UnitOfMeasurement = "%",
            AccuracyDecimals = 1,
            DeviceClass = "humidity",
            StateClass = SensorStateClass.StateClassMeasurement
        };

        var humiditySensorState = new SensorStateResponse
        {
            Key = HumiditySensorKey,
            State = 45.0f
        };

        var co2SensorInfo = new ListEntitiesSensorResponse
        {
            Key = Co2SensorKey,
            ObjectId = "co2",
            Name = "CO2",
            UnitOfMeasurement = "ppm",
            AccuracyDecimals = 0,
            DeviceClass = "carbon_dioxide",
            StateClass = SensorStateClass.StateClassMeasurement
        };

        var co2SensorState = new SensorStateResponse
        {
            Key = Co2SensorKey,
            State = 450.0f
        };

        var voltageSensorInfo = new ListEntitiesSensorResponse
        {
            Key = VoltageSensorKey,
            ObjectId = "voltage",
            Name = "Voltage",
            UnitOfMeasurement = "V",
            AccuracyDecimals = 2,
            DeviceClass = "voltage",
            StateClass = SensorStateClass.StateClassMeasurement
        };

        var voltageSensorState = new SensorStateResponse
        {
            Key = VoltageSensorKey,
            State = 3.30f
        };

        var errorMessages = new[]
        {
        "No error",
        "Communication timeout",
        "Sensor unavailable",
        "Low supply voltage"
    };

        var errorMessageIndex = 1;

        var errorMessageSensorInfo = new ListEntitiesTextSensorResponse
        {
            Key = ErrorMessageSensorKey,
            ObjectId = "error_message",
            Name = "Error Message"
        };

        var errorMessageSensorState = new TextSensorStateResponse
        {
            Key = ErrorMessageSensorKey,
            State = errorMessages[0]
        };

        EspHomeEntityConfiguration[] entities =
        [
            new EspHomeEntityConfiguration<ListEntitiesLightResponse, LightStateResponse>(
            livingRoomLightInfo,
            livingRoomLightState),

        new EspHomeEntityConfiguration<ListEntitiesLightResponse, LightStateResponse>(
            kitchenLightInfo,
            kitchenLightState),

        new EspHomeEntityConfiguration<ListEntitiesSwitchResponse, SwitchStateResponse>(
            pumpSwitchInfo,
            pumpSwitchState),

        new EspHomeEntityConfiguration<ListEntitiesSwitchResponse, SwitchStateResponse>(
            heaterSwitchInfo,
            heaterSwitchState),

        new EspHomeEntityConfiguration<ListEntitiesSensorResponse, SensorStateResponse>(
            temperatureSensorInfo,
            temperatureSensorState,
            generateMessage: () => new SensorStateResponse
            {
                Key = TemperatureSensorKey,
                State = RandomInRange(20.0f, 25.0f)
            },
            generateDelay: GenerateSensorDelay),

        new EspHomeEntityConfiguration<ListEntitiesSensorResponse, SensorStateResponse>(
            humiditySensorInfo,
            humiditySensorState,
            generateMessage: () => new SensorStateResponse
            {
                Key = HumiditySensorKey,
                State = RandomInRange(35.0f, 65.0f)
            },
            generateDelay: GenerateSensorDelay),

        new EspHomeEntityConfiguration<ListEntitiesSensorResponse, SensorStateResponse>(
            co2SensorInfo,
            co2SensorState,
            generateMessage: () => new SensorStateResponse
            {
                Key = Co2SensorKey,
                State = RandomInRange(400.0f, 1200.0f)
            },
            generateDelay: GenerateSensorDelay),

        new EspHomeEntityConfiguration<ListEntitiesSensorResponse, SensorStateResponse>(
            voltageSensorInfo,
            voltageSensorState,
            generateMessage: () => new SensorStateResponse
            {
                Key = VoltageSensorKey,
                State = RandomInRange(3.20f, 3.35f)
            },
            generateDelay: GenerateSensorDelay),

        new EspHomeEntityConfiguration<ListEntitiesTextSensorResponse, TextSensorStateResponse>(
            errorMessageSensorInfo,
            errorMessageSensorState,
            generateMessage: () =>
            {
                var message = errorMessages[errorMessageIndex];
                errorMessageIndex = (errorMessageIndex + 1) % errorMessages.Length;

                return new TextSensorStateResponse
                {
                    Key = ErrorMessageSensorKey,
                    State = message
                };
            },
            generateDelay: () => TimeSpan.FromSeconds(1))
        ];

        return entities;
    }

    /// <summary>Generates the approximately one-second sensor interval with a plus or minus 100 millisecond jitter.</summary>
    private static TimeSpan GenerateSensorDelay()
    {
        var microseconds = Random.Shared.Next(2_000_000, 4_000_000); // 2 to 4 seconds in microseconds
        return TimeSpan.FromMicroseconds(microseconds);
    }

    /// <summary>Generates a random floating-point sensor value inside the requested range.</summary>
    private static float RandomInRange(float minimum, float maximum)
    {
        return minimum + Random.Shared.NextSingle() * (maximum - minimum);
    }
}
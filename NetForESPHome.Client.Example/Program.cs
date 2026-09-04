using Microsoft.Extensions.Logging;
using NetForESPHome.Transports;
using System.Net.Sockets;

namespace NetForESPHome.Client.Example;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var logger = CreateLogger(Microsoft.Extensions.Logging.LogLevel.Trace);
        var zeroConfHosts = await EspHomeClient.DiscoverAsync().ConfigureAwait(false);

        if (zeroConfHosts is null)
        {
            Console.WriteLine("No ESPHome device found on the network.");
            return;
        }

        foreach (var service in zeroConfHosts.Services)
        {
            Console.WriteLine($"Discovered ESPHome device: {service.Key}");
        }

        var firstService = zeroConfHosts.Services.First().Value;
   
        using var connectionProbe = new ESPHomeClientConnectionMethodDetector(new TcpClient(), zeroConfHosts.IPAddress, firstService.Port);
        var connectionType = await connectionProbe.GetConnectionMethodAsync().ConfigureAwait(false);
         
        string? noisePsk = string.Empty;

        if (connectionType == ConnectionMethod.Noise)
        {
            Console.WriteLine("Server requires Noise pass key - plaes paste 32-character base64 pass key:");
            noisePsk = Console.ReadLine();
            noisePsk = noisePsk?.Trim();
        }

        using var shutdownCancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdownCancellation.Cancel();
        };

        var espHomeClient = new EspHomeClient(zeroConfHosts.IPAddress, firstService.Port, noisePsk, logger: logger);
        await espHomeClient.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        var entities = await espHomeClient.RequestEntitiesListRefresh().ConfigureAwait(false);
        await espHomeClient.StartStatesSubscriptionAsync().ConfigureAwait(false);

        Console.WriteLine($"ESPHome client started on port {firstService.Port}.");
        Console.WriteLine("Press Ctrl+C to stop.");

        while (!shutdownCancellation.IsCancellationRequested)
        {
            var msg = await espHomeClient.GetOutputMessageAsync(shutdownCancellation.Token).ConfigureAwait(false);
            logger.LogTrace($"Recieved Message type: {msg.MessageType}");
            switch (msg.MessageType)
            {
                case EspHomeMessageType.BluetoothAdvertisementReceived:
                    break;
                case EspHomeMessageType.BluetoothRawAdvertisementsReceived:
                    break;
                case EspHomeMessageType.BluetoothConnectionChanged:
                    break;
                case EspHomeMessageType.BluetoothGattNotification:
                    break;
                case EspHomeMessageType.EspHomeEntityStateChanged:
                    break;
                case EspHomeMessageType.EspHomeEntitiesListReceived:
                    break;
                case EspHomeMessageType.EspHomeEntitiesServicesListReceived:
                    break;
                case EspHomeMessageType.EspHomeDeviceInfoReceived:
                    break;
                case EspHomeMessageType.EspHomeDeviceCapabilitiesReceived:
                    break;
                case EspHomeMessageType.CameraImageReceived:
                    break;
                case EspHomeMessageType.LogReceived:
                    break;
                case EspHomeMessageType.HomeAssistantActionRequested:
                    break;
                case EspHomeMessageType.EspHomeRequestedState:
                    break;
                case EspHomeMessageType.EspHomeRequestedStateSubscription:
                    break;
                case EspHomeMessageType.BluetoothConnectionsFreeChanged:
                    break;
                case EspHomeMessageType.BluetoothScannerStateChanged:
                    break;
                case EspHomeMessageType.ZWaveProxyFrameReceived:
                    break;
                case EspHomeMessageType.ZWaveProxyRequestReceived:
                    break;
                case EspHomeMessageType.InfraredRFReceived:
                    break;
                case EspHomeMessageType.SerialProxyDataReceived:
                    break;
                case EspHomeMessageType.VoiceAssistantStartRequested:
                    break;
                case EspHomeMessageType.VoiceAssistantStopRequested:
                    break;
                case EspHomeMessageType.VoiceAssistantAudioReceived:
                    break;
                case EspHomeMessageType.VoiceAssistantAudioStreamEnded:
                    break;
                case EspHomeMessageType.VoiceAssistantAnnouncementFinished:
                    break;
                case EspHomeMessageType.ConnectionClosed:
                    return;
                case EspHomeMessageType.OutgoingMessageRejected:
                    break;
                default:
                    break;
            }
        }
    }


    private static ILogger<EspHomeClient> CreateLogger(Microsoft.Extensions.Logging.LogLevel logLevel)
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

        return loggerFactory.CreateLogger<EspHomeClient>();
    }
}

using Microsoft.Extensions.Logging;
using NetForESPHome.DomainModel.Native;
using NetForESPHome.Transports;
using System.Net.Sockets;
using Zeroconf;

namespace NetForESPHome.Client.Example;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var logger = CreateLogger(Microsoft.Extensions.Logging.LogLevel.Trace);
        var zeroConfHosts = await DiscoverAsync().ConfigureAwait(false);

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
            Console.WriteLine("Server requires Noise pass key -  paste 32-character base64 pass key and press enter:");
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

        var task1 = ListienTOGeneralMessages(logger, shutdownCancellation, espHomeClient);//.ConfigureAwait(false);
        var task2 = ListenToStateMessages(logger, shutdownCancellation, espHomeClient);//.ConfigureAwait(false);

        await espHomeClient.ConnectAsync(CancellationToken.None).ConfigureAwait(false);

        var entities = await  espHomeClient.GetEntitiesAndServicesAsync().ConfigureAwait(false);
        
        await espHomeClient.StartStatesSubscriptionAsync().ConfigureAwait(false);


        Console.WriteLine($"ESPHome client started on port {firstService.Port}.");
        Console.WriteLine("Press Ctrl+C to stop.");

        await Task.WhenAll(task1, task2).ConfigureAwait(false);
    }


    private static async Task ListenToStateMessages(ILogger<EspHomeClient> logger, CancellationTokenSource shutdownCancellation, EspHomeClient espHomeClient)
    {
        while (!shutdownCancellation.IsCancellationRequested)
        {
            var msg = await espHomeClient.GetIncomingStateChangeMessageAsync(shutdownCancellation.Token).ConfigureAwait(false);
            //logger.LogTrace($"Recieved Message type: {msg.StateType}");
            switch (msg.StateType)
            {
                case ESPHomeEntityStateType.AlarmControlPanelState:
                    break;
                case ESPHomeEntityStateType.BinarySensorState:
                    break;
                case ESPHomeEntityStateType.CameraImage:
                    break;
                case ESPHomeEntityStateType.ClimateState:
                    break;
                case ESPHomeEntityStateType.CoverState:
                    break;
                case ESPHomeEntityStateType.DateState:
                    break;
                case ESPHomeEntityStateType.DateTimeState:
                    break;
                case ESPHomeEntityStateType.Event:
                    break;
                case ESPHomeEntityStateType.FanState:
                    break;
                case ESPHomeEntityStateType.LightState:
                    break;
                case ESPHomeEntityStateType.LockState:
                    break;
                case ESPHomeEntityStateType.MediaPlayerState:
                    break;
                case ESPHomeEntityStateType.NumberState:
                    break;
                case ESPHomeEntityStateType.SelectState:
                    break;
                case ESPHomeEntityStateType.SensorState when (msg is SensorStateResponse state):
                    logger.LogTrace(state.State.ToString());
                    break;
                case ESPHomeEntityStateType.SirenState:
                    break;
                case ESPHomeEntityStateType.SwitchState:
                    break;
                case ESPHomeEntityStateType.TextSensorState when (msg is TextSensorStateResponse textSensorMsg):
                    logger.LogTrace(textSensorMsg.State);
                    break;
                case ESPHomeEntityStateType.TextState when (msg is TextStateResponse textMsg):
                    logger.LogTrace(textMsg.State);
                    break;
                case ESPHomeEntityStateType.TimeState:
                    break;
                case ESPHomeEntityStateType.UpdateState:
                    break;
                case ESPHomeEntityStateType.ValveState:
                    break;
                case ESPHomeEntityStateType.WaterHeaterState:
                    break;
            }
        }
    }

    private static async Task ListienTOGeneralMessages(ILogger<EspHomeClient> logger, CancellationTokenSource shutdownCancellation, EspHomeClient espHomeClient)
    {
        while (!shutdownCancellation.IsCancellationRequested)
        {
            var msg = await espHomeClient.GetIncomingMessageAsync(shutdownCancellation.Token).ConfigureAwait(false);
            logger.LogTrace($"Recieved Message type: {msg}");
            switch (msg)
            {

            }
        }
    }


    /// <summary>Discovers the first matching ESPHome Native API service and creates a client for its endpoint.</summary>
    private static async Task<IZeroconfHost> DiscoverAsync(string serviceName = "_esphomelib._tcp.local.", string? noisePsk = null, string clientInfo = "EspClient.NET", string? password = null, TimeProvider? timeProvider = null)
    {
        while (true)
        {
            Console.WriteLine($"Scanning network for services {serviceName}");
            var response = await ZeroconfResolver.ResolveAsync(serviceName).ConfigureAwait(false);
            var service = response.FirstOrDefault();

            if (service is null) continue;
            var serviceInfo = service.Services.FirstOrDefault(x => x.Value.Name == serviceName).Value;
            if (serviceInfo is null) continue;
            return service;
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

# NetForESPHome.Client

[![NuGet](https://img.shields.io/nuget/v/NetForESPHome.Client.svg)](https://www.nuget.org/packages/NetForESPHome.Client)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

`NetForESPHome.Client` is an independent .NET client for the **ESPHome Native API**.

The library implements the ESPHome wire protocol using .NET conventions and runtime primitives. It is **not a one-to-one rewrite or port of the Python `aioesphomeapi` client**. `aioesphomeapi` is an important compatibility reference, but its Python API shape is not treated as the public API contract for this library.

The goal is protocol compatibility with ESPHome while providing an API that is natural to use from .NET applications.

### **THIS IS NOT TESTED OR PRODUCTION READY SOFTWARE.** It is a work in progress and may contain bugs, incomplete features and breaking changes.

## Table of Contents

- [Highlights](#highlights)
- [Installation](#installation)
  - [.NET CLI](#net-cli)
  - [Package Manager](#package-manager)
- [Design goals](#design-goals)
- [Project status](#project-status)
- [Currently implemented](#currently-implemented)
  - [Connection and transport](#connection-and-transport)
  - [Discovery and device information](#discovery-and-device-information)
  - [State and application messages](#state-and-application-messages)
  - [Entity commands](#entity-commands)
- [Public API](#public-api)
  - [Connection and general operations](#connection-and-general-operations)
  - [Client state](#client-state)
  - [Output messages](#output-messages)
- [Command API](#command-api)
  - [Available command methods](#available-command-methods)
  - [Nullable command arguments](#nullable-command-arguments)
  - [Light color mode](#light-color-mode)
  - [Enum parameters](#enum-parameters)
- [Simple example](#simple-example)
- [Connection model](#connection-model)
- [Raw protobuf access](#raw-protobuf-access)
- [Logging](#logging)
- [Cancellation and disposal](#cancellation-and-disposal)
- [Compatibility notes](#compatibility-notes)
- [Relationship to ESPHome, aioesphomeapi and Open Home Foundation](#relationship-to-esphome-aioesphomeapi-and-open-home-foundation)
- [License](#license)
- [Contributing and issues](#contributing-and-issues)
- [Security](#security)

## Highlights

- ESPHome Native API client for .NET
- Plaintext and Noise-encrypted connections
- Optional legacy API password authentication
- mDNS/Zeroconf discovery
- Explicit connection lifecycle and keep-alive handling
- Entity and user-service discovery
- Entity state subscriptions
- Device information and capability requests
- High-level entity command methods
- Application messages exposed through a single asynchronous output stream
- Camera frame reassembly
- Home Assistant action/state request handling
- Bluetooth proxy message handling
- Z-Wave proxy message handling
- Infrared/RF message handling
- Serial proxy message handling
- Voice assistant message handling
- Optional `ILogger<EspHomeClient>` integration
- Cancellation support for asynchronous operations
- MIT licensed

## Installation

### .NET CLI

```bash
dotnet add package NetForESPHome.Client
```

### Package Manager

```powershell
Install-Package NetForESPHome.Client
```

## Design goals

`NetForESPHome.Client` targets the **ESPHome Native API protocol**, not API compatibility with another language implementation.

The public API therefore favors .NET concepts such as:

- `Task`, `ValueTask` and `async`/`await`;
- `CancellationToken`;
- `IAsyncDisposable`;
- `ILogger<T>`;
- `TimeProvider`;
- `DateOnly`, `TimeOnly`, `DateTimeOffset` and `TimeSpan` where they provide safer public representations than raw protocol integers;
- bounded `Channel<T>` instances for producer/consumer flows;
- explicit lifecycle and RPC status values;
- high-level command methods that build the corresponding protobuf messages internally.

Protocol-specific details such as protobuf `HasXxx` fields, bit masks and time-unit conversion should normally remain inside the library instead of leaking into application code.

## Project status

The client implements a broad part of the ESPHome Native API, but **not every feature exposed by ESPHome or `aioesphomeapi` is necessarily available in every release**.

Compatibility should be understood at the protocol level:

- supported messages use ESPHome Native API framing and semantics;
- the client negotiates the Native API connection with the device;
- supported operations are intended to work with real ESPHome devices;
- the public .NET API may intentionally differ from `aioesphomeapi`;
- new ESPHome protocol features may require a newer version of this package.

## Currently implemented

### Connection and transport

- TCP connection lifecycle
- plaintext Native API transport
- Noise transport using an ESPHome PSK
- optional legacy API password authentication
- Native API hello/version negotiation
- keep-alive ping handling
- stale-connection detection
- graceful local disconnect
- remote disconnect handling
- connection-close reason reporting
- reconnect-safe connection generations
- serialized outgoing protocol writes

### Discovery and device information

- mDNS/Zeroconf discovery
- connection method detection for plaintext vs. Noise endpoints
- entity discovery
- user-defined service discovery
- device information requests
- device capability requests

### State and application messages

- entity state updates
- camera image frame assembly
- ESPHome log messages
- Home Assistant action requests
- Home Assistant state requests and subscriptions
- Bluetooth LE advertisements
- raw Bluetooth LE advertisements
- Bluetooth connection state changes
- Bluetooth GATT notifications
- Bluetooth scanner state changes
- Bluetooth connection capacity updates
- Z-Wave proxy frames and requests
- infrared/RF receive events
- serial proxy receive data
- voice assistant session, audio and announcement messages
- explicit connection-closed messages
- outgoing-message rejection notifications

### Entity commands

High-level command builders are available for:

- covers
- fans
- lights
- switches
- climate entities
- numbers
- dates
- times
- datetimes
- selects
- sirens
- buttons
- locks
- valves
- water heaters
- media players
- text entities
- update entities
- alarm control panels

## Public API

### Connection and general operations

| API | Purpose |
| --- | --- |
| `EspHomeClient(...)` | Creates a reusable client for a specific ESPHome Native API endpoint. |
| `DiscoverAsync(...)` | Waits for and returns an ESPHome Zeroconf service discovered on the local network. |
| `ConnectAsync(...)` | Opens the TCP connection, initializes the selected transport and completes the Native API handshake. |
| `DisconnectAsync(...)` | Performs a best-effort graceful ESPHome disconnect and closes the local connection. |
| `DisposeAsync()` | Permanently stops the client and releases connection resources. |
| `GetOutputMessageAsync(...)` | Waits for the next application-level message produced by the current connection. |
| `RequestEntitiesListRefresh(...)` | Requests entity descriptions and user-defined services and returns the terminal RPC status. |
| `RequestDeviceInfoRefresh(...)` | Requests current device information; the response is published through the output stream. |
| `RequestDeviceCapabilitiesRefresh(...)` | Requests current device capabilities; the response is published through the output stream. |
| `StartStatesSubscriptionAsync(...)` | Starts receiving entity state changes. |
| `WriteMessageAsync(...)` | Advanced low-level escape hatch for sending a protobuf message directly. Prefer the high-level APIs when one exists. |

### Client state

The client exposes connection information including:

- `State`
- `ApiVersion`
- `LastDisconnectReason`
- `Host`
- `Port`

### Output messages

`GetOutputMessageAsync()` returns an `EspHomeMessage`. Its `MessageType` identifies the application-level event and `Payload` contains the corresponding data.

Currently exposed message types include:

- `BluetoothAdvertisementReceived`
- `BluetoothRawAdvertisementsReceived`
- `BluetoothConnectionChanged`
- `BluetoothGattNotification`
- `EspHomeEntityStateChanged`
- `EspHomeEntitiesListReceived`
- `EspHomeEntitiesServicesListReceived`
- `EspHomeDeviceInfoReceived`
- `EspHomeDeviceCapabilitiesReceived`
- `CameraImageReceived`
- `LogReceived`
- `HomeAssistantActionRequested`
- `EspHomeRequestedState`
- `EspHomeRequestedStateSubscription`
- `BluetoothConnectionsFreeChanged`
- `BluetoothScannerStateChanged`
- `ZWaveProxyFrameReceived`
- `ZWaveProxyRequestReceived`
- `InfraredRFReceived`
- `SerialProxyDataReceived`
- `VoiceAssistantStartRequested`
- `VoiceAssistantStopRequested`
- `VoiceAssistantAudioReceived`
- `VoiceAssistantAudioStreamEnded`
- `VoiceAssistantAnnouncementFinished`
- `ConnectionClosed`
- `OutgoingMessageRejected`

Only one logical consumer should call `GetOutputMessageAsync()` for the lifetime of a client instance.

## Command API

Application code should normally use the `SendXxxCommand` methods instead of constructing ESPHome protobuf command messages directly.

This keeps protocol details inside the library. For example:

- nullable arguments control whether the corresponding ESPHome `HasXxx` field is set;
- `null` means "do not include/change this property", which is different from explicitly sending `false`, `0` or `TimeSpan.Zero`;
- .NET time types are converted to the units required by the ESPHome protocol;
- protocol bit masks such as water-heater command fields are derived internally;
- deprecated protocol fields can be hidden from the public API;
- protocol representation changes can be handled by the library without requiring every application to rebuild protobuf messages itself.

### Available command methods

| Method | Purpose |
| --- | --- |
| `SendCoverCommand(...)` | Sets cover position/tilt or stops movement. |
| `SendFanCommand(...)` | Controls fan state, speed level, oscillation, direction and preset mode. |
| `SendLightCommand(...)` | Controls light state, brightness, color, white channels, color temperature, transitions, flashes and effects. |
| `SendSwitchCommand(...)` | Sets a switch state. |
| `SendClimateCommand(...)` | Controls climate mode, target temperatures, humidity, fan mode, swing mode and presets. |
| `SendNumberCommand(...)` | Sets a number entity value. |
| `SendDateCommand(...)` | Sets a date entity using `DateOnly`. |
| `SendTimeCommand(...)` | Sets a time entity using `TimeOnly`. |
| `SendDateTimeCommand(...)` | Sets a datetime entity using `DateTimeOffset`. |
| `SendSelectCommand(...)` | Selects one of the options advertised by a select entity. |
| `SendSirenCommand(...)` | Controls siren state, tone, volume and duration. |
| `SendButtonCommand(...)` | Presses a stateless button entity. |
| `SendLockCommand(...)` | Sends a supported lock action. |
| `SendValveCommand(...)` | Sets valve position or stops movement. |
| `SendWaterHeaterCommand(...)` | Controls water-heater mode, target temperatures, away state and on/off state. |
| `SendMediaPlayerCommand(...)` | Sends media-player commands, absolute volume, media URL and announcement state. |
| `SendTextCommand(...)` | Sets a text entity value. |
| `SendUpdateCommand(...)` | Requests an update check or installation action. |
| `SendAlarmControlPanelCommand(...)` | Sends an alarm-control-panel action and optional code. |

### Nullable command arguments

Optional command values intentionally use nullable parameters.

For example:

```csharp
await client.SendLightCommand(
    key: lightKey,
    deviceId: deviceId,
    state: true,
    brightness: 0.75f,
    transition: TimeSpan.FromMilliseconds(500));
```

Only the supplied values are included in the command. Other properties are left unchanged by the request.

`null` and an explicit zero value are therefore not equivalent:

```csharp
transition: null
```

does not specify a transition duration, while:

```csharp
transition: TimeSpan.Zero
```

explicitly requests an immediate transition.

### Light color mode

`SendLightCommand(...)` accepts an optional `ColorMode`.

In most application code it should be left as `null` so ESPHome can select an appropriate mode using the supplied color values, the light's supported color modes and its current state.

Specify `ColorMode` only when the caller intentionally needs to force one of the color modes advertised by that light entity.

The command API also derives ESPHome's protocol-specific RGB color brightness from the supplied RGB channel intensities, so application code does not need to construct the protobuf representation manually.

### Enum parameters

Enums such as climate modes, fan direction, lock commands, media-player commands and water-heater modes represent an actual user intent and therefore cannot always be inferred by the library.

When an entity advertises supported values or feature flags, application code should select only values supported by that entity.

Examples include:

- `ClimateMode`, `ClimateFanMode`, `ClimateSwingMode` and `ClimatePreset`;
- `FanDirection`;
- `ColorMode` when explicitly supplied;
- `LockCommand`;
- `WaterHeaterMode`;
- `MediaPlayerCommand`;
- `UpdateCommand`;
- `AlarmControlPanelStateCommand`.

## Simple example

The following example discovers an ESPHome endpoint, detects whether it requires Noise, connects, requests the entity list, subscribes to state changes and consumes the application message stream.

```csharp
using Microsoft.Extensions.Logging;
using NetForESPHome.Client;
using NetForESPHome.Transports;
using System.Net.Sockets;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Trace)
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
});

var logger = loggerFactory.CreateLogger<EspHomeClient>();

var discoveredHost = await EspHomeClient.DiscoverAsync().ConfigureAwait(false);
var service = discoveredHost.Services.First().Value;

Console.WriteLine($"Discovered ESPHome device at {discoveredHost.IPAddress}:{service.Port}.");

using var connectionProbe = new ESPHomeClientConnectionMethodDetector(
    new TcpClient(),
    discoveredHost.IPAddress,
    service.Port);

var connectionMethod = await connectionProbe.GetConnectionMethodAsync().ConfigureAwait(false);

string? noisePsk = null;

if (connectionMethod == ConnectionMethod.Noise)
{
    Console.Write("ESPHome requires a Noise PSK: ");
    noisePsk = Console.ReadLine()?.Trim();
}

using var shutdownCancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdownCancellation.Cancel();
};

await using var client = new EspHomeClient(
    discoveredHost.IPAddress,
    service.Port,
    noisePsk,
    logger: logger);

await client.ConnectAsync(shutdownCancellation.Token).ConfigureAwait(false);

var entityListStatus = await client.RequestEntitiesListRefresh(shutdownCancellation.Token).ConfigureAwait(false);

if (entityListStatus != EspHomeRpcStatus.Completed)
{
    Console.WriteLine($"Entity discovery finished with status: {entityListStatus}");
    return;
}

await client.StartStatesSubscriptionAsync(shutdownCancellation.Token).ConfigureAwait(false);

Console.WriteLine("Connected. Press Ctrl+C to stop.");

try
{
    while (!shutdownCancellation.IsCancellationRequested)
    {
        var message = await client.GetOutputMessageAsync(shutdownCancellation.Token).ConfigureAwait(false);

        logger.LogTrace("Received ESPHome message: {MessageType}", message.MessageType);

        switch (message.MessageType)
        {
            case EspHomeMessageType.EspHomeEntityStateChanged:
                Console.WriteLine($"Entity state changed: {message.Payload}");
                break;

            case EspHomeMessageType.ConnectionClosed:
                return;
        }
    }
}
catch (OperationCanceledException) when (shutdownCancellation.IsCancellationRequested)
{
}
```

When the endpoint requires Noise encryption, pass the base64-encoded ESPHome API encryption key as `noisePsk`.

## Connection model

`EspHomeClient` separates protocol processing from application message consumption.

Internally, protocol reads and writes are handled independently. Application-facing events are published through a bounded asynchronous output channel and consumed with:

```csharp
var message = await client.GetOutputMessageAsync(cancellationToken);
```

The output stream belongs to one logical consumer. If several parts of an application need the same events, consume the client stream in one place and dispatch the messages inside the application.

Established connections report closure using an `EspHomeMessageType.ConnectionClosed` message containing an `EspHomeConnectionClosed` payload.

Possible close reasons include:

- local disconnect;
- remote disconnect;
- transport failure;
- keep-alive timeout;
- stalled application output consumer.

## Raw protobuf access

`WriteMessageAsync(IMessage)` remains available for advanced protocol work and features that do not yet have a high-level wrapper.

It should not be the default application API.

Using raw protobuf requests couples application code directly to the ESPHome protocol schema, including field-presence rules and compatibility changes. Prefer a dedicated `SendXxxCommand`, request or subscription method whenever the library exposes one.

## Logging

Logging is optional.

Pass an `ILogger<EspHomeClient>` to the client constructor:

```csharp
var client = new EspHomeClient(
    host,
    port,
    noisePsk,
    logger: logger);
```

The library uses standard `Microsoft.Extensions.Logging` abstractions and does not require a specific logging provider.

## Cancellation and disposal

Long-running client operations support `CancellationToken`.

Use `await using` when possible:

```csharp
await using var client = new EspHomeClient(host, port, noisePsk);
await client.ConnectAsync(cancellationToken);
```

`DisposeAsync()` closes the active connection, waits for protocol processing to stop and permanently completes the application output stream.

## Compatibility notes

ESPHome evolves over time and its Native API can gain new messages, fields and capabilities.

`NetForESPHome.Client` intentionally keeps protocol adaptation inside the library where practical. High-level APIs are preferred specifically so application code does not need to know how a particular ESPHome release represents presence fields, bit masks or protocol-specific units.

If a device exposes a feature that the current package does not yet support, updating the package may be required.

## Relationship to ESPHome, aioesphomeapi and Open Home Foundation

`NetForESPHome.Client` is an **independent third-party project**.

It is:

- not developed by the ESPHome project;
- not developed by or affiliated with the Open Home Foundation;
- not sponsored, endorsed or certified by the Open Home Foundation;
- not an official ESPHome client;
- not a one-to-one port of `aioesphomeapi`.

ESPHome is an Open Home Foundation project. The `NetForESPHome` name is used solely to identify that this library is intended **for interoperability with ESPHome**.

The official ESPHome "Made for ESPHome" guidelines state that a product name containing "ESPHome" may do so when the name ends with "for ESPHome". The name `NetForESPHome` follows that naming pattern, but this project does **not** claim "Made for ESPHome" certification or permission to use official ESPHome logos.

ESPHome software, documentation, names, logos and other project assets remain subject to their respective copyright, license, trademark and brand terms. The license of this project does not grant rights to ESPHome or Open Home Foundation branding or assets.

References:

- ESPHome: https://esphome.io/
- Open Home Foundation: https://www.openhomefoundation.org/
- Made for ESPHome guidelines: https://esphome.io/guides/made_for_esphome/

## License

`NetForESPHome.Client` is licensed under the **MIT License**.

The MIT license applies to this project itself. ESPHome and other third-party projects retain their own copyright and licensing terms.

See the repository `LICENSE` file for the complete license text.

## Contributing and issues

Bug reports are especially useful when they include:

- ESPHome version;
- Native API version reported by the device;
- entity or feature type involved;
- plaintext or Noise transport;
- relevant exception or log output;
- a minimal reproduction when possible.

Protocol compatibility fixes and focused additions are preferred over changes that only reproduce the shape of another client library.

## Security

Noise PSKs and legacy API passwords are credentials.

Do not commit them to source control, include them in logs, publish them in issue reports or embed production credentials in example applications.

For production applications, load credentials from an appropriate secret or configuration provider.

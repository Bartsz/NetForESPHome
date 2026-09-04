# NetForESPHome.ServerMock

`NetForESPHome.ServerMock` is a lightweight .NET mock implementation of an **ESPHome Native API endpoint**.

It is intended for automated tests, integration tests, protocol development and applications that need a controllable ESPHome-compatible endpoint without requiring physical ESPHome hardware.

The mock server is **not a complete ESPHome firmware emulator**. It implements the protocol behavior needed to reproduce common client scenarios while keeping the server deterministic, configurable and easy to embed in tests.

## Table of Contents

- [Installation](#installation)
- [Purpose](#purpose)
- [Currently implemented](#currently-implemented)
  - [Connection and transport](#connection-and-transport)
  - [Native API requests](#native-api-requests)
  - [Entities and state](#entities-and-state)
  - [Commands](#commands)
  - [Scheduled producers](#scheduled-producers)
  - [Bluetooth proxy](#bluetooth-proxy)
- [Public API](#public-api)
  - [EspHomeMockServer](#esphomemockserver)
  - [Server state](#server-state)
  - [Entity configuration](#entity-configuration)
  - [Message producers](#message-producers)
- [Simple example](#simple-example)
- [Configuring entities](#configuring-entities)
  - [Stateful entities](#stateful-entities)
  - [Entities without retained state](#entities-without-retained-state)
  - [Reading retained state](#reading-retained-state)
  - [Updating retained state](#updating-retained-state)
- [Scheduled state generation](#scheduled-state-generation)
- [Command handling](#command-handling)
  - [Built-in state updates](#built-in-state-updates)
  - [Custom command behavior](#custom-command-behavior)
  - [Global command callback](#global-command-callback)
- [Publishing protocol messages](#publishing-protocol-messages)
- [Bluetooth advertisements](#bluetooth-advertisements)
- [Noise transport](#noise-transport)
- [mDNS advertisement](#mdns-advertisement)
- [Dynamic ports](#dynamic-ports)
- [Connection model](#connection-model)
- [TimeProvider](#timeprovider)
- [Logging and diagnostics](#logging-and-diagnostics)
- [Cancellation and disposal](#cancellation-and-disposal)
- [Testing recommendations](#testing-recommendations)
- [Scope and limitations](#scope-and-limitations)
- [Relationship to ESPHome and Open Home Foundation](#relationship-to-esphome-and-open-home-foundation)
- [License](#license)
- [Contributing and issues](#contributing-and-issues)
- [Security](#security)

## Installation

### .NET CLI

```bash
dotnet add package NetForESPHome.ServerMock
```

### Package Manager

```powershell
Install-Package NetForESPHome.ServerMock
```

## Purpose

`NetForESPHome.ServerMock` provides a programmable ESPHome Native API endpoint that can be started directly inside a .NET process.

Typical uses include:

- integration-testing an ESPHome Native API client;
- testing connection and reconnection behavior;
- testing plaintext and Noise connections;
- testing entity discovery;
- testing state subscriptions;
- testing entity commands;
- simulating changing sensors;
- simulating retained device state;
- testing Bluetooth proxy advertisements;
- testing application behavior without physical ESPHome hardware;
- reproducing protocol edge cases;
- injecting selected Native API messages directly into a client session.

The server favors explicit configuration and simple deterministic behavior over attempting to reproduce the complete ESPHome firmware runtime.

## Currently implemented

### Connection and transport

The mock server currently supports:

- TCP Native API endpoint hosting;
- plaintext ESPHome Native API framing;
- ESPHome Noise transport using a PSK;
- optional legacy API password authentication;
- Native API `Hello` exchange;
- API version reporting;
- keep-alive `PingRequest` / `PingResponse`;
- graceful `DisconnectRequest` / `DisconnectResponse`;
- sequential client sessions;
- one active client session at a time;
- serialized outgoing protocol writes;
- configurable `TimeProvider`;
- optional mDNS advertisement.

A disconnected or failed client session does not stop the server. The listener remains available for another client until the server itself is stopped.

### Native API requests

The server currently handles core requests including:

- `HelloRequest`;
- legacy `AuthenticationRequest`;
- `PingRequest`;
- `DisconnectRequest`;
- `DeviceInfoRequest`;
- `DeviceCapabilitiesRequest`;
- `ListEntitiesRequest`;
- `SubscribeStatesRequest`;
- `SubscribeBluetoothLEAdvertisementsRequest`;
- `UnsubscribeBluetoothLEAdvertisementsRequest`;
- entity command messages.

Entity descriptions and configured user-service descriptions are returned during the entity-list exchange.

### Entities and state

The mock supports:

- typed ESPHome entity descriptions;
- typed retained entity state;
- entities without retained state;
- initial state publication when a client subscribes;
- direct state replacement from test code;
- automatic publication of changed retained state;
- optional periodic state generation;
- optional custom command-to-state behavior;
- multi-device addressing through `device_id`;
- validation that entity info, state and generated messages belong to the same ESPHome entity.

Entity identity is defined by:

```text
device_id + key
```

This allows the mock to represent Native API messages that use ESPHome's multi-device addressing.

### Commands

Entity command messages are automatically routed to the matching configured entity.

When a stateful entity receives a compatible command, the server can:

1. read its current retained state;
2. apply the command;
3. store the resulting state;
4. publish the updated state when a client has subscribed to states;
5. invoke the optional server-level command callback.

The built-in state updater handles common ESPHome command/state combinations including:

- covers;
- fans;
- lights;
- switches;
- climate entities;
- water heaters;
- numbers;
- selects;
- sirens;
- locks;
- media players;
- alarm control panels;
- text entities;
- date/time entities;
- valves;
- updates.

Custom entity behavior can replace the built-in state updater when a test requires different semantics.

### Scheduled producers

Stateful entity configurations can optionally generate protocol messages on a configurable schedule.

This is useful for simulating values such as:

- temperature;
- humidity;
- voltage;
- power;
- CO2;
- text sensor values;
- periodically changing state;
- randomized measurements;
- deterministic test sequences.

The delay and message generators are supplied by the application.

### Bluetooth proxy

The mock can register simulated Bluetooth LE devices and expose Bluetooth proxy capability information to the connected client.

Registered devices can generate:

- BLE advertisement payloads;
- RSSI values;
- advertisement intervals.

Advertisements begin when the connected client subscribes to ESPHome Bluetooth LE advertisements.

The current Bluetooth mock is primarily intended for **BLE advertisement and scanner/proxy testing**. It should not be treated as a complete Bluetooth GATT peripheral implementation.

## Public API

### `EspHomeMockServer`

The main server type is:

```csharp
EspHomeMockServer
```

It implements:

```csharp
IAsyncDisposable
```

### Main operations

| API | Purpose |
| --- | --- |
| `EspHomeMockServer(...)` | Creates a reusable mock server from an `EspHomeMockServerConfiguration`. |
| `StartAsync(...)` | Starts listening for ESPHome Native API clients and starts mDNS advertisement when enabled. |
| `StopAsync(...)` | Stops the listener, closes the active session and stops producers. |
| `GetEntityState(...)` | Returns a cloned retained-state snapshot for an entity. |
| `GetEntityState<TState>(...)` | Returns and validates a typed retained-state snapshot. |
| `SetEntityStateAsync(...)` | Replaces retained state and optionally publishes the new state to a subscribed client. |
| `PublishMessageAsync(...)` | Publishes an arbitrary ESPHome protobuf message to the active client. |
| `RegisterBluetoothDevice(...)` | Registers a simulated Bluetooth LE device before the server starts. |
| `DisposeAsync()` | Permanently stops and disposes the server. |

### Server state

The server exposes:

| Property | Purpose |
| --- | --- |
| `State` | Current `EspHomeMockServerState`. |
| `Port` | Actual TCP port while listening. |
| `ConnectedClientInfo` | `client_info` reported by the active client's `HelloRequest`. |
| `LastClientException` | Most recent unexpected client-session failure. |

`EspHomeMockServerState` contains:

```text
Stopped
Listening
ClientConnected
Disposed
```

A disposed server cannot be started again.

## Entity configuration

Entities are represented by `EspHomeEntityConfiguration`.

Two generic variants cover the common cases.

### Stateful entity

```csharp
EspHomeEntityConfiguration<TInfo, TState>
```

Use this for an entity that has retained state.

Examples include:

- sensors;
- switches;
- lights;
- climate entities;
- text sensors.

It accepts:

- the entity info protobuf;
- the initial state protobuf;
- an optional state generator;
- an optional delay generator;
- optional custom command behavior;
- optional state-retention behavior.

### Entity without retained state

```csharp
EspHomeEntityConfiguration<TInfo>
```

Use this for entities where retaining state is not meaningful.

A common example is a stateless button.

The entity still participates in entity discovery and can still be observed through the server-level command callback.

## Simple example

The following example exposes a switch and a periodically changing temperature sensor.

```csharp
using Microsoft.Extensions.Logging;
using NetForESPHome.ServerMock;

const uint PumpKey = 1;
const uint TemperatureKey = 2;

var pumpInfo = new ListEntitiesSwitchResponse
{
    Key = PumpKey,
    ObjectId = "pump",
    Name = "Pump"
};

var pumpState = new SwitchStateResponse
{
    Key = PumpKey,
    State = false
};

var temperatureInfo = new ListEntitiesSensorResponse
{
    Key = TemperatureKey,
    ObjectId = "temperature",
    Name = "Temperature",
    UnitOfMeasurement = "°C",
    AccuracyDecimals = 1,
    DeviceClass = "temperature",
    StateClass = SensorStateClass.StateClassMeasurement
};

var temperatureState = new SensorStateResponse
{
    Key = TemperatureKey,
    State = 22.5f
};

EspHomeEntityConfiguration[] entities =
[
    new EspHomeEntityConfiguration<ListEntitiesSwitchResponse, SwitchStateResponse>(
        pumpInfo,
        pumpState),

    new EspHomeEntityConfiguration<ListEntitiesSensorResponse, SensorStateResponse>(
        temperatureInfo,
        temperatureState,
        generateMessage: () => new SensorStateResponse
        {
            Key = TemperatureKey,
            State = 20.0f + Random.Shared.NextSingle() * 5.0f
        },
        generateDelay: () => TimeSpan.FromSeconds(2))
];

var configuration = new EspHomeMockServerConfiguration(
    name: "mock-esp32",
    entities: entities,
    port: 6053,
    friendlyName: "Mock ESP32",
    board: "esp32dev",
    network: "wifi",
    noisePsk: null,
    macAddress: "02:00:00:00:00:01",
    advertiseMdns: true);

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Debug)
        .AddSimpleConsole();
});

var logger = loggerFactory.CreateLogger<EspHomeMockServer>();

await using var server = new EspHomeMockServer(configuration, logger);

await server.StartAsync();

Console.WriteLine($"ESPHome mock server listening on port {server.Port}.");

await Task.Delay(Timeout.InfiniteTimeSpan);
```

A connected ESPHome Native API client can now:

- complete the Native API handshake;
- discover both entities;
- subscribe to state updates;
- receive the initial switch and temperature states;
- receive generated temperature updates;
- send switch commands and observe the resulting retained state.

## Configuring entities

### Stateful entities

A stateful entity requires matching entity-info and entity-state protobuf messages.

For example:

```csharp
var lightInfo = new ListEntitiesLightResponse
{
    Key = 1,
    ObjectId = "living_room_light",
    Name = "Living Room Light",
    SupportedColorModes =
    {
        ColorMode.OnOff,
        ColorMode.Brightness
    }
};

var lightState = new LightStateResponse
{
    Key = 1,
    State = false,
    ColorMode = ColorMode.OnOff
};

var light = new EspHomeEntityConfiguration<ListEntitiesLightResponse, LightStateResponse>(
    lightInfo,
    lightState);
```

The configuration validates that:

- the info message is an ESPHome entity-info response;
- the state message is an ESPHome entity-state response;
- both messages refer to the same `key`;
- both messages refer to the same `device_id`.

This prevents a generated state from accidentally being published as another entity.

### Entities without retained state

For a stateless entity, only the entity description is required:

```csharp
var buttonInfo = new ListEntitiesButtonResponse
{
    Key = 10,
    ObjectId = "restart",
    Name = "Restart"
};

var button = new EspHomeEntityConfiguration<ListEntitiesButtonResponse>(buttonInfo);
```

`GetState()` returns `null` for this configuration.

### Reading retained state

The server can return the current retained state:

```csharp
var state = server.GetEntityState(PumpKey);
```

or a typed state:

```csharp
var state = server.GetEntityState<SwitchStateResponse>(PumpKey);
```

Returned states are snapshots rather than the mutable state instance owned internally by the mock.

### Updating retained state

Tests can change state directly:

```csharp
await server.SetEntityStateAsync(new SwitchStateResponse
{
    Key = PumpKey,
    State = true
});
```

When a client has an active state subscription, the new state is also published to that client.

To update retained state without publishing it:

```csharp
await server.SetEntityStateAsync(
    new SwitchStateResponse
    {
        Key = PumpKey,
        State = true
    },
    publish: false);
```

This is useful when preparing state before a later test step.

## Scheduled state generation

`EspHomeEntityConfiguration<TInfo, TState>` implements `IMessageProducer`.

A producer consists of two operations:

```csharp
public interface IMessageProducer
{
    ValueTask WaitForDelayAsync(TimeProvider timeProvider, CancellationToken cancellationToken);

    IMessage GenerateMessage();
}
```

The stateful entity configuration exposes this behavior through the optional:

```text
generateMessage
generateDelay
```

delegates.

For example:

```csharp
var temperatureSensor = new EspHomeEntityConfiguration<ListEntitiesSensorResponse, SensorStateResponse>(
    temperatureInfo,
    temperatureState,
    generateMessage: () => new SensorStateResponse
    {
        Key = TemperatureSensorKey,
        State = 20.0f + Random.Shared.NextSingle() * 5.0f
    },
    generateDelay: () => TimeSpan.FromSeconds(2));
```

After the client subscribes to states, the server:

1. publishes the retained initial state;
2. waits for the configured delay;
3. generates a new state;
4. retains the generated state;
5. publishes it;
6. repeats until the client session ends.

Without a configured delay, the entity does not continuously produce new values.

### Random intervals

The delay itself can be generated dynamically:

```csharp
generateDelay: () => TimeSpan.FromMilliseconds(
    Random.Shared.Next(900, 1101))
```

This can be useful when testing clients against realistic sensor jitter.

For deterministic automated tests, prefer a deterministic generator or a custom `TimeProvider`.

## Command handling

### Built-in state updates

When an ESPHome command is received, the mock identifies the target entity using its `device_id` and `key`.

For compatible stateful entities, the built-in updater applies common Native API commands directly to retained state.

For example, given:

```csharp
var initialState = new SwitchStateResponse
{
    Key = 1,
    State = false
};
```

when the connected client sends a switch command with:

```text
state = true
```

the retained state becomes:

```text
state = true
```

and the new state is automatically published when the client has subscribed to states.

This makes common integration tests possible without writing command handlers for every entity.

### Custom command behavior

More complex devices can override the built-in behavior with `applyCommand`.

For example:

```csharp
var switchEntity = new EspHomeEntityConfiguration<ListEntitiesSwitchResponse, SwitchStateResponse>(
    switchInfo,
    initialState,
    applyCommand: (currentState, command) =>
    {
        if (command is not SwitchCommandRequest switchCommand)
            return null;

        currentState.State = switchCommand.State;
        return currentState;
    });
```

The callback receives an isolated copy of the current state.

Returning a new state causes that state to become the entity's retained state.

Returning `null` means that the command did not produce a state change.

When `applyCommand` is supplied, it replaces the built-in state updater for that entity.

### Global command callback

The server configuration can also define a command callback.

It is invoked after entity-specific state handling.

This is useful for:

- verifying that a command was received;
- recording commands in a test;
- implementing side effects that do not belong to retained state;
- coordinating multiple simulated entities;
- asserting command contents.

Entity-specific state mutation and global command observation are separate concepts.

## Publishing protocol messages

Tests are not limited to configured entity state.

Any ESPHome protobuf message can be published to the currently connected client:

```csharp
var message = new SubscribeLogsResponse
{
    Message = Google.Protobuf.ByteString.CopyFromUtf8("Mock log message")
};

await server.PublishMessageAsync(message);
```

`PublishMessageAsync()` uses the same serialized server write path as normal protocol responses.

It requires an active client connection.

This API is useful for:

- protocol development;
- testing uncommon message types;
- fault scenarios;
- client features that do not yet have a specialized mock abstraction.

## Bluetooth advertisements

Bluetooth devices must be registered **before** the server starts.

Example:

```csharp
var device = new EspHomeBluetoothDeviceConfiguration(
    address: 0xAABBCCDDEE01,
    generateAdvertisementData: CreateBluetoothAdvertisement,
    generateRssi: () => Random.Shared.Next(-65, -50),
    generateDelay: () => TimeSpan.FromSeconds(1));

server.RegisterBluetoothDevice(device);
```

When at least one Bluetooth device is registered, the mock advertises the corresponding Bluetooth proxy capabilities through Native API device information and capabilities.

Advertisement generation begins after the connected client subscribes to Bluetooth LE advertisements.

A generator can dynamically produce:

- raw advertisement bytes;
- RSSI;
- publication delay.

For example:

```csharp
private static byte[] CreateBluetoothAdvertisement()
{
    return
    [
        0x02, 0x01, 0x06,

        0x0A, 0x16,
        0xD2, 0xFC,
        0x40,

        0x02, 0xC4, 0x09,
        0x03, 0x88, 0x13
    ];
}
```

This can be used to test Bluetooth advertisement parsing without requiring a real Bluetooth device or ESPHome Bluetooth proxy.

## Noise transport

The mock supports the ESPHome Noise transport.

Provide a base64-encoded 32-byte PSK:

```csharp
var pskBytes = new byte[32];
Random.Shared.NextBytes(pskBytes);

var noisePsk = Convert.ToBase64String(pskBytes);
```

Then pass it to the server configuration:

```csharp
var configuration = new EspHomeMockServerConfiguration(
    name: "mock-esp32",
    entities: entities,
    port: 6053,
    friendlyName: "Mock ESP32",
    board: "esp32dev",
    network: "wifi",
    noisePsk: noisePsk,
    macAddress: "02:00:00:00:00:01",
    advertiseMdns: true);
```

When `noisePsk` is `null`, the server uses the plaintext ESPHome Native API transport.

## mDNS advertisement

The mock server can advertise itself through mDNS.

Enable advertisement through the server configuration:

```csharp
advertiseMdns: true
```

This allows discovery code to find the mock similarly to a network ESPHome endpoint.

For isolated automated tests where discovery is not under test, mDNS can be disabled and the server can be addressed directly.

## Dynamic ports

Tests running concurrently should generally avoid fixed TCP ports.

When the server configuration requests port `0`, the operating system can assign an available port.

After `StartAsync()` completes, read the actual port from:

```csharp
server.Port
```

Example:

```csharp
await server.StartAsync();

var port = server.Port;
```

This pattern reduces port collisions in parallel integration test suites.

## Connection model

The mock intentionally uses a simple connection model.

Only **one client session is active at a time**.

The lifecycle is:

```text
Stopped
   |
   v
Listening
   |
   v
ClientConnected
   |
   v
Listening
```

A client disconnect returns the server to `Listening`.

A later client may then connect using the same server instance.

Calling `StopAsync()` moves the server back to `Stopped`.

Calling `DisposeAsync()` permanently transitions it to:

```text
Disposed
```

The sequential-session design is deliberate. It keeps retained state and protocol behavior easy to reason about while still allowing reconnect scenarios to be tested.

## `TimeProvider`

The mock uses .NET `TimeProvider` for time-dependent server behavior.

It is used by scheduled message producers and internal timing operations.

This allows tests to avoid coupling directly to wall-clock time when a suitable test time provider is supplied through the server configuration.

Producer code receives the configured `TimeProvider` through:

```csharp
WaitForDelayAsync(
    TimeProvider timeProvider,
    CancellationToken cancellationToken)
```

This makes time-based simulations testable without introducing a separate scheduling abstraction.

## Logging and diagnostics

Logging is optional.

Pass an `ILogger<EspHomeMockServer>` to the server constructor:

```csharp
var server = new EspHomeMockServer(configuration, logger);
```

The mock uses standard `Microsoft.Extensions.Logging` abstractions.

Useful diagnostic properties include:

```csharp
server.State
server.ConnectedClientInfo
server.LastClientException
```

`ConnectedClientInfo` contains the `client_info` reported by the currently connected client after the Native API hello exchange.

`LastClientException` contains the most recent unexpected session error and is reset when a new client session begins.

## Cancellation and disposal

Server start and stop operations support `CancellationToken`.

Prefer `await using` for ownership:

```csharp
await using var server = new EspHomeMockServer(configuration);

await server.StartAsync(cancellationToken);
```

When the test is complete:

```csharp
await server.StopAsync(cancellationToken);
```

or allow `DisposeAsync()` to stop and release the server automatically.

Disposal:

- stops accepting connections;
- closes the active client;
- stops scheduled producers;
- withdraws mDNS advertisement;
- releases transport resources;
- permanently ends the server lifetime.

## Testing recommendations

### Use dynamic ports

Prefer:

```csharp
port: 0
```

when a test does not specifically require port `6053`.

Read the selected port from `server.Port`.

### Keep one server per test scenario

A server owns:

- retained entity state;
- connection lifecycle;
- configured entities;
- Bluetooth devices;
- producer state.

Creating one instance per independent integration test keeps tests isolated.

### Prefer deterministic producers

Random producers are useful for demonstrations, but deterministic values make automated tests easier to reproduce.

For example:

```csharp
var values = new Queue<float>(
[
    20.0f,
    21.0f,
    22.0f
]);

generateMessage: () => new SensorStateResponse
{
    Key = TemperatureSensorKey,
    State = values.Dequeue()
}
```

### Use direct state updates for test setup

When the behavior being tested does not depend on scheduling, prefer:

```csharp
await server.SetEntityStateAsync(state);
```

over adding a periodic producer.

### Use `PublishMessageAsync()` for protocol-specific scenarios

Do not add a permanent mock abstraction for every one-off test case.

When a test only needs to inject a Native API protobuf message, use:

```csharp
await server.PublishMessageAsync(message);
```

## Scope and limitations

`NetForESPHome.ServerMock` is deliberately smaller than ESPHome itself.

It is **not** intended to:

- run ESPHome YAML configuration;
- execute ESPHome firmware components;
- emulate ESP32 hardware;
- reproduce Home Assistant;
- reproduce every ESPHome Native API feature;
- emulate every entity's physical behavior;
- implement a complete Bluetooth controller or GATT stack;
- replace real-device testing where hardware-specific behavior matters.

The purpose is to provide enough Native API behavior for predictable .NET development and integration testing.

If a test requires physical semantics that are specific to a device, implement them through:

- custom entity command handling;
- scheduled producers;
- direct retained-state updates;
- the global command callback;
- direct protobuf publication.

## Relationship to ESPHome and Open Home Foundation

`NetForESPHome.ServerMock` is an **independent third-party project**.

It is:

- not developed by the ESPHome project;
- not developed by or affiliated with the Open Home Foundation;
- not sponsored, endorsed or certified by the Open Home Foundation;
- not an official ESPHome product;
- not a complete implementation of ESPHome firmware.

The `ESPHome` name is used to identify the protocol and software ecosystem with which this package is intended to interoperate.

This project does not claim official **Made for ESPHome** certification and does not grant permission to use official ESPHome or Open Home Foundation logos or branding.

ESPHome software, documentation, names, logos and other project assets remain subject to their respective copyright, license, trademark and brand terms.

## License

`NetForESPHome.ServerMock` is licensed under the **MIT License**.

The MIT License applies to this project itself.

ESPHome and other third-party projects retain their own copyright, licensing and branding terms.

See the repository `LICENSE` file for the complete license text.

## Contributing and issues

Useful bug reports should include:

- the ESPHome Native API operation being tested;
- the client implementation used against the mock;
- configured API version;
- plaintext or Noise transport;
- entity info and state protobuf types involved;
- expected behavior;
- actual behavior;
- relevant server logs;
- a minimal reproduction when possible.

Contributions should generally prefer small protocol-compatible primitives over attempting to emulate large parts of ESPHome firmware behavior.

## Security

The mock server is primarily a development and testing tool.

Noise PSKs and legacy API passwords are credentials even when used by a mock environment.

Do not:

- commit production credentials to test projects;
- publish credentials in logs;
- reuse production ESPHome Noise keys in public examples;
- expose a test mock server to untrusted networks unless that is specifically part of the test scenario.

Use generated or dedicated test credentials instead.
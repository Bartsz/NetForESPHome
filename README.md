[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

# NetForESPHome

`NetForESPHome` is an independent .NET implementation of selected parts of the **ESPHome Native API** ecosystem.

The project focuses on 
- protocol compatibility with ESPHome while 
- providing strongly typed API
- minimizing allocations (zero-allocation is not possible without giving up on protobuf deserialization into classes)

It currently contains two main projects:

- **NetForESPHome.Client** — a .NET client for connecting to ESPHome Native API devices.
- **NetForESPHome.ServerMock** — a lightweight ESPHome Native API mock server intended for development, integration testing and protocol testing.

## Projects

### NetForESPHome.Client

Provides an asynchronous .NET client with support for:

- plaintext and Noise transports;
- ESPHome connection lifecycle and keep-alive handling;
- mDNS/Zeroconf discovery;
- entity discovery and state subscriptions;
- device information and capabilities;
- high-level entity command APIs;
- Bluetooth proxy messages;
- Z-Wave, infrared/RF and serial proxy messages;
- camera and voice assistant messages;
- structured logging and cancellation.

See the full client documentation:

**[NetForESPHome.Client README](NetForESPHome.Client/README.md)**

### NetForESPHome.ServerMock

Provides a programmable ESPHome Native API endpoint for tests and development.

It supports:

- plaintext and Noise connections;
- optional legacy API authentication;
- configurable device information and entities;
- retained entity state;
- automatic handling of common entity commands;
- scheduled state producers;
- direct state updates from tests;
- direct protobuf message publication;
- mDNS advertisement;
- simulated Bluetooth LE advertisements;
- sequential client sessions.

It is intended as a deterministic testing tool rather than a complete ESPHome firmware emulator.

See the full mock server documentation:

**[NetForESPHome.ServerMock README](NetForESPHome.ServerMock/README.md)**

## Design philosophy

`NetForESPHome` targets the **ESPHome Native API protocol**, not API-shape compatibility with `aioesphomeapi`.

The Python client is an important compatibility reference, but the .NET implementation intentionally uses concepts such as:

- `Task`, `ValueTask` and `async`/`await`;
- `CancellationToken`;
- `IAsyncDisposable`;
- `ILogger<T>`;
- `TimeProvider`;
- `Channel<T>`;
- strongly typed protocol messages;
- .NET-native date and time types where appropriate.

The objective is protocol compatibility while keeping the public API natural for .NET applications.

## Repository structure

```text
NetForESPHome
├── NetForESPHome.Client
├── NetForESPHome.Client.Example
├── NetForESPHome.ServerMock
└── NetForESPHome.ServerMock.Example
```

The example projects demonstrate the intended usage of the client and mock server.

## Relationship to ESPHome

`NetForESPHome` is an **independent third-party project**.

It is not:

- developed by ESPHome;
- developed by or affiliated with the Open Home Foundation;
- sponsored or endorsed by the Open Home Foundation;
- an official ESPHome implementation;
- a one-to-one port of `aioesphomeapi`.

The ESPHome name is used only to identify the protocol and software ecosystem with which this project interoperates.

ESPHome, its name, logos, documentation and other project assets remain subject to their respective copyright, license, trademark and branding terms.

## License

This project is licensed under the **MIT License**.

ESPHome and other third-party projects retain their own copyright and licensing terms.

See the repository `LICENSE` file for details.
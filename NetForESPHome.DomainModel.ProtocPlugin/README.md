# ProtocGenEspHomeInterfaces

Minimal C# protoc plugin for ESPHome `api.proto`.

It reads the existing ESPHome custom option:

```proto
option (base_class) = "StateResponseProtoMessage";
option (base_class) = "InfoResponseProtoMessage";
```

and generates partial C# declarations implementing:

- `IESPHomeStateMessage`
- `IESPHomeEntityInfoMessage`

The plugin does not modify generated protobuf files.

## Build

```bash
dotnet build -c Debug
```

The executable apphost will be:

- Windows: `bin/Debug/net10.0/ProtocGenEspHomeInterfaces.exe`
- Linux/macOS: `bin/Debug/net10.0/ProtocGenEspHomeInterfaces`

## Plugin options

Defaults:

```text
state_interface=IESPHomeStateMessage
info_interface=IESPHomeEntityInfoMessage
access=public
```

Example:

```text
--esphome_interfaces_opt=state_interface=MyProject.IESPHomeStateMessage,info_interface=MyProject.IESPHomeEntityInfoMessage
```

See the integration snippet in the ChatGPT response for `Grpc.Tools` / MSBuild.

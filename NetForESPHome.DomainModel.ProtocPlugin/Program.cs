using Google.Protobuf;
using Google.Protobuf.Reflection;
using ProtocGenEspHomeInterfaces.Protocol;
using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;
using System.Text;

namespace NetForESPHome.DomainModel.ProtocPlugin;

internal static class Program
{
    // ESPHome api_options.proto:
    //
    // extend google.protobuf.MessageOptions {
    //     optional string base_class = 1041;
    // }
    private const int BaseClassOptionFieldNumber = 1041;

    private const int MessageIdOptionFieldNumber = 1036;


    // protobuf tag = (field_number << 3) | wire_type.
    // string uses wire type 2 (length-delimited).
    private static readonly Extension<MessageOptions, string> BaseClassOption =
        new(BaseClassOptionFieldNumber, FieldCodec.ForString(((uint)BaseClassOptionFieldNumber << 3) | 2u));

    private static readonly Extension<MessageOptions, uint> MessageIdOption =
    new(MessageIdOptionFieldNumber, FieldCodec.ForUInt32(((uint)MessageIdOptionFieldNumber << 3) | 0u));

    private const string CommandBaseClass = "CommandProtoMessage";
    private const string StateResponseBaseClass = "StateResponseProtoMessage";
    private const string EntityInfoResponseBaseClass = "InfoResponseProtoMessage";

    private const string IEspHomeStateMsgInterface = "IESPHomeStateMessage";
    private const string IEspHomeEntityInfoMsgInterface = "IESPHomeEntityInfoMessage";
    private const string IEspHomecommanfMsgInterface = "IESPHomeCommandMessage";
    private const string IESPHomeProtocolMessageInterface = "IESPHomeProtocolMessage";
    private const string IESPHomeMessageInterface = "IESPHomeMessage";


    private const string ESPHomeProtocolMessageTypeEnum = "ESPHomeProtocolMessageType";
    private const string ESPHomeMessageTypeEnum = "ESPHomeMessageType";
    private const string ESPHomeEntityStateTypeEnum = "ESPHomeEntityStateType";
    private const string ESPHomeEntityInfoTypeEnum = "ESPHomeEntityInfoType";
    private readonly static HashSet<string> _protocolMessageTypes = [
        "PingRequest",
        "PingResponse",
        "HelloResponse",
        "HelloRequest",
        "AuthenticationRequest",
        "AuthenticationResponse",
        "DisconnectRequest",
        "DisconnectResponse",
        "GetTimeRequest",
        "GetTimeResponse",
        ];

    public static int Main()
    {
        try
        {
            var extensionRegistry = new ExtensionRegistry
            {
                BaseClassOption,
                MessageIdOption
            };

            using var input = Console.OpenStandardInput();

            var request = CodeGeneratorRequest.Parser
                .WithExtensionRegistry(extensionRegistry)
                .ParseFrom(input);

            var response = Generate(request);

            using var output = Console.OpenStandardOutput();
            response.WriteTo(output);
            output.Flush();

            return 0;
        }
        catch (Exception exception)
        {
            // stdout is reserved for CodeGeneratorResponse.
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static CodeGeneratorResponse Generate(CodeGeneratorRequest request)
    {
        var response = new CodeGeneratorResponse
        {
            // FEATURE_PROTO3_OPTIONAL = 1
            SupportedFeatures = 1
        };

        var filesToGenerate = request.FileToGenerate.ToHashSet(StringComparer.Ordinal);

        foreach (var file in request.ProtoFile)
        {
            if (!filesToGenerate.Contains(file.Name)) continue;
            var csharpNamespace = GetCSharpNamespace(file);

            var messagesMetadata = ExtractMessagesMedadata(file);
            ValidateAssumptions(messagesMetadata);
            var generatedContent = GenerateFileConten(messagesMetadata, csharpNamespace);
            if (generatedContent is null) continue;

            response.File.Add(new CodeGeneratorResponseFile
            {
                Name = GetOutputFileName(file.Name),
                Content = generatedContent
            });
        }

        return response;
    }

    private static string GetOutputFileName(string protoFileName)
    {
        var fileName = protoFileName
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

        var extensionIndex = fileName.LastIndexOf('.');
        var stem = extensionIndex >= 0
            ? fileName[..extensionIndex]
            : fileName;

        // If you change file name here, you need to change it in Domain.csproj file. 
        return $"{stem}.EspHomeMessageMetadata.g.cs";
    }

    /// <summary> Throws error if any of the assumptions made about the ESPHome messages model is incorrect. </summary> 
    private static void ValidateAssumptions(List<ProtoMessageData> messagesMetadata)
    {
        var containsProtocolMessages = _protocolMessageTypes.All(p => messagesMetadata.Exists(m => m.MessageName == p));
        if (!containsProtocolMessages) throw new Exception("Could not find all protocol messages");

    }

    private static List<ProtoMessageData> ExtractMessagesMedadata(FileDescriptorProto file)
    {
        var messagesData = new List<ProtoMessageData>(160);

        foreach (var message in file.MessageType)
        {
            var messageId = message.Options?.GetExtension(MessageIdOption) ?? 0;

            if (messageId == 0) continue;

            var baseClass = message.Options?.GetExtension(BaseClassOption);

            var specializedInterface = baseClass switch
            {
                StateResponseBaseClass => IEspHomeStateMsgInterface,
                EntityInfoResponseBaseClass => IEspHomeEntityInfoMsgInterface,
                CommandBaseClass => IEspHomecommanfMsgInterface,
                _ => null
            };

            messagesData.Add((message.Name, messageId, specializedInterface));
        }

        return messagesData;
    }

    public static string? GenerateFileConten(List<ProtoMessageData> messagesData, string csharpNamespace)
    {
        if (messagesData.Count == 0) return null;

        var sb = new StringBuilder(15 * 4096);

        AppendFileHeader(sb, csharpNamespace);
        AppendIESPHomeMessageInterface(sb, messagesData);
        AppendIESPHomeProtocolMessageInterface(sb);
        AppendIESPHomeInfoMessageInterface(sb);
        AppendIESPHomeStateMessageInterface(sb);
        AppendIESPHomeCpmmandMessageInterface(sb);

        AppendIESPHomeProtocolMessageTypeEnum(sb);
        AppendMessageTypeEnum(sb, messagesData);
        AppendEntityStateTypesEnum(sb, messagesData);
        AppendEntityListingInfoEnum(sb, messagesData);

        AppendPartialClasses(sb, messagesData);

        return sb.ToString();
    }

    public static void AppendPartialClasses(StringBuilder sb, List<ProtoMessageData> messagesData)
    {
        foreach (var (messageName, msgId, interfaceName) in messagesData)
        {
            var msgInterface = interfaceName;
            var isProtocolMessage = _protocolMessageTypes.Contains(messageName);
            var protocolInterface = isProtocolMessage ? IESPHomeProtocolMessageInterface : null;
            
            var interfaces = new string?[] { IESPHomeMessageInterface, msgInterface, protocolInterface }
                .Where(x => !string.IsNullOrWhiteSpace(x));

            var additionalInterfaces = string.Join(", ", interfaces);

            sb.AppendLine(
                $$"""

                public sealed partial class {{messageName}} : {{additionalInterfaces}}
                {
                    public const int ID = {{msgId}};
                    public int MessageId => ID;
                """);


            if (interfaceName == IEspHomeEntityInfoMsgInterface)
            {
                var infoName = TranslateInfoMessageToEntityType(messageName);
                sb.AppendLine($"    public {ESPHomeEntityInfoTypeEnum} InfoType => {ESPHomeEntityInfoTypeEnum}.{infoName};");
            }

            if (interfaceName == IEspHomeStateMsgInterface)
                sb.AppendLine($"    public {ESPHomeEntityStateTypeEnum} StateType => {ESPHomeEntityStateTypeEnum}.{GetEntityStateEnumName(messageName)};");


            if (isProtocolMessage)
                sb.AppendLine($"    public {ESPHomeProtocolMessageTypeEnum} ProtocolMessageType => {ESPHomeProtocolMessageTypeEnum}.{messageName};");

            sb.AppendLine("}");
        }
    }


    public static void AppendFileHeader(StringBuilder sb, string csharpNamespace)
    {
        sb.AppendLine(
                $$"""
            // <auto-generated />
            // generated by ${{typeof(Program).FullName}}

            #pragma warning disable CS0612 // Type or member is obsolete
            #nullable enable

            {{(string.IsNullOrEmpty(csharpNamespace) ? "" : $"namespace {csharpNamespace};")}}
            """);
    }

    private static void AppendEntityStateTypesEnum(StringBuilder sb, List<ProtoMessageData> generatedTypes)
    {
        var sw = new StringWriter(sb);
        var iw = new IndentedTextWriter(sw);

        var typesSortedById = generatedTypes.Where(x => x.SpecializedInterface == IEspHomeStateMsgInterface).OrderBy(x => x.MessageName, StringComparer.OrdinalIgnoreCase);
        iw.Indent = 0;
        iw.WriteLine($"public enum {ESPHomeEntityStateTypeEnum} : byte");
        iw.WriteLine("{");
        iw.Indent = 1;
        iw.WriteLine(
            """
            /// <summary> Unknown, unsupported or not yet mapped ESPHome entity type. </summary>
                Unknown = 0,
            """);

        foreach (var type in typesSortedById)
        {
            var entityTypeName = GetEntityStateEnumName(type.MessageName);

            iw.WriteLine($"{entityTypeName},");
        }

        iw.Indent = 0;
        iw.WriteLine("}");
    }

    public static void AppendIESPHomeProtocolMessageInterface(StringBuilder sb)
    {
        sb.AppendLine(
            $$"""

            /// <summary> Messages that are directly related to EPSHome Native API protocol and will
            /// never reach the consumer of the client. A convenient marker interface to be used by `Read Messages Loop` 
            /// together with enum for exhaustive switch expression guarantee. </summary>
            public interface {{IESPHomeProtocolMessageInterface}}
            {
               {{ESPHomeProtocolMessageTypeEnum}} ProtocolMessageType { get; }
            }
            """);
    }

    public static void AppendIESPHomeProtocolMessageTypeEnum(StringBuilder sb)
    {

        sb.AppendLine(
            $$"""

            public enum {{ESPHomeProtocolMessageTypeEnum}}
            {
            """);

        foreach (var msgType in _protocolMessageTypes)
        {
            sb.AppendLine($"    {msgType},");
        }

        sb.AppendLine("}");
    }

    private static string GetEntityStateEnumName(string protoTypeName) => protoTypeName.Replace("Response", "");

    private static void AppendEntityListingInfoEnum(StringBuilder sb, List<ProtoMessageData> generatedTypes)
    {
        var sw = new StringWriter(sb);
        var iw = new IndentedTextWriter(sw);

        var typesSortedById = generatedTypes.Where(x => x.SpecializedInterface == IEspHomeEntityInfoMsgInterface).OrderBy(x => x.MessageName, StringComparer.OrdinalIgnoreCase);
        iw.Indent = 0;
        iw.WriteLine("");
        iw.WriteLine($"public enum {ESPHomeEntityInfoTypeEnum} : byte");
        iw.WriteLine("{");
        iw.Indent = 1;
        iw.WriteLine(
           """
                /// <summary> Unknown, unsupported or not yet mapped ESPHome entity info type. </summary>
                Unknown = 0,
            """);

        foreach (var type in typesSortedById)
        {
            var entityTypeName = TranslateInfoMessageToEntityType(type.MessageName);
            entityTypeSummaryByTypeName.TryGetValue(entityTypeName, out var summary);

            iw.WriteLine($"/// <summary> {summary} </summary>");
            iw.WriteLine($"{entityTypeName},");
        }

        iw.Indent = 0;
        iw.WriteLine("}");
    }

    private static void AppendIESPHomeInfoMessageInterface(StringBuilder sb)
    {
        sb.AppendLine(
            $$""" 
             
            public interface {{IEspHomeEntityInfoMsgInterface}} : {{IESPHomeMessageInterface}}
            {
                uint Key { get; set; }
                uint DeviceId { get; set; }
                string Name { get; set; }
                string Icon { get; set; }
                bool DisabledByDefault { get; set; }
                EntityCategory EntityCategory { get; set; }
                {{ESPHomeEntityInfoTypeEnum}} InfoType { get; }
            }
            """);
    }

    private static void AppendIESPHomeStateMessageInterface(StringBuilder sb)
    {
        sb.AppendLine(
            $$""" 
                                 
            public interface {{IEspHomeStateMsgInterface}} : {{IESPHomeMessageInterface}}
            {
                uint Key { get; set; }
                uint DeviceId { get; set; }
                {{ESPHomeEntityStateTypeEnum}} StateType { get; }
            }
            """);
    }

    private static void AppendIESPHomeCpmmandMessageInterface(StringBuilder sb)
    {
        sb.AppendLine(
            $$""" 
                                 
            public interface {{IEspHomecommanfMsgInterface}} : {{IESPHomeMessageInterface}}
            {
                uint Key { get; set; }
                uint DeviceId { get; set; }
     
            }
            """);
    }

    /// <summary> Generates a parse method for IESPHomeMessage interface. </summary>  
    private static void AppendIESPHomeMessageInterface(StringBuilder sb, List<ProtoMessageData> messagesData)
    {
        var typesSortedById = messagesData.OrderBy(x => x.MessageId);
        var maxMsgId = messagesData.Max(x => x.MessageId);

        sb.AppendLine(
            $$""" 

            public interface {{IESPHomeMessageInterface}} //: global::Google.Protobuf.IMessage
            {
                /// <summary> Protobuf message id. </summary>
                int MessageId { get; }

                /// <summary> Largest value of a any protobuf  message id. </summary>
                static int MaxMsgId => {{maxMsgId}}; 
          
                /// <summary> Gets the message parser and parses the bytes into the IESPHomeMessage </summary>
                static {{IESPHomeMessageInterface}} Parse(int messageId, ReadOnlySpan<byte> data)
                {
                    return messageId switch
                    {
            """);

        foreach (var (messageName, msgId, _) in typesSortedById)
        {
            sb.AppendLine(
                $$"""
                            {{msgId}} => {{messageName}}.Parser.ParseFrom(data),
                """);
        }

        sb.AppendLine(
            """
                        _ => throw new System.Runtime.CompilerServices.SwitchExpressionException($"Parser for IMessage with id {messageId} was not registered.")
                    };
                }
            }
            """);
    }

    /// <summary> Generates a an enum with all message types. </summary>  
    private static void AppendMessageTypeEnum(StringBuilder sb, List<ProtoMessageData> generatedTypes)
    {
        var typesSortedById = generatedTypes.OrderBy(x => x.MessageName, StringComparer.OrdinalIgnoreCase);

        sb.AppendLine(
            $$"""

            public enum {{ESPHomeMessageTypeEnum}}
            {
            """);

        foreach (var (messageName, msgId, _) in typesSortedById)
        {
            sb.AppendLine($"    {messageName} = {msgId},");
        }

        sb.AppendLine("}");
    }

    private static string GetCSharpNamespace(FileDescriptorProto file)
    {
        var explicitNamespace = file.Options?.CsharpNamespace;
        if (!string.IsNullOrWhiteSpace(explicitNamespace)) return explicitNamespace;
        if (string.IsNullOrWhiteSpace(file.Package)) return string.Empty;

        // This fallback is only relevant for .proto files that have a package
        // but no csharp_namespace. ESPHome api.proto currently has neither.
        return string.Join('.', file.Package.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(ToPascalCase));
    }

    private static string ToPascalCase(string value)
    {
        var result = new StringBuilder(value.Length);
        var capitalizeNext = true;

        foreach (var character in value)
        {
            if (character is '_' or '-')
            {
                capitalizeNext = true;
                continue;
            }

            result.Append(capitalizeNext ? char.ToUpperInvariant(character) : character);
            capitalizeNext = false;
        }

        return result.ToString();
    }

    /// <summary> Translates ESPHome entity type names into the naming convention used by this project 
    /// for improved consistency and semantic clarity. </summary>
    private static string TranslateInfoMessageToEntityType(string msgName) => msgName switch
    {
        "ListEntitiesAlarmControlPanelResponse" => "AlarmControlPanel",
        "ListEntitiesBinarySensorResponse" => "BinarySensor",
        "ListEntitiesButtonResponse" => "Button",
        "ListEntitiesCameraResponse" => "Camera",
        "ListEntitiesClimateResponse" => "Climate",
        "ListEntitiesCoverResponse" => "Cover",
        "ListEntitiesDateResponse" => "DateInput",
        "ListEntitiesDateTimeResponse" => "DateTimeInput",
        "ListEntitiesEventResponse" => "Event",
        "ListEntitiesFanResponse" => "Fan",
        "ListEntitiesInfraredResponse" => "Infrared",
        "ListEntitiesLightResponse" => "Light",
        "ListEntitiesLockResponse" => "Lock",
        "ListEntitiesMediaPlayerResponse" => "MediaPlayer",
        "ListEntitiesNumberResponse" => "NumericInput",
        "ListEntitiesRadioFrequencyResponse" => "RadioFrequency",
        "ListEntitiesSelectResponse" => "SelectTextInput",
        "ListEntitiesSensorResponse" => "NumericSensor",
        "ListEntitiesSirenResponse" => "Siren",
        "ListEntitiesSwitchResponse" => "BinaryInput",
        "ListEntitiesTextResponse" => "TextInput",
        "ListEntitiesTextSensorResponse" => "TextSensor",
        "ListEntitiesTimeResponse" => "TimeInput",
        "ListEntitiesUpdateResponse" => "Update",
        "ListEntitiesValveResponse" => "Valve",
        "ListEntitiesWaterHeaterResponse" => "WaterHeater",
        _ => throw new SwitchExpressionException($"New Entity Info Type message of name {msgName} is not registered for translation.")
    };
    private readonly static Dictionary<string, string> entityTypeSummaryByTypeName = new(StringComparer.OrdinalIgnoreCase)
    {
        { "NumericInput", "Writable float value with configured minimum, maximum, step, unit and device class." },
        { "BinaryInput", "Writable boolean ON/OFF state with an optional device class describing its purpose." },
        { "TimeInput", "Writable time-of-day value represented by hour, minute and second values." },
        { "DateInput", "Writable calendar date represented by year, month and day values." },
        { "DateTimeInput", "Writable date and time value represented by the Native API as Unix epoch seconds." },
        { "TextInput", "Writable string value with length limits, validation pattern and text or password mode." },
        { "SelectTextInput", "Writable string value constrained to one of the entity's advertised string options." },
        { "Button", "Stateless momentary action that can be pressed but does not expose a persistent state." },
        { "Event", "Stateless event source emitting occurrences identified by one of the advertised string event types." },
        { "NumericSensor", "Read-only float value with an optional unit, device class, accuracy and state class." },
        { "BinarySensor", "Read-only boolean state with a device class describing what the state represents." },
        { "TextSensor", "Read-only string value with an optional device class describing what the text represents." },
        { "AlarmControlPanel", "Alarm system state with arming, disarming and triggering commands and optional code support." },
        { "Siren", "Legacy controllable siren with boolean state and optional tone, duration and volume." },
        { "Cover", "Controllable cover with position, optional tilt and open, close and stop operations." },
        { "Lock", "Lock with lock state and lock, unlock and optional open operations and code requirements." },
        { "Light", "Controllable light with ON/OFF, brightness, color, color temperature and optional effects." },
        { "MediaPlayer", "Media playback entity with playback state, volume, mute state and supported media features." },
        { "Camera", "Read-only image source that provides captured image data to Native API consumers." },
        { "Valve", "Controllable valve with position, current movement operation and open, close and stop actions." },
        { "WaterHeater", "Water heater with current and target temperatures, operating mode and supported features." },
        { "Climate", "Climate controller with target temperature and mode plus optional temperature, humidity, fan, swing and preset properties." },
        { "Fan", "Controllable fan with ON/OFF state and optional speed, oscillation, direction and preset." },
        { "RadioFrequency", "Stateless raw RF transmitter or receiver with frequency range and modulation capabilities." },
        { "Infrared", "Stateless raw IR/RF signal transmitter or receiver exposing timing data and capabilities." },
        { "Update", "Software update entity exposing versions, installation progress and release information." },
    };

}

internal record struct ProtoMessageData(string MessageName, uint MessageId, string? SpecializedInterface)
{
    public static implicit operator (string MessageName, uint MessageId, string? SpecializedInterface)(ProtoMessageData value)
    {
        return (value.MessageName, value.MessageId, value.SpecializedInterface);
    }

    public static implicit operator ProtoMessageData((string MessageName, uint MessageId, string? SpecializedInterface) value)
    {
        return new ProtoMessageData(value.MessageName, value.MessageId, value.SpecializedInterface);
    }
}
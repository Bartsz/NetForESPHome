using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.Collections.Frozen;
using System.Diagnostics;

namespace NetForESPHome.DomainModel;

/// <summary>Maps generated ESPHome protobuf types to their Native API message identifiers and protocol categories.</summary>
public static class EspHomeMessageMapper
{
    private const int MessageIdCapacity = 160;
    private const int MaximumSupportedMessageId = 512;

    private static readonly MessageParser?[] MessageParsersById;
    private static readonly FrozenDictionary<Type, int> MessageIdsByType;
    private static readonly bool[] EntityInfoResponseMessages;
    private static readonly bool[] EntityStateResponseMessages;

    static EspHomeMessageMapper()
    {
        var messageParsersById = new MessageParser?[MessageIdCapacity];
        var messageIdsByType = new Dictionary<Type, int>();
        var entityInfoResponseMessages = new bool[MessageIdCapacity];
        var entityStateResponseMessages = new bool[MessageIdCapacity];

        foreach (var descriptor in ApiReflection.Descriptor.MessageTypes)
            AddMessageDescriptor(descriptor, ref messageParsersById, ref entityInfoResponseMessages, ref entityStateResponseMessages, messageIdsByType);

        MessageParsersById = messageParsersById;
        MessageIdsByType = messageIdsByType.ToFrozenDictionary();
        EntityInfoResponseMessages = entityInfoResponseMessages;
        EntityStateResponseMessages = entityStateResponseMessages;

       
        //foreach (var item in MessageParsersById)
        //{
        //    Debug.WriteLine($"Message ID: {item, 3}: {item?.GetType().Name}");
        //}

         
    }

    /// <summary>Determines whether the message describes an ESPHome entity returned by a list request.</summary>
    public static bool IsEntityInfoResponseMessage(IMessage message) => EntityInfoResponseMessages[GetMessageId(message)];

    /// <summary>Determines whether the message represents an ESPHome entity state update.</summary>
    public static bool IsEntityStateResponseMessage(IMessage message) => EntityStateResponseMessages[GetMessageId(message)];

    /// <summary>Returns the ESPHome protocol message ID assigned to the specified protobuf message.</summary>
    public static int GetMessageId(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (MessageIdsByType.TryGetValue(message.GetType(), out var messageId)) return messageId;

        throw new ArgumentOutOfRangeException(nameof(message), $"Unknown ESPHome message type: {message.GetType().FullName}.");
    }

    /// <summary>Parses an ESPHome protobuf message using its protocol message ID.</summary>
    public static IMessage ParseMessage(int messageId, ReadOnlySpan<byte> data)
    {
        var isMessageIdInRange = (uint)messageId < MessageParsersById.Length;
        if (!isMessageIdInRange)
            throw new ArgumentOutOfRangeException(nameof(messageId), messageId, "ESPHome message ID is outside the supported range.");

        var parser = MessageParsersById[messageId];
        if (parser is not null) return parser.ParseFrom(data);

        throw new ArgumentOutOfRangeException(nameof(messageId), messageId, "Unknown ESPHome message ID.");
    }

    /// <summary>Adds a protobuf message descriptor and its nested message descriptors to the runtime lookup tables.</summary>
    private static void AddMessageDescriptor(MessageDescriptor descriptor, ref MessageParser?[] messageParsersById, ref bool[] entityInfoResponseMessages, ref bool[] entityStateResponseMessages, Dictionary<Type, int> messageIdsByType)
    {
        var options = descriptor.GetOptions();
        if (options is null) return;

        var messageId = checked((int)options.GetExtension(ApiOptionsExtensions.Id));
        if (messageId > MaximumSupportedMessageId)
            throw new InvalidOperationException($"ESPHome message '{descriptor.FullName}' has unsupported ID {messageId}. The maximum supported ID is {MaximumSupportedMessageId}.");

        if (messageId >= messageParsersById.Length)
        {
            var newLength = messageId + 16;

            Array.Resize(ref messageParsersById, newLength);
            Array.Resize(ref entityInfoResponseMessages, newLength);
            Array.Resize(ref entityStateResponseMessages, newLength);
        }

        var baseClass = options.GetExtension(ApiOptionsExtensions.BaseClass);
        //Console.WriteLine($"Message id: {messageId,4}: {descriptor.Name,-30} ");

        if (baseClass == "InfoResponseProtoMessage")
            entityInfoResponseMessages[messageId] = true;

        if (baseClass == "StateResponseProtoMessage")
            entityStateResponseMessages[messageId] = true;

        if (messageId != 0)
        {
            if (messageParsersById[messageId] is not null)
                throw new InvalidOperationException($"ESPHome message ID {messageId} is assigned more than once.");

            var parser = descriptor.Parser ?? throw new InvalidOperationException($"ESPHome message '{descriptor.FullName}' has no parser.");
            var clrType = descriptor.ClrType ?? throw new InvalidOperationException($"ESPHome message '{descriptor.FullName}' has no CLR type.");

            messageParsersById[messageId] = parser;

            if (!messageIdsByType.TryAdd(clrType, messageId))
                throw new InvalidOperationException($"ESPHome message type '{clrType.FullName}' is assigned more than once.");
        }

        foreach (var nestedDescriptor in descriptor.NestedTypes)
            AddMessageDescriptor(nestedDescriptor, ref messageParsersById, ref entityInfoResponseMessages, ref entityStateResponseMessages, messageIdsByType);
    }
}
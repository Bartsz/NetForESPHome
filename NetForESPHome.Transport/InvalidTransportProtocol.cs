namespace NetForESPHome.Transports;

/// <summary>Indicates that incoming ESPHome data uses a different transport protocol than the active transport.</summary>
[Serializable]
public class InvalidTransportProtocol : Exception
{
    public readonly ConnectionMethod CurrentProtocol;

    /// <summary>Creates an exception without a detected transport protocol.</summary>
    public InvalidTransportProtocol()
    {
        CurrentProtocol = ConnectionMethod.Unknown;
    }

    /// <summary>Creates an exception for the detected connection method.</summary>
    public InvalidTransportProtocol(ConnectionMethod currentProtocol)
    {
        CurrentProtocol = currentProtocol;
    }

    /// <summary>Creates an exception with a custom message.</summary>
    public InvalidTransportProtocol(string? message) : base(message)
    {
        CurrentProtocol = ConnectionMethod.Unknown;
    }

    /// <summary>Creates an exception with a custom message and inner exception.</summary>
    public InvalidTransportProtocol(string? message, Exception? innerException) : base(message, innerException)
    {
        CurrentProtocol = ConnectionMethod.Unknown;
    }
}

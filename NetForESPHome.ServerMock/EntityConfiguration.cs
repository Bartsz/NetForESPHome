using Google.Protobuf;
using NetForESPHome.DomainModel.Native;

namespace NetForESPHome.ServerMock;

/// <summary>Describes one ESPHome entity and optionally owns its retained state and scheduled state producer.</summary>
public abstract class EspHomeEntityConfiguration
{
    /// <summary>Gets the entity description returned during a ListEntities exchange.</summary>
    public abstract IESPHomeEntityInfoMessage EntityInfo { get; }

    /// <summary>Gets the entity key used by ESPHome protocol messages.</summary>
    public uint Key => EntityInfo.Key;

    /// <summary>Gets the ESPHome device identifier associated with the entity.</summary>
    public uint DeviceId => EntityInfo.DeviceId;

    /// <summary>Gets the entity address used for state and command routing.</summary>
    internal EntityAddress Address => EntityAddress.From(EntityInfo);

    /// <summary>Gets a snapshot of the retained entity state, or null when retaining state is not meaningful for the entity.</summary>
    public abstract IESPHomeStateMessage? GetState();

    /// <summary>Replaces the retained entity state when the supplied message belongs to this entity.</summary>
    internal abstract bool TrySetState(IESPHomeStateMessage state);

    /// <summary>Applies one entity command and returns the resulting retained state when the command changes state.</summary>
    internal abstract bool TryApplyCommand(IESPHomeCommandMessage command, out IESPHomeStateMessage? state);
}

/// <summary>Describes an ESPHome entity that has no retained state, such as a button.</summary>
public sealed class EspHomeEntityConfiguration<TInfo> : EspHomeEntityConfiguration
    where TInfo : class, IESPHomeEntityInfoMessage
{
    /// <summary>Gets the typed entity description supplied to this configuration.</summary>
    public TInfo Info { get; }

    /// <inheritdoc />
    public override IESPHomeEntityInfoMessage EntityInfo => Info;

    /// <summary>Creates an entity configuration that only publishes entity metadata and forwards commands to the server callback.</summary>
    public EspHomeEntityConfiguration(TInfo entityInfo)
    {
        ArgumentNullException.ThrowIfNull(entityInfo);
        Info = entityInfo;
    }

    /// <inheritdoc />
    public override IESPHomeStateMessage? GetState() => null;

    /// <inheritdoc />
    internal override bool TrySetState(IESPHomeStateMessage state) => false;

    /// <inheritdoc />
    internal override bool TryApplyCommand(IESPHomeCommandMessage command, out IESPHomeStateMessage? state)
    {
        state = null;
        return false;
    }
}

/// <summary>Describes an ESPHome entity with typed retained state and an optional scheduled state generator.</summary>
public sealed class EspHomeEntityConfiguration<TInfo, TState> : EspHomeEntityConfiguration, IMessageProducer
    where TInfo : class, IESPHomeEntityInfoMessage
    where TState : class, IESPHomeStateMessage, IDeepCloneable<TState>
{
    private readonly Lock _stateLock = new();
    private readonly Func<TState>? _generateMessage;
    private readonly Func<TimeSpan>? _generateDelay;
    private readonly Func<TState, IESPHomeMessage, TState?>? _applyCommand;
    private readonly bool _retainState;

    private TState _state;

    /// <summary>Gets the typed entity description supplied to this configuration.</summary>
    public TInfo Info { get; }

    /// <inheritdoc />
    public override IESPHomeEntityInfoMessage EntityInfo => Info;

    /// <summary>Creates a stateful entity configuration with optional periodic generation and command-specific state mutation.</summary>
    public EspHomeEntityConfiguration(TInfo entityInfo, TState initialState, Func<TState>? generateMessage = null, Func<TimeSpan>? generateDelay = null, Func<TState, IESPHomeMessage, TState?>? applyCommand = null, bool? retainState = null)
    {
        ArgumentNullException.ThrowIfNull(entityInfo);
        ArgumentNullException.ThrowIfNull(initialState);

        var infoAddress = EntityAddress.From(entityInfo);
        var stateAddress = EntityAddress.From(initialState);
        if (infoAddress != stateAddress)
        {
            throw new ArgumentException($"Entity info address {infoAddress} does not match initial state address {stateAddress}.", nameof(initialState));
        }

        Info = entityInfo;
        _state = initialState.Clone();
        _generateMessage = generateMessage;
        _generateDelay = generateDelay;
        _applyCommand = applyCommand;
        _retainState = retainState ?? (initialState.StateType is not ESPHomeEntityStateType.Event
            and not ESPHomeEntityStateType.CameraImage);
    }

    /// <summary>Gets a typed snapshot of the retained entity state, or null when state retention is disabled.</summary>
    public TState? GetTypedState()
    {
        if (!_retainState) return null;

        lock (_stateLock)
        {
            return _state.Clone();
        }
    }

    /// <inheritdoc />
    public override IESPHomeStateMessage? GetState() => GetTypedState();

    /// <inheritdoc />
    public ValueTask WaitForDelayAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var delay = _generateDelay?.Invoke() ?? Timeout.InfiniteTimeSpan;
        ValidateDelay(delay);

        return new ValueTask(Task.Delay(delay, timeProvider, cancellationToken));
    }

    /// <inheritdoc />
    public IESPHomeMessage GenerateMessage()
    {
        lock (_stateLock)
        {
            var generatedState = _generateMessage?.Invoke() ?? _state.Clone();
            ArgumentNullException.ThrowIfNull(generatedState);

            ValidateStateAddress(generatedState);
            _state = generatedState.Clone();
            return _state.Clone();
        }
    }

    /// <summary>Replaces the typed retained state after validating that it belongs to this entity.</summary>
    public void SetState(TState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateStateAddress(state);

        lock (_stateLock)
        {
            _state = state.Clone();
        }
    }

    /// <inheritdoc />
    internal override bool TrySetState(IESPHomeStateMessage state)
    {
        if (!_retainState || state is not TState typedState) return false;

        SetState(typedState);
        return true;
    }

    /// <inheritdoc />
    internal override bool TryApplyCommand(IESPHomeCommandMessage command, out IESPHomeStateMessage? state)
    {
        ArgumentNullException.ThrowIfNull(command);
        state = null;

        if (!_retainState || EntityAddress.From(command) != Address) return false;

        lock (_stateLock)
        {
            var currentState = _state.Clone();
            var updatedState = _applyCommand is null
                ? EspHomeEntityStateUpdater.Apply(currentState, command) as TState
                : _applyCommand(currentState, command);

            if (updatedState is null) return false;

            ValidateStateAddress(updatedState);
            _state = updatedState.Clone();
            state = _state.Clone();
            return true;
        }
    }

    private static void ValidateDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
        {
            throw new InvalidOperationException($"ESPHome message producer generated an invalid delay: {delay}.");
        }
    }

    private void ValidateStateAddress(IESPHomeStateMessage state)
    {
        var actualAddress = EntityAddress.From(state);
        if (actualAddress != Address)
            throw new InvalidOperationException($"Generated state address {actualAddress} does not match entity address {Address}.");
    }
}

/// <summary>Identifies an ESPHome entity across optional multi-device Native API messages.</summary>
internal readonly record struct EntityAddress(uint DeviceId, uint Key)
{
    public static EntityAddress From(IESPHomeEntityInfoMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new EntityAddress(message.DeviceId, message.Key);
    }

    public static EntityAddress From(IESPHomeStateMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new EntityAddress(message.DeviceId, message.Key);
    }

    public static EntityAddress From(IESPHomeCommandMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new EntityAddress(message.DeviceId, message.Key);
    }

    /// <inheritdoc />
    public override string ToString() => $"{DeviceId}:{Key}";
}

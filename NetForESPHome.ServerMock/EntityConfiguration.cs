using Google.Protobuf;
using NetForESPHome.DomainModel;

namespace NetForESPHome.ServerMock;

/// <summary>Describes one ESPHome entity and optionally owns its retained state and scheduled state producer.</summary>
public abstract class EspHomeEntityConfiguration
{
    /// <summary>Gets the entity description returned during a ListEntities exchange.</summary>
    public abstract IMessage EntityInfo { get; }

    /// <summary>Gets the entity key used by ESPHome protocol messages.</summary>
    public uint Key => EntityAddress.FromMessage(EntityInfo).Key;

    /// <summary>Gets the ESPHome device identifier associated with the entity.</summary>
    public uint DeviceId => EntityAddress.FromMessage(EntityInfo).DeviceId;

    /// <summary>Gets a snapshot of the retained entity state, or null when retaining state is not meaningful for the entity.</summary>
    public abstract IMessage? GetState();

    /// <summary>Replaces the retained entity state when the supplied message belongs to this entity.</summary>
    internal abstract bool TrySetState(IMessage state);

    /// <summary>Applies one entity command and returns the resulting retained state when the command changes state.</summary>
    internal abstract bool TryApplyCommand(IMessage command, out IMessage? state);
}

/// <summary>Describes an ESPHome entity that has no retained state, such as a button.</summary>
public sealed class EspHomeEntityConfiguration<TInfo> : EspHomeEntityConfiguration where TInfo : class, IMessage<TInfo>
{
    private readonly TInfo _entityInfo;

    /// <summary>Gets the typed entity description supplied to this configuration.</summary>
    public TInfo Info => _entityInfo;

    /// <summary>Gets the entity description returned during a ListEntities exchange.</summary>
    public override IMessage EntityInfo => _entityInfo;

    /// <summary>Creates an entity configuration that only publishes entity metadata and forwards commands to the server callback.</summary>
    public EspHomeEntityConfiguration(TInfo entityInfo)
    {
        ArgumentNullException.ThrowIfNull(entityInfo);
        if (!EspHomeMessageMapper.IsEntityInfoResponseMessage(entityInfo)) throw new ArgumentException($"Message type {entityInfo.GetType().Name} is not an ESPHome entity info response.", nameof(entityInfo));

        _ = EntityAddress.FromMessage(entityInfo);
        _entityInfo = entityInfo;
    }

    /// <summary>Returns null because this entity configuration intentionally does not retain state.</summary>
    public override IMessage? GetState() => null;

    /// <summary>Rejects state replacement because this entity configuration does not retain state.</summary>
    internal override bool TrySetState(IMessage state) => false;

    /// <summary>Leaves commands unchanged because this entity configuration has no retained state to update.</summary>
    internal override bool TryApplyCommand(IMessage command, out IMessage? state)
    {
        state = null;
        return false;
    }
}

/// <summary>Describes an ESPHome entity with typed retained state and an optional scheduled state generator.</summary>
public sealed class EspHomeEntityConfiguration<TInfo, TState> : EspHomeEntityConfiguration, IMessageProducer where TInfo : class, IMessage<TInfo> where TState : class, IMessage<TState>, IDeepCloneable<TState>
{
    private readonly Lock _stateLock = new();
    private readonly TInfo _entityInfo;
    private readonly Func<TState>? _generateMessage;
    private readonly Func<TimeSpan>? _generateDelay;
    private readonly Func<TState, IMessage, TState?>? _applyCommand;
    private readonly bool _retainState;

    private TState _state;

    /// <summary>Gets the typed entity description supplied to this configuration.</summary>
    public TInfo Info => _entityInfo;

    /// <summary>Gets the entity description returned during a ListEntities exchange.</summary>
    public override IMessage EntityInfo => _entityInfo;

    /// <summary>Creates a stateful entity configuration with optional periodic generation and command-specific state mutation.</summary>
    public EspHomeEntityConfiguration(TInfo entityInfo, TState initialState, Func<TState>? generateMessage = null, Func<TimeSpan>? generateDelay = null, Func<TState, IMessage, TState?>? applyCommand = null, bool? retainState = null)
    {
        ArgumentNullException.ThrowIfNull(entityInfo);
        ArgumentNullException.ThrowIfNull(initialState);

        if (!EspHomeMessageMapper.IsEntityInfoResponseMessage(entityInfo)) throw new ArgumentException($"Message type {entityInfo.GetType().Name} is not an ESPHome entity info response.", nameof(entityInfo));
        if (!EspHomeMessageMapper.IsEntityStateResponseMessage(initialState)) throw new ArgumentException($"Message type {initialState.GetType().Name} is not an ESPHome entity state response.", nameof(initialState));

        var infoAddress = EntityAddress.FromMessage(entityInfo);
        var stateAddress = EntityAddress.FromMessage(initialState);
        if (infoAddress != stateAddress) throw new ArgumentException($"Entity info address {infoAddress} does not match initial state address {stateAddress}.", nameof(initialState));

        _entityInfo = entityInfo;
        _state = initialState.Clone();
        _generateMessage = generateMessage;
        _generateDelay = generateDelay;
        _applyCommand = applyCommand;
        _retainState = retainState ?? (initialState is not EventResponse && initialState is not CameraImageResponse);
    }

    /// <summary>Gets a typed snapshot of the retained entity state, or null when state retention is disabled.</summary>
    public TState? GetTypedState()
    {
        if (!_retainState) return null;

        lock (_stateLock)
            return _state.Clone();
    }

    /// <summary>Gets a snapshot of the retained entity state, or null when state retention is disabled.</summary>
    public override IMessage? GetState() => GetTypedState();

    /// <summary>Waits using the configured delay generator and the server time provider.</summary>
    public ValueTask WaitForDelayAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var delay = _generateDelay?.Invoke() ?? Timeout.InfiniteTimeSpan;
        var isInvalidDelay = delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan;
        if (isInvalidDelay) throw new InvalidOperationException($"ESPHome message producer generated an invalid delay: {delay}.");

        return new ValueTask(Task.Delay(delay, timeProvider, cancellationToken));
    }

    /// <summary>Generates an isolated state message and updates the producer seed used for later snapshots.</summary>
    public IMessage GenerateMessage()
    {
        lock (_stateLock)
        {
            var message = _generateMessage?.Invoke() ?? _state.Clone();
            ArgumentNullException.ThrowIfNull(message);

            ValidateStateAddress(message);
            _state = message.Clone();
            return _state.Clone();
        }
    }

    /// <summary>Replaces the typed retained state after validating that it belongs to this entity.</summary>
    public void SetState(TState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateStateAddress(state);

        lock (_stateLock)
            _state = state.Clone();
    }

    /// <summary>Replaces the retained entity state when the supplied message has the expected state type and address.</summary>
    internal override bool TrySetState(IMessage state)
    {
        if (!_retainState) return false;
        if (state is not TState typedState) return false;

        SetState(typedState);
        return true;
    }

    /// <summary>Applies a custom or built-in ESPHome command and atomically stores the resulting state.</summary>
    internal override bool TryApplyCommand(IMessage command, out IMessage? state)
    {
        ArgumentNullException.ThrowIfNull(command);
        state = null;
        if (!_retainState) return false;

        lock (_stateLock)
        {
            var currentState = _state.Clone();
            var updatedState = _applyCommand?.Invoke(currentState, command);

            if (_applyCommand is null)
            {
                var wasApplied = EspHomeEntityStateUpdater.TryApply(currentState, command, out var builtInState);
                if (!wasApplied) return false;

                updatedState = builtInState as TState;
            }

            if (updatedState is null) return false;

            ValidateStateAddress(updatedState);
            _state = updatedState.Clone();
            state = _state.Clone();
            return true;
        }
    }

    /// <summary>Ensures generated or assigned state cannot accidentally target another entity.</summary>
    private void ValidateStateAddress(TState state)
    {
        var expectedAddress = EntityAddress.FromMessage(_entityInfo);
        var actualAddress = EntityAddress.FromMessage(state);
        if (actualAddress != expectedAddress) throw new InvalidOperationException($"Generated state address {actualAddress} does not match entity address {expectedAddress}.");
    }
}

/// <summary>Identifies an ESPHome entity across optional multi-device Native API messages.</summary>
internal readonly record struct EntityAddress(uint DeviceId, uint Key)
{
    /// <summary>Reads the standard key and optional device_id fields from an ESPHome entity message.</summary>
    public static EntityAddress FromMessage(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var keyField = message.Descriptor.FindFieldByName("key");
        if (keyField is null) throw new ArgumentException($"ESPHome message type {message.GetType().Name} does not contain an entity key.", nameof(message));

        var key = (uint)keyField.Accessor.GetValue(message);
        var deviceIdField = message.Descriptor.FindFieldByName("device_id");
        var deviceId = deviceIdField is null ? 0u : (uint)deviceIdField.Accessor.GetValue(message);

        return new EntityAddress(deviceId, key);
    }

    /// <summary>Formats the address for configuration and protocol validation failures.</summary>
    public override string ToString() => $"{DeviceId}:{Key}";
}
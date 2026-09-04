namespace NetForESPHome.Client;

public sealed partial class EspHomeClient
{
    /// <summary>Sends a partial cover command while leaving every omitted property unchanged.</summary>
    /// <param name="position">Target position from 0.0 (closed) to 1.0 (open); use only when the entity supports positioning.</param>
    /// <param name="tilt">Target tilt from 0.0 to 1.0; use only when the entity supports tilt.</param>
    /// <param name="stop">Requests stopping the current movement when the entity supports stop.</param>
    public ValueTask SendCoverCommand(uint key, uint deviceId, float? position = null, float? tilt = null, bool stop = false, CancellationToken cancellationToken = default)
    {
        var command = new CoverCommandRequest
        {
            Key = key,
            DeviceId = deviceId,
            HasPosition = position.HasValue,
            Position = position ?? 0,
            HasTilt = tilt.HasValue,
            Tilt = tilt ?? 0,
            Stop = stop
        };

        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sends a partial fan command using only current, non-deprecated ESPHome fan controls.</summary>
    /// <param name="speedLevel">Speed level supported by the entity; use the reported supported speed count to determine the valid range.</param>
    /// <param name="direction">Direction requested by the user; use only when the entity reports direction support.</param>
    /// <param name="presetMode">Exact preset name reported by the entity in its supported preset modes.</param>
    public ValueTask SendFanCommand(uint key, uint deviceId, bool? state = null, int? speedLevel = null, bool? oscillating = null, FanDirection? direction = null, string? presetMode = null, CancellationToken cancellationToken = default)
    {
        var command = new FanCommandRequest
        {
            Key = key,
            DeviceId = deviceId,
            HasState = state.HasValue,
            State = state ?? false,
            HasSpeedLevel = speedLevel.HasValue,
            SpeedLevel = speedLevel ?? 0,
            HasOscillating = oscillating.HasValue,
            Oscillating = oscillating ?? false,
            HasDirection = direction.HasValue,
            Direction = direction ?? 0,
            HasPresetMode = presetMode is not null,
            PresetMode = presetMode ?? string.Empty
        };

        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sends a partial light command and derives protocol-specific RGB brightness while allowing ESPHome to infer the color mode when no explicit mode is requested.</summary>
    /// <param name="state">Requested on/off state, or null to leave it unchanged.</param>
    /// <param name="brightness">Master brightness from 0.0 to 1.0, or null to leave it unchanged.</param>
    /// <param name="colorMode">Normally null. Specify only to force one of the color modes reported by the entity when deterministic switching between overlapping modes is required.</param>
    /// <param name="rgb">Actual red, green and blue channel intensities from 0.0 to 1.0. The wrapper derives ESPHome color brightness and normalized RGB components.</param>
    /// <param name="white">White channel intensity from 0.0 to 1.0.</param>
    /// <param name="colorTemperatureMireds">Requested color temperature in mireds within the range reported by the entity.</param>
    /// <param name="coldWhite">Direct cold-white channel intensity from 0.0 to 1.0; normally unnecessary when color temperature is sufficient.</param>
    /// <param name="warmWhite">Direct warm-white channel intensity from 0.0 to 1.0; normally unnecessary when color temperature is sufficient.</param>
    /// <param name="transition">Transition duration. Null leaves the transition unspecified and allows the device default; zero explicitly requests an immediate transition.</param>
    /// <param name="flash">Flash duration, or null to avoid requesting a flash.</param>
    /// <param name="effect">Exact effect name reported by the entity; use "None" when reported by the entity to stop an active effect.</param>
    public ValueTask SendLightCommand(uint key, uint deviceId, bool? state = null, float? brightness = null, ColorMode? colorMode = null, 
        (float Red, float Green, float Blue)? rgb = null, float? white = null, float? colorTemperatureMireds = null, float? coldWhite = null, 
        float? warmWhite = null, TimeSpan? transition = null, TimeSpan? flash = null, string? effect = null, CancellationToken cancellationToken = default)
    {
        if (transition.HasValue && transition.Value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(transition), transition, "Transition cannot be negative.");
        if (flash.HasValue && flash.Value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(flash), flash, "Flash duration cannot be negative.");

        var rgbValue = rgb ?? (0f, 0f, 0f);
        var rgbBrightness = rgb.HasValue ? Math.Max(rgbValue.Red, Math.Max(rgbValue.Green, rgbValue.Blue)) : 0f;
        var rgbNormalizationFactor = rgbBrightness > 0f ? 1f / rgbBrightness : 0f;

        var transitionLengthMilliseconds = transition.HasValue ? checked((uint)Math.Round(transition.Value.TotalMilliseconds)) : 0u;
        var flashLengthMilliseconds = flash.HasValue ? checked((uint)Math.Round(flash.Value.TotalMilliseconds)) : 0u;

        var command = new LightCommandRequest
        {
            Key = key,
            DeviceId = deviceId,

            HasState = state.HasValue,
            State = state ?? false,

            HasBrightness = brightness.HasValue,
            Brightness = brightness ?? 0,

            HasColorMode = colorMode.HasValue,
            ColorMode = colorMode ?? 0,

            HasColorBrightness = rgb.HasValue,
            ColorBrightness = rgbBrightness,

            HasRgb = rgb.HasValue,
            Red = rgbValue.Red * rgbNormalizationFactor,
            Green = rgbValue.Green * rgbNormalizationFactor,
            Blue = rgbValue.Blue * rgbNormalizationFactor,

            HasWhite = white.HasValue,
            White = white ?? 0,

            HasColorTemperature = colorTemperatureMireds.HasValue,
            ColorTemperature = colorTemperatureMireds ?? 0,

            HasColdWhite = coldWhite.HasValue,
            ColdWhite = coldWhite ?? 0,

            HasWarmWhite = warmWhite.HasValue,
            WarmWhite = warmWhite ?? 0,

            HasTransitionLength = transition.HasValue,
            TransitionLength = transitionLengthMilliseconds,

            HasFlashLength = flash.HasValue,
            FlashLength = flashLengthMilliseconds,

            HasEffect = effect is not null,
            Effect = effect ?? string.Empty
        };

        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sets the requested switch state.</summary>
    public ValueTask SendSwitchCommand(uint key, uint deviceId, bool state, CancellationToken cancellationToken = default)
    {
        var command = new SwitchCommandRequest { Key = key, DeviceId = deviceId, State = state };
        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sends a partial climate command while preserving every omitted setting.</summary>
    /// <param name="mode">Operating mode selected from the entity's supported modes.</param>
    /// <param name="targetTemperature">Single target temperature in the unit reported by the entity; use for single-point climate entities.</param>
    /// <param name="targetTemperatureLow">Lower target temperature for a two-point target range.</param>
    /// <param name="targetTemperatureHigh">Upper target temperature for a two-point target range.</param>
    /// <param name="fanMode">Standard fan mode selected from the entity's supported fan modes; cannot be combined with customFanMode.</param>
    /// <param name="swingMode">Swing mode selected from the entity's supported swing modes.</param>
    /// <param name="customFanMode">Exact custom fan mode reported by the entity; cannot be combined with fanMode.</param>
    /// <param name="preset">Standard preset selected from the entity's supported presets; cannot be combined with customPreset.</param>
    /// <param name="customPreset">Exact custom preset reported by the entity; cannot be combined with preset.</param>
    /// <param name="targetHumidity">Target relative humidity supported by the entity.</param>
    public ValueTask SendClimateCommand(uint key, uint deviceId, ClimateMode? mode = null, float? targetTemperature = null, float? targetTemperatureLow = null, float? targetTemperatureHigh = null, ClimateFanMode? fanMode = null, ClimateSwingMode? swingMode = null, string? customFanMode = null, ClimatePreset? preset = null, string? customPreset = null, float? targetHumidity = null, CancellationToken cancellationToken = default)
    {
        var hasConflictingFanModes = fanMode.HasValue && customFanMode is not null;
        if (hasConflictingFanModes) throw new ArgumentException("Standard and custom fan modes cannot be sent in the same command.");

        var hasConflictingPresets = preset.HasValue && customPreset is not null;
        if (hasConflictingPresets) throw new ArgumentException("Standard and custom presets cannot be sent in the same command.");

        var command = new ClimateCommandRequest
        {
            Key = key,
            DeviceId = deviceId,

            HasMode = mode.HasValue,
            Mode = mode ?? 0,

            HasTargetTemperature = targetTemperature.HasValue,
            TargetTemperature = targetTemperature ?? 0,

            HasTargetTemperatureLow = targetTemperatureLow.HasValue,
            TargetTemperatureLow = targetTemperatureLow ?? 0,

            HasTargetTemperatureHigh = targetTemperatureHigh.HasValue,
            TargetTemperatureHigh = targetTemperatureHigh ?? 0,

            HasFanMode = fanMode.HasValue,
            FanMode = fanMode ?? 0,

            HasSwingMode = swingMode.HasValue,
            SwingMode = swingMode ?? 0,

            HasCustomFanMode = customFanMode is not null,
            CustomFanMode = customFanMode ?? string.Empty,

            HasPreset = preset.HasValue,
            Preset = preset ?? 0,

            HasCustomPreset = customPreset is not null,
            CustomPreset = customPreset ?? string.Empty,

            HasTargetHumidity = targetHumidity.HasValue,
            TargetHumidity = targetHumidity ?? 0
        };

        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sets a number entity to the requested value.</summary>
    /// <param name="value">Value within the min, max and step constraints reported by the entity.</param>
    public ValueTask SendNumberCommand(uint key, uint deviceId, float value, CancellationToken cancellationToken = default)
    {
        var command = new NumberCommandRequest { Key = key, DeviceId = deviceId, State = value };
        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sets a date entity using the platform date type instead of exposing protocol date fields.</summary>
    public ValueTask SendDateCommand(uint key, uint deviceId, DateOnly date, CancellationToken cancellationToken = default)
    {
        var command = new DateCommandRequest
        {
            Key = key,
            DeviceId = deviceId,
            Year = (uint)date.Year,
            Month = (uint)date.Month,
            Day = (uint)date.Day
        };

        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sets a time entity using the platform time type; sub-second precision is discarded because ESPHome transmits whole seconds.</summary>
    public ValueTask SendTimeCommand(uint key, uint deviceId, TimeOnly time, CancellationToken cancellationToken = default)
    {
        var command = new TimeCommandRequest
        {
            Key = key,
            DeviceId = deviceId,
            Hour = (uint)time.Hour,
            Minute = (uint)time.Minute,
            Second = (uint)time.Second
        };

        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sets a datetime entity and converts the supplied instant to the unsigned Unix timestamp used by ESPHome.</summary>
    /// <param name="dateTime">Instant representable by the protocol's unsigned 32-bit Unix timestamp.</param>
    public ValueTask SendDateTimeCommand(uint key, uint deviceId, DateTimeOffset dateTime, CancellationToken cancellationToken = default)
    {
        var unixTimeSeconds = dateTime.ToUnixTimeSeconds();
        var isOutsideProtocolRange = unixTimeSeconds is < 0 or > uint.MaxValue;
        if (isOutsideProtocolRange) throw new ArgumentOutOfRangeException(nameof(dateTime), dateTime, "ESPHome datetime must fit into an unsigned 32-bit Unix timestamp.");

        var command = new DateTimeCommandRequest { Key = key, DeviceId = deviceId, EpochSeconds = (uint)unixTimeSeconds };
        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Selects one of the options advertised by a select entity.</summary>
    /// <param name="option">Exact option string reported by the entity.</param>
    public ValueTask SendSelectCommand(uint key, uint deviceId, string option, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(option);

        var command = new SelectCommandRequest { Key = key, DeviceId = deviceId, State = option };
        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sends a partial siren command while preserving omitted siren settings.</summary>
    /// <param name="tone">Exact tone name reported by the entity.</param>
    /// <param name="volume">Volume from 0.0 to 1.0 when volume control is supported.</param>
    /// <param name="duration">Requested duration; ESPHome represents this field as a whole number of seconds.</param>
    public ValueTask SendSirenCommand(uint key, uint deviceId, bool? state = null, string? tone = null, float? volume = null, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        if (duration.HasValue && duration.Value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration cannot be negative.");

        var durationSeconds = duration.HasValue ? checked((uint)Math.Round(duration.Value.TotalSeconds)) : 0u;

        var command = new SirenCommandRequest
        {
            Key = key,
            DeviceId = deviceId,
            HasState = state.HasValue,
            State = state ?? false,
            HasTone = tone is not null,
            Tone = tone ?? string.Empty,
            HasVolume = volume.HasValue,
            Volume = volume ?? 0,
            HasDuration = duration.HasValue,
            Duration = durationSeconds
        };

        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Presses the stateless button represented by the specified entity key.</summary>
    public ValueTask SendButtonCommand(uint key, uint deviceId, CancellationToken cancellationToken = default)
    {
        var command = new ButtonCommandRequest { Key = key, DeviceId = deviceId };
        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sends the requested lock action; protocol code fields are intentionally omitted until ESPHome implements them.</summary>
    /// <param name="command">Unlock, Lock, or Open; Open must only be used when the entity reports open support.</param>
    public ValueTask SendLockCommand(uint key, uint deviceId, LockCommand command, CancellationToken cancellationToken = default)
    {
        var request = new LockCommandRequest { Key = key, DeviceId = deviceId, Command = command };
        return WriteMessageAsync(request, cancellationToken);
    }

    /// <summary>Sends a partial valve command while leaving an omitted position unchanged.</summary>
    /// <param name="position">Target position from 0.0 to 1.0 when position control is supported.</param>
    /// <param name="stop">Requests stopping the current movement when stop is supported.</param>
    public ValueTask SendValveCommand(uint key, uint deviceId, float? position = null, bool stop = false, CancellationToken cancellationToken = default)
    {
        var command = new ValveCommandRequest
        {
            Key = key,
            DeviceId = deviceId,
            HasPosition = position.HasValue,
            Position = position ?? 0,
            Stop = stop
        };

        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sends a partial water-heater command and derives the protocol presence and state bitmasks from the supplied nullable values.</summary>
    /// <param name="mode">Operating mode selected from the entity's supported modes.</param>
    /// <param name="targetTemperature">Single target temperature in the unit reported by the entity.</param>
    /// <param name="targetTemperatureLow">Lower target for an entity supporting a two-point temperature range.</param>
    /// <param name="targetTemperatureHigh">Upper target for an entity supporting a two-point temperature range.</param>
    /// <param name="away">Explicitly enables or disables away state; null leaves it unchanged.</param>
    /// <param name="on">Explicitly enables or disables on state; null leaves it unchanged.</param>
    public ValueTask SendWaterHeaterCommand(uint key, uint deviceId, WaterHeaterMode? mode = null, float? targetTemperature = null, float? targetTemperatureLow = null, float? targetTemperatureHigh = null, bool? away = null, bool? on = null, CancellationToken cancellationToken = default)
    {
        const uint ModeField = 1u << 0;
        const uint TargetTemperatureField = 1u << 1;
        const uint TargetTemperatureLowField = 1u << 3;
        const uint TargetTemperatureHighField = 1u << 4;
        const uint OnStateField = 1u << 5;
        const uint AwayStateField = 1u << 6;

        const uint AwayState = 1u << 0;
        const uint OnState = 1u << 1;

        var hasFields = 0u;
        if (mode.HasValue) hasFields |= ModeField;
        if (targetTemperature.HasValue) hasFields |= TargetTemperatureField;
        if (targetTemperatureLow.HasValue) hasFields |= TargetTemperatureLowField;
        if (targetTemperatureHigh.HasValue) hasFields |= TargetTemperatureHighField;
        if (on.HasValue) hasFields |= OnStateField;
        if (away.HasValue) hasFields |= AwayStateField;

        var state = 0u;
        if (away == true) state |= AwayState;
        if (on == true) state |= OnState;

        var command = new WaterHeaterCommandRequest
        {
            Key = key,
            DeviceId = deviceId,
            HasFields = hasFields,
            Mode = mode ?? 0,
            TargetTemperature = targetTemperature ?? 0,
            TargetTemperatureLow = targetTemperatureLow ?? 0,
            TargetTemperatureHigh = targetTemperatureHigh ?? 0,
            State = state
        };

        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Sends any supported combination of media-player transport, volume and media playback settings.</summary>
    /// <param name="command">Optional transport or playback command selected by the caller according to the entity's supported features.</param>
    /// <param name="volume">Absolute volume from 0.0 to 1.0.</param>
    /// <param name="mediaUrl">Media URL understood by the target media player.</param>
    /// <param name="announcement">Marks media playback as an announcement when the entity supports announcements.</param>
    public ValueTask SendMediaPlayerCommand(uint key, uint deviceId, MediaPlayerCommand? command = null, float? volume = null, string? mediaUrl = null, bool? announcement = null, CancellationToken cancellationToken = default)
    {
        var request = new MediaPlayerCommandRequest
        {
            Key = key,
            DeviceId = deviceId,
            HasCommand = command.HasValue,
            Command = command ?? 0,
            HasVolume = volume.HasValue,
            Volume = volume ?? 0,
            HasMediaUrl = mediaUrl is not null,
            MediaUrl = mediaUrl ?? string.Empty,
            HasAnnouncement = announcement.HasValue,
            Announcement = announcement ?? false
        };

        return WriteMessageAsync(request, cancellationToken);
    }

    /// <summary>Sets a text entity after leaving validation of its advertised length and pattern constraints to the caller.</summary>
    /// <param name="value">Text satisfying the min length, max length and pattern reported by the entity.</param>
    public ValueTask SendTextCommand(uint key, uint deviceId, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        var command = new TextCommandRequest { Key = key, DeviceId = deviceId, State = value };
        return WriteMessageAsync(command, cancellationToken);
    }

    /// <summary>Requests checking for or installing an update and rejects the protocol's non-action None value.</summary>
    /// <param name="command">Use Update to install the available update or Check to refresh update information; None is not a valid action.</param>
    public ValueTask SendUpdateCommand(uint key, uint deviceId, UpdateCommand command, CancellationToken cancellationToken = default)
    {
        var isActionCommand = (int)command != 0 && Enum.IsDefined(command);
        if (!isActionCommand) throw new ArgumentOutOfRangeException(nameof(command), command, "Update command must represent an actionable ESPHome update operation.");

        var request = new UpdateCommandRequest { Key = key, DeviceId = deviceId, Command = command };
        return WriteMessageAsync(request, cancellationToken);
    }

    /// <summary>Sends an alarm-panel action together with the optional code required by the target panel.</summary>
    /// <param name="command">Disarm, one of the supported arm modes, or Trigger; arm modes must be supported by the entity.</param>
    /// <param name="code">Code required according to the entity's RequiresCode and RequiresCodeToArm properties, or null when no code is required.</param>
    public ValueTask SendAlarmControlPanelCommand(uint key, uint deviceId, AlarmControlPanelStateCommand command, string? code = null, CancellationToken cancellationToken = default)
    {
        var request = new AlarmControlPanelCommandRequest
        {
            Key = key,
            DeviceId = deviceId,
            Command = command,
            Code = code ?? string.Empty
        };

        return WriteMessageAsync(request, cancellationToken);
    }
}
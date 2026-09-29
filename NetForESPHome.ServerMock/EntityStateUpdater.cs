using NetForESPHome.DomainModel.Native;

namespace NetForESPHome.ServerMock;

/// <summary>Applies common ESPHome command messages to retained mock entity state without requiring per-entity callbacks.</summary>
internal static class EspHomeEntityStateUpdater
{
    /// <summary>Applies a supported command to the supplied cloned state and returns the resulting state message.</summary>
    public static bool TryApply(IESPHomeMessage currentState, IESPHomeMessage command, out IESPHomeMessage? updatedState)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(command);

        updatedState = currentState switch
        {
            CoverStateResponse state when command is CoverCommandRequest request => ApplyCoverCommand(state, request),
            FanStateResponse state when command is FanCommandRequest request => ApplyFanCommand(state, request),
            LightStateResponse state when command is LightCommandRequest request => ApplyLightCommand(state, request),
            SwitchStateResponse state when command is SwitchCommandRequest request => ApplySwitchCommand(state, request),
            ClimateStateResponse state when command is ClimateCommandRequest request => ApplyClimateCommand(state, request),
            WaterHeaterStateResponse state when command is WaterHeaterCommandRequest request => ApplyWaterHeaterCommand(state, request),
            NumberStateResponse state when command is NumberCommandRequest request => ApplyNumberCommand(state, request),
            SelectStateResponse state when command is SelectCommandRequest request => ApplySelectCommand(state, request),
            SirenStateResponse state when command is SirenCommandRequest request => ApplySirenCommand(state, request),
            LockStateResponse state when command is LockCommandRequest request => ApplyLockCommand(state, request),
            MediaPlayerStateResponse state when command is MediaPlayerCommandRequest request => ApplyMediaPlayerCommand(state, request),
            AlarmControlPanelStateResponse state when command is AlarmControlPanelCommandRequest request => ApplyAlarmControlPanelCommand(state, request),
            TextStateResponse state when command is TextCommandRequest request => ApplyTextCommand(state, request),
            DateStateResponse state when command is DateCommandRequest request => ApplyDateCommand(state, request),
            TimeStateResponse state when command is TimeCommandRequest request => ApplyTimeCommand(state, request),
            ValveStateResponse state when command is ValveCommandRequest request => ApplyValveCommand(state, request),
            DateTimeStateResponse state when command is DateTimeCommandRequest request => ApplyDateTimeCommand(state, request),
            UpdateStateResponse state when command is UpdateCommandRequest request => ApplyUpdateCommand(state, request),
            _ => null
        };

        return updatedState is not null;
    }

    /// <summary>Applies position, tilt and stop commands while leaving physical movement simulation to user generators.</summary>
    private static CoverStateResponse ApplyCoverCommand(CoverStateResponse state, CoverCommandRequest request)
    {
        if (request.HasPosition) state.Position = request.Position;
        if (request.HasTilt) state.Tilt = request.Tilt;
        if (request.Stop || request.HasPosition || request.HasTilt) state.CurrentOperation = (CoverOperation)0;
        return state;
    }

    /// <summary>Applies modern fan command fields represented by FanStateResponse.</summary>
    private static FanStateResponse ApplyFanCommand(FanStateResponse state, FanCommandRequest request)
    {
        if (request.HasState) state.State = request.State;
        if (request.HasOscillating) state.Oscillating = request.Oscillating;
        if (request.HasDirection) state.Direction = request.Direction;
        if (request.HasSpeedLevel) state.SpeedLevel = request.SpeedLevel;
        if (request.HasPresetMode) state.PresetMode = request.PresetMode;
        return state;
    }

    /// <summary>Applies light fields that have a direct representation in LightStateResponse.</summary>
    private static LightStateResponse ApplyLightCommand(LightStateResponse state, LightCommandRequest request)
    {
        if (request.HasState) state.State = request.State;
        if (request.HasBrightness) state.Brightness = request.Brightness;
        if (request.HasColorMode) state.ColorMode = request.ColorMode;
        if (request.HasColorBrightness) state.ColorBrightness = request.ColorBrightness;

        if (request.HasRgb)
        {
            state.Red = request.Red;
            state.Green = request.Green;
            state.Blue = request.Blue;
        }

        if (request.HasWhite) state.White = request.White;
        if (request.HasColorTemperature) state.ColorTemperature = request.ColorTemperature;
        if (request.HasColdWhite) state.ColdWhite = request.ColdWhite;
        if (request.HasWarmWhite) state.WarmWhite = request.WarmWhite;
        if (request.HasEffect) state.Effect = request.Effect;

        return state;
    }

    /// <summary>Applies a switch command directly to its retained boolean state.</summary>
    private static SwitchStateResponse ApplySwitchCommand(SwitchStateResponse state, SwitchCommandRequest request)
    {
        state.State = request.State;
        return state;
    }

    /// <summary>Applies climate command fields represented by ClimateStateResponse.</summary>
    private static ClimateStateResponse ApplyClimateCommand(ClimateStateResponse state, ClimateCommandRequest request)
    {
        if (request.HasMode) state.Mode = request.Mode;
        if (request.HasTargetTemperature) state.TargetTemperature = request.TargetTemperature;
        if (request.HasTargetTemperatureLow) state.TargetTemperatureLow = request.TargetTemperatureLow;
        if (request.HasTargetTemperatureHigh) state.TargetTemperatureHigh = request.TargetTemperatureHigh;
        if (request.HasFanMode) state.FanMode = request.FanMode;
        if (request.HasSwingMode) state.SwingMode = request.SwingMode;
        if (request.HasCustomFanMode) state.CustomFanMode = request.CustomFanMode;
        if (request.HasPreset) state.Preset = request.Preset;
        if (request.HasCustomPreset) state.CustomPreset = request.CustomPreset;
        if (request.HasTargetHumidity) state.TargetHumidity = request.TargetHumidity;
        return state;
    }

    /// <summary>Applies WaterHeaterCommandRequest has_fields bits and preserves unspecified state flags.</summary>
    private static WaterHeaterStateResponse ApplyWaterHeaterCommand(WaterHeaterStateResponse state, WaterHeaterCommandRequest request)
    {
        const uint HasMode = 1;
        const uint HasTargetTemperature = 2;
        const uint HasLegacyState = 4;
        const uint HasTargetTemperatureLow = 8;
        const uint HasTargetTemperatureHigh = 16;
        const uint HasOnState = 32;
        const uint HasAwayState = 64;
        const uint AwayStateFlag = 1;
        const uint OnStateFlag = 2;

        if ((request.HasFields & HasMode) != 0) state.Mode = request.Mode;
        if ((request.HasFields & HasTargetTemperature) != 0) state.TargetTemperature = request.TargetTemperature;
        if ((request.HasFields & HasTargetTemperatureLow) != 0) state.TargetTemperatureLow = request.TargetTemperatureLow;
        if ((request.HasFields & HasTargetTemperatureHigh) != 0) state.TargetTemperatureHigh = request.TargetTemperatureHigh;
        if ((request.HasFields & HasLegacyState) != 0) state.State = request.State;

        if ((request.HasFields & HasOnState) != 0)
            state.State = CopyFlag(state.State, request.State, OnStateFlag);

        if ((request.HasFields & HasAwayState) != 0)
            state.State = CopyFlag(state.State, request.State, AwayStateFlag);

        return state;
    }

    /// <summary>Copies one state bit from a command mask into retained state.</summary>
    private static uint CopyFlag(uint currentValue, uint sourceValue, uint flag)
    {
        if ((sourceValue & flag) != 0) return currentValue | flag;
        return currentValue & ~flag;
    }

    /// <summary>Applies a numeric value and marks the state as available.</summary>
    private static NumberStateResponse ApplyNumberCommand(NumberStateResponse state, NumberCommandRequest request)
    {
        state.State = request.State;
        state.MissingState = false;
        return state;
    }

    /// <summary>Applies a selected option and marks the state as available.</summary>
    private static SelectStateResponse ApplySelectCommand(SelectStateResponse state, SelectCommandRequest request)
    {
        state.State = request.State;
        state.MissingState = false;
        return state;
    }

    /// <summary>Applies the siren on/off field when it is present in the command.</summary>
    private static SirenStateResponse ApplySirenCommand(SirenStateResponse state, SirenCommandRequest request)
    {
        if (request.HasState) state.State = request.State;
        return state;
    }

    /// <summary>Maps lock commands to the stable state a simple mock reaches immediately.</summary>
    private static LockStateResponse ApplyLockCommand(LockStateResponse state, LockCommandRequest request)
    {
        state.State = (int)request.Command switch
        {
            0 => (LockState)2,
            1 => (LockState)1,
            2 => (LockState)7,
            _ => state.State
        };

        return state;
    }

    /// <summary>Applies media player command and volume fields without simulating playback progression.</summary>
    private static MediaPlayerStateResponse ApplyMediaPlayerCommand(MediaPlayerStateResponse state, MediaPlayerCommandRequest request)
    {
        if (request.HasVolume) state.Volume = Math.Clamp(request.Volume, 0f, 1f);
        if (!request.HasCommand) return state;

        switch ((int)request.Command)
        {
            case 0:
                state.State = (MediaPlayerState)2;
                break;
            case 1:
                state.State = (MediaPlayerState)3;
                break;
            case 2:
                state.State = (MediaPlayerState)1;
                break;
            case 3:
                state.Muted = true;
                break;
            case 4:
                state.Muted = false;
                break;
            case 5:
                state.State = (int)state.State == 2 ? (MediaPlayerState)3 : (MediaPlayerState)2;
                break;
            case 6:
                state.Volume = Math.Clamp(state.Volume + 0.1f, 0f, 1f);
                break;
            case 7:
                state.Volume = Math.Clamp(state.Volume - 0.1f, 0f, 1f);
                break;
            case 12:
                state.State = (MediaPlayerState)6;
                break;
            case 13:
                state.State = (MediaPlayerState)5;
                break;
        }

        return state;
    }

    /// <summary>Maps alarm control commands to the stable panel state reached by a simple mock.</summary>
    private static AlarmControlPanelStateResponse ApplyAlarmControlPanelCommand(AlarmControlPanelStateResponse state, AlarmControlPanelCommandRequest request)
    {
        state.State = (int)request.Command switch
        {
            0 => (AlarmControlPanelState)0,
            1 => (AlarmControlPanelState)2,
            2 => (AlarmControlPanelState)1,
            3 => (AlarmControlPanelState)3,
            4 => (AlarmControlPanelState)4,
            5 => (AlarmControlPanelState)5,
            6 => (AlarmControlPanelState)9,
            _ => state.State
        };

        return state;
    }

    /// <summary>Applies a text command and marks the state as available.</summary>
    private static TextStateResponse ApplyTextCommand(TextStateResponse state, TextCommandRequest request)
    {
        state.State = request.State;
        state.MissingState = false;
        return state;
    }

    /// <summary>Applies a calendar date command and marks the state as available.</summary>
    private static DateStateResponse ApplyDateCommand(DateStateResponse state, DateCommandRequest request)
    {
        state.Year = request.Year;
        state.Month = request.Month;
        state.Day = request.Day;
        state.MissingState = false;
        return state;
    }

    /// <summary>Applies a time-of-day command and marks the state as available.</summary>
    private static TimeStateResponse ApplyTimeCommand(TimeStateResponse state, TimeCommandRequest request)
    {
        state.Hour = request.Hour;
        state.Minute = request.Minute;
        state.Second = request.Second;
        state.MissingState = false;
        return state;
    }

    /// <summary>Applies valve position and stop commands while leaving movement simulation to user generators.</summary>
    private static ValveStateResponse ApplyValveCommand(ValveStateResponse state, ValveCommandRequest request)
    {
        if (request.HasPosition) state.Position = request.Position;
        if (request.Stop || request.HasPosition) state.CurrentOperation = (ValveOperation)0;
        return state;
    }

    /// <summary>Applies an epoch value and marks the datetime state as available.</summary>
    private static DateTimeStateResponse ApplyDateTimeCommand(DateTimeStateResponse state, DateTimeCommandRequest request)
    {
        state.EpochSeconds = request.EpochSeconds;
        state.MissingState = false;
        return state;
    }

    /// <summary>Starts a simple in-progress update for update commands while leaving version evolution to user generators.</summary>
    private static UpdateStateResponse ApplyUpdateCommand(UpdateStateResponse state, UpdateCommandRequest request)
    {
        if ((int)request.Command == 1)
        {
            state.InProgress = true;
            state.MissingState = false;
        }

        return state;
    }
}
using NetForESPHome.DomainModel.Native;

namespace NetForESPHome.ServerMock;

/// <summary>Applies common ESPHome command messages to retained mock entity state without requiring per-entity callbacks.</summary>
internal static class EspHomeEntityStateUpdater
{
    private const uint WaterHeaterHasMode = 1;
    private const uint WaterHeaterHasTargetTemperature = 2;
    private const uint WaterHeaterHasLegacyState = 4;
    private const uint WaterHeaterHasTargetTemperatureLow = 8;
    private const uint WaterHeaterHasTargetTemperatureHigh = 16;
    private const uint WaterHeaterHasOnState = 32;
    private const uint WaterHeaterHasAwayState = 64;
    private const uint WaterHeaterAwayStateFlag = 1;
    private const uint WaterHeaterOnStateFlag = 2;

    /// <summary>Applies a supported command to the supplied cloned state and returns the resulting state message.</summary>
    public static IESPHomeStateMessage? Apply(IESPHomeStateMessage currentState, IESPHomeCommandMessage command)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(command);

        return (currentState, command) switch
        {
            (CoverStateResponse state, CoverCommandRequest request) => ApplyCoverCommand(state, request),
            (FanStateResponse state, FanCommandRequest request) => ApplyFanCommand(state, request),
            (LightStateResponse state, LightCommandRequest request) => ApplyLightCommand(state, request),
            (SwitchStateResponse state, SwitchCommandRequest request) => ApplySwitchCommand(state, request),
            (ClimateStateResponse state, ClimateCommandRequest request) => ApplyClimateCommand(state, request),
            (WaterHeaterStateResponse state, WaterHeaterCommandRequest request) => ApplyWaterHeaterCommand(state, request),
            (NumberStateResponse state, NumberCommandRequest request) => ApplyNumberCommand(state, request),
            (SelectStateResponse state, SelectCommandRequest request) => ApplySelectCommand(state, request),
            (SirenStateResponse state, SirenCommandRequest request) => ApplySirenCommand(state, request),
            (LockStateResponse state, LockCommandRequest request) => ApplyLockCommand(state, request),
            (MediaPlayerStateResponse state, MediaPlayerCommandRequest request) => ApplyMediaPlayerCommand(state, request),
            (AlarmControlPanelStateResponse state, AlarmControlPanelCommandRequest request) => ApplyAlarmControlPanelCommand(state, request),
            (TextStateResponse state, TextCommandRequest request) => ApplyTextCommand(state, request),
            (DateStateResponse state, DateCommandRequest request) => ApplyDateCommand(state, request),
            (TimeStateResponse state, TimeCommandRequest request) => ApplyTimeCommand(state, request),
            (ValveStateResponse state, ValveCommandRequest request) => ApplyValveCommand(state, request),
            (DateTimeStateResponse state, DateTimeCommandRequest request) => ApplyDateTimeCommand(state, request),
            (UpdateStateResponse state, UpdateCommandRequest request) => ApplyUpdateCommand(state, request),
            _ => null
        };
    }

    private static CoverStateResponse ApplyCoverCommand(CoverStateResponse state, CoverCommandRequest request)
    {
        if (request.HasPosition) state.Position = request.Position;
        if (request.HasTilt) state.Tilt = request.Tilt;
        if (request.Stop || request.HasPosition || request.HasTilt) state.CurrentOperation = (CoverOperation)0;

        return state;
    }

    private static FanStateResponse ApplyFanCommand(FanStateResponse state, FanCommandRequest request)
    {
        if (request.HasState) state.State = request.State;
        if (request.HasOscillating) state.Oscillating = request.Oscillating;
        if (request.HasDirection) state.Direction = request.Direction;
        if (request.HasSpeedLevel) state.SpeedLevel = request.SpeedLevel;
        if (request.HasPresetMode) state.PresetMode = request.PresetMode;

        return state;
    }

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

    private static SwitchStateResponse ApplySwitchCommand(SwitchStateResponse state, SwitchCommandRequest request)
    {
        state.State = request.State;
        return state;
    }

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

    private static WaterHeaterStateResponse ApplyWaterHeaterCommand(WaterHeaterStateResponse state, WaterHeaterCommandRequest request)
    {
        ApplyWaterHeaterValues(state, request);
        ApplyWaterHeaterStateFlags(state, request);
        return state;
    }

    private static void ApplyWaterHeaterValues(WaterHeaterStateResponse state, WaterHeaterCommandRequest request)
    {
        if (HasFlag(request.HasFields, WaterHeaterHasMode))
        {
            state.Mode = request.Mode;
        }

        if (HasFlag(request.HasFields, WaterHeaterHasTargetTemperature))
        {
            state.TargetTemperature = request.TargetTemperature;
        }

        if (HasFlag(request.HasFields, WaterHeaterHasTargetTemperatureLow))
        {
            state.TargetTemperatureLow = request.TargetTemperatureLow;
        }

        if (HasFlag(request.HasFields, WaterHeaterHasTargetTemperatureHigh))
        {
            state.TargetTemperatureHigh = request.TargetTemperatureHigh;
        }

        if (HasFlag(request.HasFields, WaterHeaterHasLegacyState))
        {
            state.State = request.State;
        }
    }

    private static void ApplyWaterHeaterStateFlags(WaterHeaterStateResponse state, WaterHeaterCommandRequest request)
    {
        if (HasFlag(request.HasFields, WaterHeaterHasOnState))
        {
            state.State = CopyFlag(state.State, request.State, WaterHeaterOnStateFlag);
        }

        if (HasFlag(request.HasFields, WaterHeaterHasAwayState))
        {
            state.State = CopyFlag(state.State, request.State, WaterHeaterAwayStateFlag);
        }
    }

    private static bool HasFlag(uint value, uint flag) => (value & flag) != 0;

    private static uint CopyFlag(uint currentValue, uint sourceValue, uint flag) =>
        (sourceValue & flag) != 0 ? currentValue | flag : currentValue & ~flag;

    private static NumberStateResponse ApplyNumberCommand(NumberStateResponse state, NumberCommandRequest request)
    {
        state.State = request.State;
        state.MissingState = false;
        return state;
    }

    private static SelectStateResponse ApplySelectCommand(SelectStateResponse state, SelectCommandRequest request)
    {
        state.State = request.State;
        state.MissingState = false;
        return state;
    }

    private static SirenStateResponse ApplySirenCommand(SirenStateResponse state, SirenCommandRequest request)
    {
        if (request.HasState)
        {
            state.State = request.State;
        }

        return state;
    }

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

    private static MediaPlayerStateResponse ApplyMediaPlayerCommand(
        MediaPlayerStateResponse state,
        MediaPlayerCommandRequest request)
    {
        if (request.HasVolume)
        {
            state.Volume = Math.Clamp(request.Volume, 0f, 1f);
        }

        if (!request.HasCommand)
        {
            return state;
        }

        ApplyMediaPlayerAction(state, (int)request.Command);
        return state;
    }

    private static void ApplyMediaPlayerAction(MediaPlayerStateResponse state, int command)
    {
        switch (command)
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
    }

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

    private static TextStateResponse ApplyTextCommand(TextStateResponse state, TextCommandRequest request)
    {
        state.State = request.State;
        state.MissingState = false;
        return state;
    }

    private static DateStateResponse ApplyDateCommand(DateStateResponse state, DateCommandRequest request)
    {
        state.Year = request.Year;
        state.Month = request.Month;
        state.Day = request.Day;
        state.MissingState = false;
        return state;
    }

    private static TimeStateResponse ApplyTimeCommand(TimeStateResponse state, TimeCommandRequest request)
    {
        state.Hour = request.Hour;
        state.Minute = request.Minute;
        state.Second = request.Second;
        state.MissingState = false;
        return state;
    }

    private static ValveStateResponse ApplyValveCommand(ValveStateResponse state, ValveCommandRequest request)
    {
        if (request.HasPosition) state.Position = request.Position;
        if (request.Stop || request.HasPosition) state.CurrentOperation = (ValveOperation)0;

        return state;
    }

    private static DateTimeStateResponse ApplyDateTimeCommand(DateTimeStateResponse state, DateTimeCommandRequest request)
    {
        state.EpochSeconds = request.EpochSeconds;
        state.MissingState = false;
        return state;
    }

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
using Google.Protobuf.Collections;

namespace NetForESPHome.DomainModel.Native;
 
public sealed partial class ListEntitiesMediaPlayerResponse : IESPHomeEntityInfoMessage
{ 
    public MediaPlayerEntityFeature? _mediaPlayerCapabilities;
    public MediaPlayerEntityFeature SupportedFunctions => _mediaPlayerCapabilities ??= (MediaPlayerEntityFeature)this.FeatureFlags;
}

public enum AudioCodec
{
    Unknown,
    Pcm,
    Mp3,
    Flac,
    Opus
}

// ESPHome enum definition from media_player.h
[Flags]
public enum MediaPlayerEntityFeature : uint
{
    PAUSE = 1 << 0,
    SEEK = 1 << 1,
    VOLUME_SET = 1 << 2,
    VOLUME_MUTE = 1 << 3,
    PREVIOUS_TRACK = 1 << 4,
    NEXT_TRACK = 1 << 5,

    TURN_ON = 1 << 7,
    TURN_OFF = 1 << 8,
    PLAY_MEDIA = 1 << 9,
    VOLUME_STEP = 1 << 10,
    SELECT_SOURCE = 1 << 11,
    STOP = 1 << 12,
    CLEAR_PLAYLIST = 1 << 13,
    PLAY = 1 << 14,
    SHUFFLE_SET = 1 << 15,
    SELECT_SOUND_MODE = 1 << 16,
    BROWSE_MEDIA = 1 << 17,
    REPEAT_SET = 1 << 18,
    GROUPING = 1 << 19,
    MEDIA_ANNOUNCE = 1 << 20,
    MEDIA_ENQUEUE = 1 << 21,
    SEARCH_MEDIA = 1 << 22,
};


public sealed partial class ListEntitiesClimateResponse 
{ 
    private ClimateSupportedFeatures? _feature;
    private ClimateFanModeFlags? _fanModes;
    private ClimateSwingModeFlags? _swingModes;
    private ClimatePresetFlags? _climatePresets;
    private ClimateControlMode? _supportedModeFlags;
    public ClimateSupportedFeatures FeatureFlagsEnum => _feature ??= GetSupportedFeatures(FeatureFlags);
    public ClimateFanModeFlags FanModes => _fanModes ??= GetSupportedFanModes(SupportedFanModes);
    public ClimateSwingModeFlags SwingModes => _swingModes ??= GetSupportedSwingModes(SupportedSwingModes);
    public ClimatePresetFlags SupportedClimatePresets => _climatePresets ??= GetSupportedClimatePresets(SupportedPresets);
    public ClimateControlMode SupportedClimateControlModes => _supportedModeFlags ??= GetSupportedClimateModes(SupportedModes);

    private ClimateSupportedFeatures GetSupportedFeatures(uint featureFlags)
    {
        _feature = (ClimateSupportedFeatures)featureFlags;
        _feature |= (this.SupportsCurrentTemperature ? ClimateSupportedFeatures.CURRENT_TEMPERATURE : 0);
        _feature |= (this.SupportsTwoPointTargetTemperature ? ClimateSupportedFeatures.TWO_POINT_TARGET_TEMPERATURE : 0);
        _feature |= (this.SupportsCurrentHumidity ? ClimateSupportedFeatures.CURRENT_HUMIDITY : 0);
        _feature |= (this.SupportsTwoPointTargetTemperature ? ClimateSupportedFeatures.TARGET_HUMIDITY : 0);
        _feature |= (this.SupportsAction ? ClimateSupportedFeatures.ACTION : 0);

        return _feature.Value;
    }

    private static ClimateFanModeFlags GetSupportedFanModes(RepeatedField<ClimateFanMode> supportedFanModes)
    {
        ClimateFanModeFlags fanModes = 0;

        foreach (var supportedFanMode in supportedFanModes)
        {
            if (supportedFanMode == ClimateFanMode.ClimateFanOn) fanModes |= ClimateFanModeFlags.On;
            if (supportedFanMode == ClimateFanMode.ClimateFanOff) fanModes |= ClimateFanModeFlags.Off;
            if (supportedFanMode == ClimateFanMode.ClimateFanAuto) fanModes |= ClimateFanModeFlags.Auto;
            if (supportedFanMode == ClimateFanMode.ClimateFanLow) fanModes |= ClimateFanModeFlags.Low;
            if (supportedFanMode == ClimateFanMode.ClimateFanMedium) fanModes |= ClimateFanModeFlags.Medium;
            if (supportedFanMode == ClimateFanMode.ClimateFanHigh) fanModes |= ClimateFanModeFlags.High;
            if (supportedFanMode == ClimateFanMode.ClimateFanMiddle) fanModes |= ClimateFanModeFlags.Middle;
            if (supportedFanMode == ClimateFanMode.ClimateFanFocus) fanModes |= ClimateFanModeFlags.Focus;
            if (supportedFanMode == ClimateFanMode.ClimateFanDiffuse) fanModes |= ClimateFanModeFlags.Diffuse;
            if (supportedFanMode == ClimateFanMode.ClimateFanQuiet) fanModes |= ClimateFanModeFlags.Quiet;
        }

        return fanModes;
    }

    private static ClimateSwingModeFlags GetSupportedSwingModes(RepeatedField<ClimateSwingMode> supportedSwingModes)
    {
        ClimateSwingModeFlags swingModes = 0;

        foreach (var swingMode in supportedSwingModes)
        {
            if (swingMode == ClimateSwingMode.ClimateSwingOff) swingModes |= ClimateSwingModeFlags.Off;
            if (swingMode == ClimateSwingMode.ClimateSwingBoth) swingModes |= ClimateSwingModeFlags.Both;
            if (swingMode == ClimateSwingMode.ClimateSwingVertical) swingModes |= ClimateSwingModeFlags.Vertical;
            if (swingMode == ClimateSwingMode.ClimateSwingHorizontal) swingModes |= ClimateSwingModeFlags.Horizontal;
        }

        return swingModes;
    }

    private static ClimatePresetFlags GetSupportedClimatePresets(RepeatedField<ClimatePreset> supportedPresets)
    {
        ClimatePresetFlags presetModes = 0;

        foreach (var presetMode in supportedPresets)
        {
            if (presetMode == ClimatePreset.None) presetModes |= ClimatePresetFlags.None;
            if (presetMode == ClimatePreset.Home) presetModes |= ClimatePresetFlags.Home;
            if (presetMode == ClimatePreset.Away) presetModes |= ClimatePresetFlags.Away;
            if (presetMode == ClimatePreset.Boost) presetModes |= ClimatePresetFlags.Boost;
            if (presetMode == ClimatePreset.Comfort) presetModes |= ClimatePresetFlags.Comfort;
            if (presetMode == ClimatePreset.Eco) presetModes |= ClimatePresetFlags.Eco;
            if (presetMode == ClimatePreset.Sleep) presetModes |= ClimatePresetFlags.Sleep;
            if (presetMode == ClimatePreset.Activity) presetModes |= ClimatePresetFlags.Activity;
        }

        return presetModes;
    }

    private static ClimateControlMode GetSupportedClimateModes(RepeatedField<ClimateMode> deviceValues)
    {
        var supportedModes = ClimateControlMode.Unknown;

        foreach (var deviceValue in deviceValues)
        {
            var nidoValue = deviceValue switch
            {
                ClimateMode.Off => ClimateControlMode.Off,
                ClimateMode.HeatCool => ClimateControlMode.HeatCool,
                ClimateMode.Cool => ClimateControlMode.Cool,
                ClimateMode.Heat => ClimateControlMode.Heat,
                ClimateMode.FanOnly => ClimateControlMode.FanOnly,
                ClimateMode.Dry => ClimateControlMode.Dry,
                ClimateMode.Auto => ClimateControlMode.Auto,
                _ => ClimateControlMode.Unknown,
            };

            supportedModes |= nidoValue;
        }

        return supportedModes;
    }
}

// ESPHome enum
[Flags]
public enum ClimateSupportedFeatures : uint
{
    // Reporting current temperature is supported
    CURRENT_TEMPERATURE = 1 << 0,
    // Setting two target temperatures is supported (used in conjunction with CLIMATE_MODE_HEAT_COOL)
    TWO_POINT_TARGET_TEMPERATURE = 1 << 1,
    // Single-point mode is NOT supported (UI always displays two handles, setting 'target_temperature' is not supported)
    CLIMATE_REQUIRES_TWO_POINT_TARGET_TEMPERATURE = 1 << 2,
    // Reporting current humidity is supported
    CURRENT_HUMIDITY = 1 << 3,
    // Setting a target humidity is supported
    TARGET_HUMIDITY = 1 << 4,
    // Reporting current climate action is supported
    ACTION = 1 << 5,
};


public sealed partial class ListEntitiesFanResponse
{
    
}

public sealed partial class ListEntitiesWaterHeaterResponse : IESPHomeEntityInfoMessage
{ 
    private WaterHeaterModeFlags? _heaterModes; 
    public WaterHeaterModeFlags HeaterSupportedModes => _heaterModes ??= GetSupportedHeaterModes(SupportedModes);
    public WaterHeaterSupportedFeatures SupportedFeaturesFlags => (WaterHeaterSupportedFeatures)(SupportedFeatures);

    private static WaterHeaterModeFlags GetSupportedHeaterModes(RepeatedField<WaterHeaterMode> supportedModes)
    {
        WaterHeaterModeFlags heaterModesFlags = 0;

        foreach (var heaterMode in supportedModes)
        {
            if (heaterMode == WaterHeaterMode.Off) heaterModesFlags |= WaterHeaterModeFlags.Off;
            if (heaterMode == WaterHeaterMode.Eco) heaterModesFlags |= WaterHeaterModeFlags.Eco;
            if (heaterMode == WaterHeaterMode.Electric) heaterModesFlags |= WaterHeaterModeFlags.Electric;
            if (heaterMode == WaterHeaterMode.Performance) heaterModesFlags |= WaterHeaterModeFlags.Performance;
            if (heaterMode == WaterHeaterMode.HighDemand) heaterModesFlags |= WaterHeaterModeFlags.HighDemand;
            if (heaterMode == WaterHeaterMode.HeatPump) heaterModesFlags |= WaterHeaterModeFlags.HeatPump;
            if (heaterMode == WaterHeaterMode.Gas) heaterModesFlags |= WaterHeaterModeFlags.Gas;
        }

        return heaterModesFlags;
    }
}

// ESPHome Enum 
public enum WaterHeaterSupportedFeatures : uint
{
    CURRENT_TEMPERATURE = 1 << 0,
    TARGET_TEMPERATURE = 1 << 1,
    OPERATION_MODE = 1 << 2,
    AWAY_MODE = 1 << 3,
    ON_OFF = 1 << 4,
    TWO_POINT_TARGET_TEMPERATURE = 1 << 5,
};

public sealed partial class ListEntitiesAlarmControlPanelResponse : IESPHomeEntityInfoMessage
{ 
    public AlarmControlPanelFeature SupportedFeatureFlags => (AlarmControlPanelFeature)this.SupportedFeatures;
}
 
// ESPHome enum
[Flags]
public enum AlarmControlPanelFeature : byte
{
    // Matches Home Assistant values
    ARM_HOME = 1 << 0,
    ARM_AWAY = 1 << 1,
    ARM_NIGHT = 1 << 2,
    TRIGGER = 1 << 3,
    ARM_CUSTOM_BYPASS = 1 << 4,
    ARM_VACATION = 1 << 5,
};

 

public sealed partial class ListEntitiesLightResponse : IESPHomeEntityInfoMessage
{ 
    private ColorModeFlags? _supportedColorModes;
    public ColorModeFlags SupportedColorModeFlags => _supportedColorModes ??= GetSupportedColorModes(this.SupportedColorModes);

    public bool HasCapability(LightColorCapability capability) =>
       this.SupportedColorModes.Any(mode => ((int)mode & (int)capability) == (int)capability);

    /// <summary>Converts the ESPHome supported color-mode collection to an array so distinct overlapping ColorMode values are not collapsed into a flags mask.</summary>
    private static ColorModeFlags GetSupportedColorModes(RepeatedField<ColorMode> colorModes)
    {
        ColorModeFlags nidoValues = 0;

        foreach (var value in colorModes)
        {
            nidoValues |= value switch
            {
                ColorMode.Unknown => ColorModeFlags.Unknown,
                ColorMode.OnOff => ColorModeFlags.OnOff,
                ColorMode.LegacyBrightness => ColorModeFlags.LegacyBrightness,
                ColorMode.Brightness => ColorModeFlags.Brightness,
                ColorMode.White => ColorModeFlags.White,
                ColorMode.ColorTemperature => ColorModeFlags.ColorTemperature,
                ColorMode.ColdWarmWhite => ColorModeFlags.ColdWarmWhite,
                ColorMode.Rgb => ColorModeFlags.Rgb,
                ColorMode.RgbWhite => ColorModeFlags.RgbWhite,
                ColorMode.RgbColorTemperature => ColorModeFlags.RgbColorTemperature,
                ColorMode.RgbColdWarmWhite => ColorModeFlags.RgbColdWarmWhite,
                _ => 0
            };
        }

        return nidoValues;
    }
}
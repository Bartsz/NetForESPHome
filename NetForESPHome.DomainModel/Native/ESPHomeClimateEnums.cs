namespace NetForESPHome.DomainModel.Native;


///// <summary>Represents feature flags reported by an ESPHome climate entity.</summary>
//[Flags]
//public enum ClimateSupportedFeatures : byte
//{
//    None = 0,
//    CurrentTemperature = 1 << 0,
//    TwoPointTargetTemperature = 1 << 1,
//    RequiresTwoPointTargetTemperature = 1 << 2,
//    CurrentHumidity = 1 << 3,
//    TargetHumidity = 1 << 4,
//    Action = 1 << 5
//}

[Flags]
public enum ClimateFanModeFlags : ushort
{
    On = 1 << 0,
    Off = 1 << 1,
    Auto = 1 << 2,
    Low = 1 << 3,
    Medium = 1 << 4,
    High = 1 << 5,
    Middle = 1 << 6,
    Focus = 1 << 7,
    Diffuse = 1 << 8,
    Quiet = 1 << 9,
};


[Flags]
public enum ClimatePresetFlags : byte
{
    None = 1 << 0,
    Home = 1 << 1,
    Away = 1 << 2,
    Boost = 1 << 3,
    Comfort = 1 << 4,
    Eco = 1 << 5,
    Sleep = 1 << 6,
    Activity = 1 << 7,
}


[Flags]
public enum ClimateSwingModeFlags : byte
{
    Off = 1 << 0,
    Vertical = 1 << 1,
    Horizontal = 1 << 2,
    Both = Vertical | Horizontal,
}

public enum WaterHeaterModeFlags : byte
{
    Off = 1 << 0,
    Eco = 1 << 1,
    Electric = 1 << 2,
    Performance = 1 << 3,
    HighDemand = 1 << 4,
    HeatPump = 1 << 5,
    Gas = 1 << 6,
}



/// TO BE REMOVED ?


/// <summary>Represents the set of climate modes reported as supported by ESPHome.</summary>
[Flags]
public enum ClimateControlMode : byte
{
    Unknown = 0,
    Off = 1 << 0,
    Cool = 1 << 2,
    Heat = 1 << 3,
    FanOnly = 1 << 4,
    Dry = 1 << 5,
    Auto = 1 << 6,
    HeatCool = Heat | Cool,
}




[Flags]
public enum WaterHeaterStateFlag : byte
{
    Away = 1 << 0,
    On = 1 << 1,
};

 
[Flags]
public enum InfraredCapability : uint
{
    Transmitter = 1 << 0,
    Receiver = 1 << 1
}

[Flags]
public enum RadioFrequencyCapability : uint
{
    Transmitter = 1 << 0,
    Receiver = 1 << 1
}

public enum RadioFrequencyModulation : byte
{
    OnOffKeying = 0
}

// From ESPHome
public enum LightColorCapability : byte
{
    ON_OFF            = 1 << 0,  
    BRIGHTNESS        = 1 << 1,  
    WHITE             = 1 << 2,  
    COLOR_TEMPERATURE = 1 << 3,  
    COLD_WARM_WHITE   = 1 << 4,  
    RGB               = 1 << 5   
};

public enum ColorModeFlags : ushort
{
    Unknown = 0,
    OnOff = 1 << 0,
    LegacyBrightness = 1 << 1,
    Brightness = 1 << 2,
    White = 1 << 3,
    ColorTemperature = 1 << 4,
    ColdWarmWhite = 1 << 5,
    Rgb = 1 << 6,
    RgbWhite = 1 << 7,
    RgbColorTemperature = 1 << 8,
    RgbColdWarmWhite = 1 << 9,
}
using NetForESPHome.Client.Model;
using NetForESPHome.DomainModel.Native;

namespace NetForESPHome.Client;

public interface IEspHomeClient
{
    ApiVersion? ApiVersion { get; }
    int? LastDisconnectReason { get; }
    ConnectionState State { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    ValueTask DisposeAsync();
    ValueTask<EspHomeRpcResult<EspHomeEntityDiscovery>> GetEntitiesAndServicesAsync(CancellationToken cancellationToken = default);
    ValueTask<IESPHomeMessage> GetIncomingMessageAsync(CancellationToken ct = default); 
    ValueTask<IESPHomeStateMessage> GetIncomingStateChangeMessageAsync(CancellationToken ct = default);
    ValueTask<bool> RequestDeviceCapabilitiesRefresh(CancellationToken ct = default);
    ValueTask<bool> RequestDeviceInfoRefresh(CancellationToken ct = default); 
    ValueTask<bool> SendAlarmControlPanelCommand(uint key, uint deviceId, AlarmControlPanelStateCommand command, string? code = null, CancellationToken ct = default);
    ValueTask<bool> SendButtonCommand(uint key, uint deviceId, CancellationToken ct = default);
    ValueTask<bool> SendClimateCommand(uint key, uint deviceId, ClimateMode? mode = null, float? targetTemperature = null, float? targetTemperatureLow = null, float? targetTemperatureHigh = null, ClimateFanMode? fanMode = null, ClimateSwingMode? swingMode = null, string? customFanMode = null, ClimatePreset? preset = null, string? customPreset = null, float? targetHumidity = null, CancellationToken ct = default);
    ValueTask<bool> SendCoverCommand(uint key, uint deviceId, float? position = null, float? tilt = null, bool stop = false, CancellationToken ct = default);
    ValueTask<bool> SendDateCommand(uint key, uint deviceId, DateOnly date, CancellationToken ct = default);
    ValueTask<bool> SendDateTimeCommand(uint key, uint deviceId, DateTimeOffset dateTime, CancellationToken ct = default);
    ValueTask<bool> SendFanCommand(uint key, uint deviceId, bool? state = null, int? speedLevel = null, bool? oscillating = null, FanDirection? direction = null, string? presetMode = null, CancellationToken ct = default);
    ValueTask<bool> SendLightCommand(uint key, uint deviceId, bool? state = null, float? brightness = null, ColorMode? colorMode = null, (float Red, float Green, float Blue)? rgb = null, float? white = null, float? colorTemperatureMireds = null, float? coldWhite = null, float? warmWhite = null, TimeSpan? transition = null, TimeSpan? flash = null, string? effect = null, CancellationToken ct = default);
    ValueTask<EspHomeRpcResult<LightStateResponse>> SendLightCommandAndWaitForStateAsync(uint key, uint deviceId, TimeSpan responseTimeout, bool? state = null, float? brightness = null, ColorMode? colorMode = null, (float Red, float Green, float Blue)? rgb = null, float? white = null, float? colorTemperatureMireds = null, float? coldWhite = null, float? warmWhite = null, TimeSpan? transition = null, TimeSpan? flash = null, string? effect = null, CancellationToken ct = default);
    ValueTask<bool> SendLockCommand(uint key, uint deviceId, LockCommand command, CancellationToken ct = default);
    ValueTask<bool> SendMediaPlayerCommand(uint key, uint deviceId, MediaPlayerCommand? command = null, float? volume = null, string? mediaUrl = null, bool? announcement = null, CancellationToken ct = default);
    ValueTask<bool> SendNumberCommand(uint key, uint deviceId, float value, CancellationToken ct = default);
    ValueTask<bool> SendSelectTextCommand(uint key, uint deviceId, string option, CancellationToken ct = default);
    ValueTask<bool> SendSirenCommand(uint key, uint deviceId, bool? state = null, string? tone = null, float? volume = null, TimeSpan? duration = null, CancellationToken ct = default);
    ValueTask<bool> SendSwitchCommand(uint key, uint deviceId, bool state, CancellationToken ct = default);
    ValueTask<EspHomeRpcResult<SwitchStateResponse>> SendSwitchCommandAndWaitForStateAsync(uint key, uint deviceId, bool state, TimeSpan responseTimeout, CancellationToken ct = default);
    ValueTask<bool> SendTextCommand(uint key, uint deviceId, string value, CancellationToken ct = default);
    ValueTask<bool> SendTimeCommand(uint key, uint deviceId, TimeOnly time, CancellationToken ct = default);
    ValueTask<bool> SendUpdateCommand(uint key, uint deviceId, UpdateCommand command, CancellationToken ct = default);
    ValueTask<bool> SendValveCommand(uint key, uint deviceId, float? position = null, bool stop = false, CancellationToken ct = default);
    ValueTask<bool> SendWaterHeaterCommand(uint key, uint deviceId, WaterHeaterMode? mode = null, float? targetTemperature = null, float? targetTemperatureLow = null, float? targetTemperatureHigh = null, bool? away = null, bool? on = null, CancellationToken ct = default);
    ValueTask StartStatesSubscriptionAsync(CancellationToken ct = default);
 
}
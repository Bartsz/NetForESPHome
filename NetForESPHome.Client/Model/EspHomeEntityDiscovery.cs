using NetForESPHome.DomainModel.Native;

namespace NetForESPHome.Client.Model;

/// <summary>Contains the complete entity and user-service descriptions returned by one successful ESPHome discovery operation.</summary>
public sealed class EspHomeEntityDiscovery
{
    /// <summary>Gets all entity descriptions received before ESPHome completed discovery.</summary>
    public IESPHomeEntityInfoMessage[] Entities { get; }

    /// <summary>Gets all user-defined service descriptions received before ESPHome completed discovery.</summary>
    public ListEntitiesServicesResponse[] Services { get; }

    /// <summary>Creates one completed discovery snapshot from the messages returned by ESPHome.</summary>
    public EspHomeEntityDiscovery(IESPHomeEntityInfoMessage[] entities, ListEntitiesServicesResponse[] services)
    {
        Entities = entities;
        Services = services;
    }
}
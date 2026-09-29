using System.Runtime.InteropServices;

namespace NetForESPHome.Client.Model;

[StructLayout(LayoutKind.Auto)]
public record struct ApiVersion(uint Major, uint Minor) : IComparable<ApiVersion>
{
    public int CompareTo(ApiVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public static bool operator <(ApiVersion a, ApiVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ApiVersion a, ApiVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ApiVersion a, ApiVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ApiVersion a, ApiVersion b) => a.CompareTo(b) >= 0;
} 

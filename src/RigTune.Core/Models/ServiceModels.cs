using System.Text.Json.Serialization;

namespace RigTune.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceStartType
{
    Boot,
    System,
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
}

/// <summary>Live state of a Windows service as observed by <see cref="Platform.IServiceAccess"/>.</summary>
public sealed record ServiceState(string ServiceName, ServiceStartType StartType, bool IsRunning, bool IsPending = false);

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBridge.Coordinator;

internal static class CoordinatorSerialization
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}

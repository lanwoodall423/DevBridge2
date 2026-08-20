#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using DevBridge2;

namespace DevBridge.Coordinator;

/// <summary>
/// Compact, versioned result for one low-level game primitive. The route is
/// retained as evidence of the lease-checked live call; normalized fields are
/// provided for callers that should not need to understand the broad status
/// response.
/// </summary>
internal sealed class GamePrimitiveResponse
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.GamePrimitives;

    [JsonPropertyName("contract")]
    public string Contract { get; init; } = DevBridgeSchemaVersions.GamePrimitives;

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("toolName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("route")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRimBridgeRoute? Route { get; init; }

    [JsonPropertyName("completionRoute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRimBridgeRoute? CompletionRoute { get; init; }

    [JsonPropertyName("completionConfirmed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CompletionConfirmed { get; init; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GameCondition? Condition { get; init; }

    [JsonPropertyName("attempts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attempts { get; init; }

    [JsonPropertyName("elapsedMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ElapsedMs { get; init; }

    [JsonPropertyName("timeoutMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeoutMs { get; init; }

    [JsonPropertyName("cursorSequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CursorSequence { get; init; }

    [JsonPropertyName("checkpoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Checkpoint { get; init; }

    [JsonPropertyName("nextCheckpoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCheckpoint { get; init; }

    [JsonPropertyName("errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Errors { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("nextAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAction { get; init; }
}

internal sealed class GameCondition
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("equals")]
    public JsonElement Expected { get; init; }
}

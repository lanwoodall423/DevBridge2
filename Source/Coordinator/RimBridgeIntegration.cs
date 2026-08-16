using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DevBridge.Coordinator;

internal enum RimBridgeMode
{
    Off,
    Optional,
    Required
}

internal static class RimBridgeModes
{
    internal static RimBridgeMode Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return RimBridgeMode.Off;

        return value.Trim().ToLowerInvariant() switch
        {
            "off" => RimBridgeMode.Off,
            "optional" => RimBridgeMode.Optional,
            "required" => RimBridgeMode.Required,
            _ => throw new ProfileException("RIMBRIDGE_INVALID_MODE",
                "DEVBRIDGE_RIMBRIDGE_MODE must be off, optional, or required.")
        };
    }

    internal static string Text(RimBridgeMode mode) => mode switch
    {
        RimBridgeMode.Optional => "optional",
        RimBridgeMode.Required => "required",
        _ => "off"
    };
}

internal static class RimBridgeIntegrationConstants
{
    internal const string PackageId = "brrainz.rimbridgeserver";
    internal const string LoopbackHost = "127.0.0.1";
    internal const string EndpointNotFoundCode = "RIMBRIDGE_ENDPOINT_NOT_FOUND";
    internal const string StartupTimeoutCode = "RIMBRIDGE_STARTUP_TIMEOUT";
    internal const string StartupFailedCode = "RIMBRIDGE_STARTUP_FAILED";
    internal const string AuthFailedCode = "RIMBRIDGE_AUTH_FAILED";
    internal const string ProcessMismatchCode = "RIMBRIDGE_PROCESS_MISMATCH";
    internal const string CompanionToolName = "devbridge/get_generation_context";
    internal const string CompanionSchemaVersion = "devbridge-generation-context/v1";
    internal const string CompanionUnavailableCode = "RIMBRIDGE_COMPANION_UNAVAILABLE";
    internal const string CompanionContextInvalidCode = "RIMBRIDGE_COMPANION_CONTEXT_INVALID";
    internal const string CompanionIdentityMismatchCode = "RIMBRIDGE_COMPANION_IDENTITY_MISMATCH";
    internal const string CompanionAssemblyNotDiscoveredDiagnostic = "ASSEMBLY_NOT_DISCOVERED";
    internal const string CompanionWrongLocationDiagnostic = "WRONG_LOCATION";
    internal const string CompanionStaleBinaryDiagnostic = "STALE_BINARY";
    internal const string CompanionLoadFailedDiagnostic = "COMPANION_LOAD_FAILED";
    internal const string CompanionSdkCompatibilityDiagnostic = "SDK_COMPATIBILITY_FAILURE";
    internal const string CompanionToolRegistrationDiagnostic = "TOOL_REGISTRATION_FAILED";
    internal const string CompanionToolNotRegisteredDiagnostic = "TOOL_NOT_REGISTERED";
    internal const string CompanionToolCallFailedDiagnostic = "TOOL_CALL_FAILED";
    internal const string CompanionGenerationContextIncompleteDiagnostic = "GENERATION_CONTEXT_INCOMPLETE";
    internal const string CompanionIdentityMismatchDiagnostic = "IDENTITY_MISMATCH";
    internal const string CompanionAuthenticationDiagnostic = "AUTHENTICATION_FAILED";
    internal const string CompanionEndpointUnavailableDiagnostic = "ENDPOINT_UNAVAILABLE";
}

internal static class RimBridgeCompanionDiagnostics
{
    internal static string Code(RimBridgeIntegrationState bridge)
    {
        if (bridge == null)
            return null;
        if (!string.IsNullOrWhiteSpace(bridge.CompanionDiagnosticCode))
            return bridge.CompanionDiagnosticCode;
        if (string.Equals(bridge.CompanionErrorCode, RimBridgeIntegrationConstants.AuthFailedCode,
                StringComparison.Ordinal))
            return RimBridgeIntegrationConstants.CompanionAuthenticationDiagnostic;
        if (string.Equals(bridge.CompanionErrorCode, RimBridgeIntegrationConstants.CompanionIdentityMismatchCode,
                StringComparison.Ordinal))
            return RimBridgeIntegrationConstants.CompanionIdentityMismatchDiagnostic;
        if (string.Equals(bridge.CompanionErrorCode, RimBridgeIntegrationConstants.CompanionContextInvalidCode,
                StringComparison.Ordinal))
            return RimBridgeIntegrationConstants.CompanionGenerationContextIncompleteDiagnostic;
        if (string.Equals(bridge.CompanionErrorCode, RimBridgeIntegrationConstants.CompanionUnavailableCode,
                StringComparison.Ordinal))
        {
            if (Contains(bridge.CompanionError, "not registered"))
                return RimBridgeIntegrationConstants.CompanionToolNotRegisteredDiagnostic;
            if (Contains(bridge.CompanionError, "could not be called"))
                return RimBridgeIntegrationConstants.CompanionToolCallFailedDiagnostic;
            return RimBridgeIntegrationConstants.CompanionLoadFailedDiagnostic;
        }
        return null;
    }

    internal static string Reason(RimBridgeIntegrationState bridge)
    {
        if (bridge == null)
            return null;
        if (!string.IsNullOrWhiteSpace(bridge.CompanionDiagnosticReason))
            return bridge.CompanionDiagnosticReason;
        return Code(bridge) switch
        {
            RimBridgeIntegrationConstants.CompanionToolNotRegisteredDiagnostic =>
                "The companion tool is not registered.",
            RimBridgeIntegrationConstants.CompanionToolCallFailedDiagnostic =>
                "The companion tool call failed.",
            RimBridgeIntegrationConstants.CompanionAuthenticationDiagnostic =>
                "RimBridge authentication failed.",
            RimBridgeIntegrationConstants.CompanionIdentityMismatchDiagnostic =>
                "Generation context identity does not match coordinator state.",
            RimBridgeIntegrationConstants.CompanionGenerationContextIncompleteDiagnostic =>
                "Generation context is incomplete or unsupported.",
            RimBridgeIntegrationConstants.CompanionLoadFailedDiagnostic =>
                "Companion verification failed without a more specific host diagnostic.",
            _ => null
        };
    }

    private static bool Contains(string value, string fragment) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
}

internal sealed class RimBridgeIntegrationException : Exception
{
    internal RimBridgeIntegrationException(string code, string message) : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}

internal enum RimBridgeLifecycleState
{
    DISABLED,
    WAITING,
    NOT_INSTALLED,
    DISCOVERED,
    READY,
    FAILED,
    STALE
}

internal sealed class RimBridgeIntegrationState
{
    public string ConfiguredMode { get; set; } = "off";
    public RimBridgeLifecycleState LifecycleState { get; set; } = RimBridgeLifecycleState.DISABLED;
    public string PackageId { get; set; } = RimBridgeIntegrationConstants.PackageId;
    public string Version { get; set; }
    public string Host { get; set; }
    public int Port { get; set; }
    public bool TokenAvailable { get; set; }
    public string LaunchId { get; set; }
    public int Generation { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStartUtcTicks { get; set; }
    public DateTime? DiscoveryTimestampUtc { get; set; }
    public DateTime? LastVerificationTimestampUtc { get; set; }
    public DateTime? LogBoundaryTimestampUtc { get; set; }
    public long LogBoundaryPosition { get; set; }
    public bool LogExistedAtBoundary { get; set; }
    public long LogBoundaryCreationUtcTicks { get; set; }
    public string LogBoundaryPrefixHash { get; set; }
    public string ErrorCode { get; set; }
    public string Error { get; set; }
    public bool CompanionAvailable { get; set; }
    public bool CompanionVerified { get; set; }
    public string CompanionToolName { get; set; }
    public string CompanionLaunchId { get; set; }
    public int CompanionGeneration { get; set; }
    public int CompanionProcessId { get; set; }
    public DateTime? CompanionVerificationTimestampUtc { get; set; }
    public string CompanionErrorCode { get; set; }
    public string CompanionError { get; set; }
    public string CompanionDiagnosticCode { get; set; }
    public string CompanionDiagnosticReason { get; set; }

    internal RimBridgeIntegrationState Clone() => new()
    {
        ConfiguredMode = ConfiguredMode,
        LifecycleState = LifecycleState,
        PackageId = PackageId,
        Version = Version,
        Host = Host,
        Port = Port,
        TokenAvailable = TokenAvailable,
        LaunchId = LaunchId,
        Generation = Generation,
        ProcessId = ProcessId,
        ProcessStartUtcTicks = ProcessStartUtcTicks,
        DiscoveryTimestampUtc = DiscoveryTimestampUtc,
        LastVerificationTimestampUtc = LastVerificationTimestampUtc,
        LogBoundaryTimestampUtc = LogBoundaryTimestampUtc,
        LogBoundaryPosition = LogBoundaryPosition,
        LogExistedAtBoundary = LogExistedAtBoundary,
        LogBoundaryCreationUtcTicks = LogBoundaryCreationUtcTicks,
        LogBoundaryPrefixHash = LogBoundaryPrefixHash,
        ErrorCode = ErrorCode,
        Error = Error,
        CompanionAvailable = CompanionAvailable,
        CompanionVerified = CompanionVerified,
        CompanionToolName = CompanionToolName,
        CompanionLaunchId = CompanionLaunchId,
        CompanionGeneration = CompanionGeneration,
        CompanionProcessId = CompanionProcessId,
        CompanionVerificationTimestampUtc = CompanionVerificationTimestampUtc,
        CompanionErrorCode = CompanionErrorCode,
        CompanionError = CompanionError,
        CompanionDiagnosticCode = CompanionDiagnosticCode,
        CompanionDiagnosticReason = CompanionDiagnosticReason
    };

    internal static RimBridgeIntegrationState Disabled(RimBridgeMode mode) => new()
    {
        ConfiguredMode = RimBridgeModes.Text(mode),
        LifecycleState = mode == RimBridgeMode.Off
            ? RimBridgeLifecycleState.DISABLED
            : RimBridgeLifecycleState.WAITING
    };
}

internal sealed class RimBridgeProfileDecision
{
    internal RimBridgeMode Mode { get; init; }
    internal bool Included { get; init; }
    internal bool Installed { get; init; }
    internal string Version { get; init; }
    internal string ErrorCode { get; init; }
    internal string Error { get; init; }
}

internal static class RimBridgeProfilePolicy
{
    internal static RimBridgeProfileDecision Decide(RimBridgeMode mode,
        IReadOnlyDictionary<string, List<InstalledModMetadata>> installed)
    {
        if (mode == RimBridgeMode.Off)
            return new RimBridgeProfileDecision { Mode = mode };

        if (installed == null || !installed.TryGetValue(RimBridgeIntegrationConstants.PackageId,
                out List<InstalledModMetadata> candidates) || candidates.Count == 0)
        {
            throw new ProfileException("RIMBRIDGE_NOT_INSTALLED",
                "RimBridgeServer package " + RimBridgeIntegrationConstants.PackageId +
                " is required by the base profile but was not found in the configured mod roots.");
        }

        if (candidates.Count > 1)
        {
            string detail = string.Join("; ", candidates.Select(value => value.DirectoryPath)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            throw new ProfileException("RIMBRIDGE_AMBIGUOUS_PACKAGE",
                "RimBridgeServer package " + RimBridgeIntegrationConstants.PackageId +
                " is ambiguous; installed candidates are: " + detail + ".");
        }

        InstalledModMetadata metadata = candidates[0];
        if (!string.IsNullOrWhiteSpace(metadata.MetadataError))
        {
            throw new ProfileException("RIMBRIDGE_MALFORMED_METADATA",
                "RimBridgeServer metadata is malformed at " + metadata.DirectoryPath + ": " +
                metadata.MetadataError);
        }

        return new RimBridgeProfileDecision
        {
            Mode = mode,
            Included = true,
            Installed = true,
            Version = metadata.Version
        };
    }
}

internal sealed class RimBridgeLogBoundary
{
    internal string Path { get; init; }
    internal bool Available { get; init; }
    internal bool Existed { get; init; }
    internal long Length { get; init; }
    internal long CreationUtcTicks { get; init; }
    internal string PrefixHash { get; init; }
    internal DateTime CapturedUtc { get; init; }
    internal string Error { get; init; }
}

internal sealed class RimBridgeEndpoint
{
    public string Host { get; init; }
    public int Port { get; init; }
    public string Token { get; init; }
    public string LaunchId { get; init; }
    public int Generation { get; init; }
    public int ProcessId { get; init; }
    public long ProcessStartUtcTicks { get; init; }
    public DateTime DiscoveredUtc { get; init; }

    internal bool IsValid => IPAddress.TryParse(Host, out IPAddress address) &&
        IPAddress.IsLoopback(address) && Port is > 0 and <= 65535 &&
        !string.IsNullOrWhiteSpace(Token);
}

internal sealed class RimBridgeLogDiscoveryResult
{
    internal RimBridgeEndpoint Endpoint { get; init; }
    internal bool SawPort { get; init; }
    internal bool SawToken { get; init; }
    internal bool BoundaryInvalid { get; init; }
    internal bool StartupFailed { get; init; }
    internal string ErrorCode { get; init; }
    internal string Error { get; init; }
}

internal static class RimBridgeLogDiscovery
{
    private static readonly Regex PortLine = new(
        @"\[RimBridge\]\s+GABP server running standalone on port\s+(?<port>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TokenLine = new(
        @"\[RimBridge\]\s+Bridge token:\s*(?<token>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex FailureLine = new(
        @"\[RimBridge\].*(Failed to start server|STARTUP_INIT_FAILURE|startup failure)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RimWorldStartupLine = new(
        @"^\s*RimWorld\s+\d+\.\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static RimBridgeLogBoundary CaptureBoundary(string path, DateTime capturedUtc)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new RimBridgeLogBoundary
                {
                    Path = path,
                    Available = true,
                    Existed = false,
                    Length = 0,
                    CapturedUtc = capturedUtc
                };
            }

            FileInfo info = new(path);
            return new RimBridgeLogBoundary
            {
                Path = path,
                Available = true,
                Existed = true,
                Length = info.Length,
                CreationUtcTicks = info.CreationTimeUtc.Ticks,
                PrefixHash = ReadPrefixHash(path, info.Length),
                CapturedUtc = capturedUtc
            };
        }
        catch (Exception exception)
        {
            return new RimBridgeLogBoundary
            {
                Path = path,
                Available = false,
                CapturedUtc = capturedUtc,
                Error = "Player.log boundary could not be captured: " + exception.Message
            };
        }
    }

    internal static RimBridgeLogDiscoveryResult Discover(RimBridgeLogBoundary boundary,
        string launchId, int generation, int processId, long processStartUtcTicks, DateTime now)
    {
        if (boundary == null || !boundary.Available)
        {
            return new RimBridgeLogDiscoveryResult
            {
                ErrorCode = RimBridgeIntegrationConstants.EndpointNotFoundCode,
                Error = boundary?.Error ?? "Player.log boundary is unavailable."
            };
        }

        try
        {
            if (!File.Exists(boundary.Path))
                return new RimBridgeLogDiscoveryResult();

            FileInfo info = new(boundary.Path);
            bool scanFromBeginning = !boundary.Existed;
            if (boundary.Existed &&
                (info.Length < boundary.Length ||
                 (boundary.CreationUtcTicks > 0 && info.CreationTimeUtc.Ticks > 0 &&
                  info.CreationTimeUtc.Ticks != boundary.CreationUtcTicks) ||
                 string.IsNullOrWhiteSpace(boundary.PrefixHash) ||
                 !string.Equals(ReadPrefixHash(boundary.Path, boundary.Length),
                     boundary.PrefixHash, StringComparison.Ordinal)))
            {
                // RimWorld truncates Player.log during a normal launch. A genuinely
                // shortened log that contains the fresh process startup marker can be
                // safely rebased; arbitrary or larger replacements still destroy the
                // append-only attribution boundary and remain fail-closed.
                bool hasStartupMarker = HasRimWorldStartupMarker(boundary.Path);
                if (hasStartupMarker)
                    scanFromBeginning = true;
                else if (info.Length < boundary.Length)
                    // The file is in the normal pre-marker truncation window. Keep
                    // polling instead of converting a transient state into a terminal
                    // required-mode failure.
                    return new RimBridgeLogDiscoveryResult();
                else
                {
                    return new RimBridgeLogDiscoveryResult
                    {
                        BoundaryInvalid = true,
                        ErrorCode = RimBridgeIntegrationConstants.EndpointNotFoundCode,
                        Error = "Player.log was truncated or rotated after the launch boundary; stale bridge output was ignored."
                    };
                }
            }

            using FileStream stream = new(boundary.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!scanFromBeginning)
                stream.Seek(boundary.Length, SeekOrigin.Begin);
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            int? port = null;
            bool sawPort = false;
            bool sawToken = false;
            bool startupFailed = false;
            RimBridgeEndpoint lastEndpoint = null;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (FailureLine.IsMatch(line))
                    startupFailed = true;

                Match portMatch = PortLine.Match(line);
                if (portMatch.Success && int.TryParse(portMatch.Groups["port"].Value,
                        out int parsedPort) && parsedPort is > 0 and <= 65535)
                {
                    port = parsedPort;
                    sawPort = true;
                    sawToken = false;
                    continue;
                }

                Match tokenMatch = TokenLine.Match(line);
                if (tokenMatch.Success && port.HasValue)
                {
                    string token = tokenMatch.Groups["token"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        sawToken = true;
                        lastEndpoint = new RimBridgeEndpoint
                        {
                            Host = RimBridgeIntegrationConstants.LoopbackHost,
                            Port = port.Value,
                            Token = token,
                            LaunchId = launchId,
                            Generation = generation,
                            ProcessId = processId,
                            ProcessStartUtcTicks = processStartUtcTicks,
                            DiscoveredUtc = now
                        };
                    }
                }
            }

            return new RimBridgeLogDiscoveryResult
            {
                Endpoint = lastEndpoint,
                SawPort = sawPort,
                SawToken = sawToken,
                StartupFailed = startupFailed,
                ErrorCode = startupFailed
                    ? RimBridgeIntegrationConstants.StartupFailedCode
                    : sawPort && !sawToken ? RimBridgeIntegrationConstants.AuthFailedCode : null,
                Error = startupFailed
                    ? "RimBridgeServer reported a startup failure in the current Player.log segment."
                    : sawPort && !sawToken
                        ? "RimBridgeServer reported a port but no bridge token in the current Player.log segment."
                        : null
            };
        }
        catch (IOException)
        {
            return new RimBridgeLogDiscoveryResult
            {
                ErrorCode = RimBridgeIntegrationConstants.EndpointNotFoundCode,
                Error = "Player.log was not readable during bridge discovery."
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new RimBridgeLogDiscoveryResult
            {
                ErrorCode = RimBridgeIntegrationConstants.EndpointNotFoundCode,
                Error = "Player.log access was denied during bridge discovery."
            };
        }
    }

    private static string ReadPrefixHash(string path, long length)
    {
        long boundedLength = Math.Min(Math.Max(0, length), 64 * 1024);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[(int)boundedLength];
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer, read, buffer.Length - read);
            if (count <= 0)
                break;
            read += count;
        }

        return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
    }

    private static bool HasRimWorldStartupMarker(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (RimWorldStartupLine.IsMatch(line))
                return true;
        }

        return false;
    }
}

internal static class RimBridgeEndpointVerifier
{
    internal static bool CanConnect(RimBridgeEndpoint endpoint, TimeSpan timeout)
    {
        if (endpoint == null || !endpoint.IsValid)
            return false;

        try
        {
            using TcpClient client = new();
            Task connection = client.ConnectAsync(endpoint.Host, endpoint.Port);
            return connection.Wait(timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout) &&
                client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

internal enum RimBridgeCompanionVerificationStatus
{
    Unavailable,
    Match,
    Mismatch,
    Invalid
}

internal sealed class RimBridgeCompanionVerification
{
    internal RimBridgeCompanionVerificationStatus Status { get; init; }
    internal string Code { get; init; }
    internal string Error { get; init; }
    internal string DiagnosticCode { get; init; }
    internal string DiagnosticReason { get; init; }
    internal string LaunchId { get; init; }
    internal int Generation { get; init; }
    internal int ProcessId { get; init; }
}

internal static class RimBridgeCompanionClient
{
    private const string ProtocolVersion = "gabp/1";

    internal static RimBridgeCompanionVerification Verify(
        RimBridgeEndpoint endpoint,
        string expectedLaunchId,
        int expectedGeneration,
        int expectedProcessId,
        TimeSpan timeout)
    {
        if (endpoint == null || !endpoint.IsValid)
            return Unavailable(RimBridgeIntegrationConstants.EndpointNotFoundCode,
                "A verified RimBridge endpoint was not available for companion verification.",
                RimBridgeIntegrationConstants.CompanionEndpointUnavailableDiagnostic,
                "RimBridge endpoint unavailable.");

        try
        {
            using TcpClient client = new();
            TimeSpan boundedTimeout = timeout <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(1)
                : timeout;
            Task connect = client.ConnectAsync(endpoint.Host, endpoint.Port);
            if (!connect.Wait(boundedTimeout) || !client.Connected)
                return Unavailable(RimBridgeIntegrationConstants.EndpointNotFoundCode,
                    "The RimBridge endpoint did not accept a companion verification connection.",
                    RimBridgeIntegrationConstants.CompanionEndpointUnavailableDiagnostic,
                    "RimBridge endpoint unavailable.");

            client.ReceiveTimeout = Math.Max(1, (int)boundedTimeout.TotalMilliseconds);
            client.SendTimeout = Math.Max(1, (int)boundedTimeout.TotalMilliseconds);
            using NetworkStream stream = client.GetStream();
            DateTime deadline = DateTime.UtcNow + boundedTimeout;

            SendFrame(stream, new Dictionary<string, object>
            {
                ["v"] = ProtocolVersion,
                ["type"] = "request",
                ["id"] = "devbridge-hello-" + Guid.NewGuid().ToString("N"),
                ["method"] = "session/hello",
                ["params"] = new Dictionary<string, object>
                {
                    ["token"] = endpoint.Token,
                    ["bridgeVersion"] = ComponentVersions.BridgeToolsHandshakeVersion(),
                    ["platform"] = "RimWorld",
                    ["launchId"] = expectedLaunchId
                }
            });

            using JsonDocument hello = ReadFrame(stream, deadline);
            if (TryReadError(hello.RootElement, out int helloErrorCode))
            {
                if (helloErrorCode == -31000)
                    return Invalid(RimBridgeIntegrationConstants.AuthFailedCode,
                        "RimBridge rejected the bridge token during companion verification.",
                        RimBridgeIntegrationConstants.CompanionAuthenticationDiagnostic,
                        "RimBridge authentication failed.");
                return Unavailable(RimBridgeIntegrationConstants.CompanionUnavailableCode,
                    "RimBridge did not establish a companion verification session.",
                    RimBridgeIntegrationConstants.CompanionLoadFailedDiagnostic,
                    "Companion session/handshake failed; the host did not establish the companion session.");
            }

            SendFrame(stream, new Dictionary<string, object>
            {
                ["v"] = ProtocolVersion,
                ["type"] = "request",
                ["id"] = "devbridge-context-" + Guid.NewGuid().ToString("N"),
                ["method"] = "tools/call",
                ["params"] = new Dictionary<string, object>
                {
                    ["name"] = RimBridgeIntegrationConstants.CompanionToolName,
                    ["arguments"] = new Dictionary<string, object>()
                }
            });

            using JsonDocument response = ReadFrame(stream, deadline);
            if (TryReadError(response.RootElement, out int toolErrorCode))
            {
                if (toolErrorCode == -31000)
                    return Invalid(RimBridgeIntegrationConstants.AuthFailedCode,
                        "RimBridge rejected the bridge token for the generation-context tool.",
                        RimBridgeIntegrationConstants.CompanionAuthenticationDiagnostic,
                        "RimBridge authentication failed.");
                if (toolErrorCode == -31002 || toolErrorCode == -32601)
                    return Unavailable(RimBridgeIntegrationConstants.CompanionUnavailableCode,
                        "The optional DevBridge generation-context tool is not registered.",
                        RimBridgeIntegrationConstants.CompanionToolNotRegisteredDiagnostic,
                        "The companion loaded or was queried, but devbridge/get_generation_context is not registered.");
                return Unavailable(RimBridgeIntegrationConstants.CompanionUnavailableCode,
                    "The optional DevBridge generation-context tool could not be called.",
                    RimBridgeIntegrationConstants.CompanionToolCallFailedDiagnostic,
                    "The companion tool call failed; the host did not identify a more specific cause.");
            }

            JsonElement payload = response.RootElement.GetProperty("result");
            if (payload.ValueKind == JsonValueKind.Object &&
                TryGetProperty(payload, "structuredContent", out JsonElement structured) &&
                structured.ValueKind == JsonValueKind.Object)
                payload = structured;

            if (payload.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(payload, "success", out JsonElement success) ||
                success.ValueKind != JsonValueKind.True ||
                !TryGetProperty(payload, "available", out JsonElement available) ||
                available.ValueKind != JsonValueKind.True)
            {
                string code = ReadString(payload, "errorCode") ??
                    RimBridgeIntegrationConstants.CompanionContextInvalidCode;
                return Invalid(code, "The companion returned no complete DevBridge generation context.",
                    RimBridgeIntegrationConstants.CompanionGenerationContextIncompleteDiagnostic,
                    "Generation context is incomplete or unavailable.");
            }

            if (!string.Equals(ReadString(payload, "schemaVersion"),
                    RimBridgeIntegrationConstants.CompanionSchemaVersion,
                    StringComparison.Ordinal))
                return Invalid(RimBridgeIntegrationConstants.CompanionContextInvalidCode,
                    "The companion generation-context schema is not supported.",
                    RimBridgeIntegrationConstants.CompanionGenerationContextIncompleteDiagnostic,
                    "Generation context schema is unsupported or incomplete.");

            string launchId = ReadString(payload, "launchId");
            int? generation = ReadInt(payload, "generation");
            int? processId = ReadInt(payload, "processId");
            if (string.IsNullOrWhiteSpace(launchId) || !generation.HasValue || !processId.HasValue)
                return Invalid(RimBridgeIntegrationConstants.CompanionContextInvalidCode,
                    "The companion returned an incomplete DevBridge generation context.",
                    RimBridgeIntegrationConstants.CompanionGenerationContextIncompleteDiagnostic,
                    "Generation context is incomplete; launchId, generation, or processId is missing.");

            if (!string.Equals(launchId, expectedLaunchId, StringComparison.Ordinal) ||
                generation.Value != expectedGeneration || processId.Value != expectedProcessId)
            {
                return new RimBridgeCompanionVerification
                {
                    Status = RimBridgeCompanionVerificationStatus.Mismatch,
                    Code = RimBridgeIntegrationConstants.CompanionIdentityMismatchCode,
                    Error = "The RimBridge companion reported a different DevBridge launch, generation, or process identity.",
                    DiagnosticCode = RimBridgeIntegrationConstants.CompanionIdentityMismatchDiagnostic,
                    DiagnosticReason = "Generation context identity does not match coordinator state.",
                    LaunchId = launchId,
                    Generation = generation.Value,
                    ProcessId = processId.Value
                };
            }

            return new RimBridgeCompanionVerification
            {
                Status = RimBridgeCompanionVerificationStatus.Match,
                LaunchId = launchId,
                Generation = generation.Value,
                ProcessId = processId.Value
            };
        }
        catch (TimeoutException)
        {
            return Unavailable(RimBridgeIntegrationConstants.CompanionUnavailableCode,
                "RimBridge companion verification timed out.",
                RimBridgeIntegrationConstants.CompanionEndpointUnavailableDiagnostic,
                "Companion verification timed out.");
        }
        catch (SocketException)
        {
            return Unavailable(RimBridgeIntegrationConstants.CompanionUnavailableCode,
                "RimBridge companion verification could not connect.",
                RimBridgeIntegrationConstants.CompanionEndpointUnavailableDiagnostic,
                "Companion endpoint connection failed.");
        }
        catch (JsonException)
        {
            return Invalid(RimBridgeIntegrationConstants.CompanionContextInvalidCode,
                "RimBridge returned an invalid companion response.",
                RimBridgeIntegrationConstants.CompanionLoadFailedDiagnostic,
                "Companion response was malformed or incompatible.");
        }
        catch
        {
            return Unavailable(RimBridgeIntegrationConstants.CompanionUnavailableCode,
                "RimBridge companion verification was unavailable.",
                RimBridgeIntegrationConstants.CompanionLoadFailedDiagnostic,
                "Companion verification failed without a more specific host diagnostic.");
        }
    }

    private static RimBridgeCompanionVerification Unavailable(string code, string error,
        string diagnosticCode, string diagnosticReason) => new()
    {
        Status = RimBridgeCompanionVerificationStatus.Unavailable,
        Code = code,
        Error = error,
        DiagnosticCode = diagnosticCode,
        DiagnosticReason = diagnosticReason
    };

    private static RimBridgeCompanionVerification Invalid(string code, string error,
        string diagnosticCode = null, string diagnosticReason = null) => new()
    {
        Status = RimBridgeCompanionVerificationStatus.Invalid,
        Code = code,
        Error = error,
        DiagnosticCode = diagnosticCode,
        DiagnosticReason = diagnosticReason
    };

    private static void SendFrame(NetworkStream stream, object message)
    {
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Program.JsonOptions));
        byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length +
            "\r\nContent-Type: application/json\r\n\r\n");
        stream.Write(header, 0, header.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static JsonDocument ReadFrame(NetworkStream stream, DateTime deadline)
    {
        List<byte> header = new();
        while (true)
        {
            int value = ReadByte(stream, deadline);
            if (value < 0)
                throw new IOException("RimBridge closed the companion verification connection.");
            header.Add((byte)value);
            if (header.Count > 8192)
                throw new InvalidDataException("RimBridge response headers were too large.");
            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' &&
                header[^2] == '\r' && header[^1] == '\n')
                break;
        }

        string headerText = Encoding.ASCII.GetString(header.ToArray());
        int length = 0;
        foreach (string line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line.Substring("Content-Length:".Length).Trim(), out int parsed))
                length = parsed;
        }
        if (length <= 0 || length > 1024 * 1024)
            throw new InvalidDataException("RimBridge response did not contain a bounded content length.");

        byte[] body = new byte[length];
        int offset = 0;
        while (offset < body.Length)
        {
            EnsureBeforeDeadline(deadline);
            int read = stream.Read(body, offset, body.Length - offset);
            if (read <= 0)
                throw new IOException("RimBridge closed the companion response before it was complete.");
            offset += read;
        }
        return JsonDocument.Parse(body);
    }

    private static int ReadByte(NetworkStream stream, DateTime deadline)
    {
        EnsureBeforeDeadline(deadline);
        byte[] one = new byte[1];
        int read = stream.Read(one, 0, 1);
        return read == 0 ? -1 : one[0];
    }

    private static void EnsureBeforeDeadline(DateTime deadline)
    {
        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException("RimBridge companion verification exceeded its bounded timeout.");
    }

    private static bool TryReadError(JsonElement root, out int code)
    {
        code = 0;
        if (!TryGetProperty(root, "error", out JsonElement error) ||
            error.ValueKind != JsonValueKind.Object)
            return false;
        if (TryGetProperty(error, "code", out JsonElement value) && value.TryGetInt32(out int parsed))
            code = parsed;
        return true;
    }

    private static string ReadString(JsonElement value, string name) =>
        TryGetProperty(value, name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static int? ReadInt(JsonElement value, string name) =>
        TryGetProperty(value, name, out JsonElement property) && property.TryGetInt32(out int parsed)
            ? parsed : null;

    private static bool TryGetProperty(JsonElement value, string name, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty candidate in value.EnumerateObject())
            {
                if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }
}

internal static class RimBridgeEndpointStore
{
    private const string FileName = "rimbridge.endpoint.json";

    internal static string PathFor(string runtimeRoot) => Path.Combine(runtimeRoot, FileName);

    internal static void Save(string runtimeRoot, RimBridgeEndpoint endpoint)
    {
        if (endpoint == null || !endpoint.IsValid)
            throw new InvalidOperationException("Cannot persist an invalid RimBridge endpoint.");

        Directory.CreateDirectory(runtimeRoot);
        string path = PathFor(runtimeRoot);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(endpoint, Program.JsonOptions);
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    internal static RimBridgeEndpoint Load(string runtimeRoot)
    {
        try
        {
            string path = PathFor(runtimeRoot);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<RimBridgeEndpoint>(File.ReadAllText(path), Program.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    internal static void Delete(string runtimeRoot)
    {
        try { File.Delete(PathFor(runtimeRoot)); } catch { }
    }
}

internal sealed class JsonRimBridgeEndpoint
{
    [JsonPropertyName("host")]
    public string Host { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("token")]
    public string Token { get; init; }

    [JsonPropertyName("launchId")]
    public string LaunchId { get; init; }

    [JsonPropertyName("generation")]
    public int Generation { get; init; }

    [JsonPropertyName("rimworldPid")]
    public int ProcessId { get; init; }

    [JsonPropertyName("processStartIdentity")]
    public long ProcessStartUtcTicks { get; init; }

    [JsonPropertyName("discoveredUtc")]
    public DateTime DiscoveredUtc { get; init; }

    internal static JsonRimBridgeEndpoint From(RimBridgeEndpoint endpoint) => endpoint == null ? null : new()
    {
        Host = endpoint.Host,
        Port = endpoint.Port,
        Token = endpoint.Token,
        LaunchId = endpoint.LaunchId,
        Generation = endpoint.Generation,
        ProcessId = endpoint.ProcessId,
        ProcessStartUtcTicks = endpoint.ProcessStartUtcTicks,
        DiscoveredUtc = endpoint.DiscoveredUtc
    };
}

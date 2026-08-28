using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DevBridge2;

namespace DevBridge.Coordinator;

internal sealed partial class CoordinatorState
{
    internal JsonCommandResponse CreateJsonResponse(BridgeRequest request, int exitCode,
        IReadOnlyList<string> messages)
    {
        bool doctorCommand = string.Equals(request.Command, "doctor", StringComparison.OrdinalIgnoreCase);
        bool historyCommand = string.Equals(request.Command, "history", StringComparison.OrdinalIgnoreCase);
        bool coordinatorControl = string.Equals(request.Command, "coordinator", StringComparison.OrdinalIgnoreCase);
        bool projectResolveCommand = IsProjectResolveCommand(request);
        if (doctorCommand && request.DoctorAudit == null)
            request.DoctorAudit = RunDoctorAudit(request);
        if (historyCommand && request.HistoryResult == null)
        {
            lock (gate)
                request.HistoryResult = BuildGenerationHistoryViewLocked(state.Generation);
        }

        PersistedState snapshot;
        bool statusCommand = string.Equals(request.Command, "status", StringComparison.OrdinalIgnoreCase);
        lock (gate)
        {
            if (persistedStateLoadBlocked)
                snapshot = CloneStateLocked();
            else if (doctorCommand && request.DoctorAudit != null)
                snapshot = CloneStateLocked();
            else if (projectResolveCommand)
                snapshot = CloneStateLocked();
            else if (coordinatorControl)
                snapshot = CloneStateLocked();
            else if (!statusCommand)
            {
                SynchronizeLocked();
                RevalidateMaintenanceReadyLocked();
            }
            else
            {
                DetectExternalModsConfigMutationLocked();
                PruneProjectIntentsLocked();
                PruneStaleLeasesLocked();
            }
            if (!coordinatorControl && !persistedStateLoadBlocked && !(doctorCommand && request.DoctorAudit != null) &&
                !projectResolveCommand)
                RefreshRimBridgePolicyStateLocked();
            snapshot = CloneStateLocked();
            if (!coordinatorControl)
                snapshot.BaselineFingerprint = ReadBaselineFingerprintLocked() ?? snapshot.BaselineFingerprint;
            if (!coordinatorControl && !(doctorCommand && request.DoctorAudit != null) && !projectResolveCommand)
                snapshot.ModsConfigOwnership = CurrentModsConfigOwnershipLocked();
        }

        GenerationHistoryView operationalHistory = null;
        ConfigurationHealth nextGenerationConfig = null;
        if (doctorCommand && request.DoctorAudit != null)
        {
            operationalHistory = request.DoctorAudit.GenerationHistory;
            nextGenerationConfig = request.DoctorAudit.NextGenerationConfig;
        }
        else if (projectResolveCommand)
        {
            nextGenerationConfig = request.ProjectResolutionResult?.NextGenerationConfig;
        }
        else if (statusCommand && !persistedStateLoadBlocked)
        {
            lock (gate)
            {
                operationalHistory = BuildGenerationHistoryViewLocked(snapshot.Generation);
                nextGenerationConfig = EvaluateFutureConfigurationLocked(snapshot);
            }
        }

        string commandName = request.Command ?? string.Empty;
        string command = commandName;
        if (request.Arguments.Count > 0)
            command += " " + string.Join(" ", request.Arguments);

        bool maintenanceSafetyLost = exitCode == 0 && !snapshot.MaintenanceReady &&
            (string.Equals(commandName, "stop", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(commandName, "ensure-ready", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(commandName, "restart", StringComparison.OrdinalIgnoreCase)) &&
            (snapshot.ErrorCode == ProcessInspection.ErrorCode ||
             snapshot.ErrorCode == "MAINTENANCE_PROCESS_PRESENT");
        int effectiveExitCode = maintenanceSafetyLost ? 4 : exitCode;

        JsonCommandResponse response = new()
        {
            Success = effectiveExitCode == 0,
            Command = command,
            ExitCode = effectiveExitCode,
            State = snapshot.Phase.ToString(),
            CoordinatorRoot = snapshot.CoordinatorRoot,
            RimWorldRoot = rimWorldRoot,
            RimWorldExecutable = rimWorldExe,
            DevBridgeSourceRoot = runtimeIdentity?.DevBridgeSourceRoot,
            DevBridgeRuntimeRoot = runtimeIdentity?.DevBridgeRuntimeRoot ?? coordinatorRoot,
            DevBridgePinnedWorktreeRoot = runtimeIdentity?.DevBridgePinnedWorktreeRoot,
            RuntimeIdentity = runtimeIdentity,
            Identity = doctorCommand && request.DoctorAudit?.Identity != null
                ? request.DoctorAudit.Identity
                : BuildIdentityContract(snapshot, request.ProcessSnapshot),
            CoordinatorBuild = RunningBuildIdentity,
            PublishedCoordinatorBuild = PublishedCoordinatorBuildIdentity,
            CoordinatorBuildMatchesPublished = CoordinatorBuildMatchesPublished,
            RuntimeSlotId = snapshot.RuntimeSlotId,
            GoalId = request.GoalId,
            WakeId = request.WakeId,
            McpRequestId = request.McpRequestId,
            GameState = snapshot.Phase.ToString(),
            Generation = snapshot.Generation,
            RimWorldPid = snapshot.ProcessId,
            RimWorldProcessStartIdentity = snapshot.ProcessStartUtcTicks,
            LaunchGeneration = snapshot.LaunchGeneration,
            MaintenanceReady = snapshot.MaintenanceReady,
            LeaseState = snapshot.Leases.Any(value =>
                string.Equals(value.Agent, request.Agent, StringComparison.Ordinal)) ? "HELD" : "QUEUED",
            SessionDirty = snapshot.SessionDirty,
            ActiveTests = snapshot.Leases.Count,
            RestartPending = snapshot.RestartPending,
            RestartQueued = snapshot.RestartPending,
            TargetGeneration = snapshot.TargetGeneration,
            LaunchOwner = snapshot.LaunchOwner,
            LaunchAttemptCount = snapshot.LaunchAttemptCount,
            LaunchBudgetRemaining = snapshot.LaunchBudgetRemaining,
            WaitingForBridgeDeadlineUtc = snapshot.WaitingForBridgeDeadlineUtc,
            NextLeaseExpirationUtc = snapshot.Leases.Count == 0
                ? null
                : snapshot.Leases.Min(value => LeaseExpiresUtc(value)),
            RetryAfterSeconds = snapshot.Leases.Count == 0
                ? null
                : RetryAfterSeconds(snapshot.Leases.Min(value => LeaseExpiresUtc(value)), clock.UtcNow),
            RequiresNewProcess = snapshot.RequiresNewProcess,
            ProfileMode = snapshot.ProfileMode,
            ResolverProfileMode = snapshot.ProfileMode,
            LaunchProfileMode = snapshot.LaunchProfileMode,
            RequestedProjects = snapshot.RequestedProjects ?? new List<string>(),
            ResolvedProjectPackageIds = snapshot.ResolvedProjectPackageIds ?? new List<string>(),
            ResolvedMods = snapshot.ResolvedMods ?? new List<string>(),
            TestInputs = TestGenerationInputs.CloneValues(snapshot.TestInputs),
            ProfileFingerprint = snapshot.ProfileFingerprint,
            BaselineFingerprint = snapshot.BaselineFingerprint,
            RimBridge = RedactedRimBridge(snapshot.RimBridge),
            ModsConfigOwnership = snapshot.ModsConfigOwnership,
            ModsConfigMutationAuthority = snapshot.ModsConfigMutationAuthority,
            ExternalModsConfigMutation = snapshot.ExternalModsConfigMutation,
            RimBridgePolicy = snapshot.RimBridgePolicy?.Clone(),
            RimBridgeRoute = request.RimBridgeRouteResult?.ToJson(),
            ProfileConflict = snapshot.ProfileConflict,
            ProfileStrategy = "aggregate-first",
            AggregateAllowed = snapshot.Phase != BridgePhase.ISOLATING &&
                (snapshot.CrashIsolation == null || IsTerminalIsolationStatus(snapshot.CrashIsolation.Status)),
            TerminalFailurePhase = snapshot.CrashIsolation?.OriginalFailurePhase ?? snapshot.TerminalFailurePhase,
            TerminalFailureCode = snapshot.CrashIsolation?.OriginalFailureCode ?? snapshot.TerminalFailureCode,
            TerminalFailureDetail = snapshot.CrashIsolation?.OriginalFailureDetail ?? snapshot.TerminalFailureDetail,
            TerminalFailureExceptionType = snapshot.CrashIsolation?.OriginalFailureExceptionType ?? snapshot.TerminalFailureExceptionType,
            TerminalFailureExceptionMessage = snapshot.CrashIsolation?.OriginalFailureExceptionMessage ?? snapshot.TerminalFailureExceptionMessage,
            TerminalFailureDiagnosticDetail = snapshot.CrashIsolation?.OriginalFailureDiagnosticDetail ?? snapshot.TerminalFailureDiagnosticDetail,
            RuntimeProfileFingerprint = snapshot.RuntimeProfile?.ProfileFingerprint,
            CrashIsolation = snapshot.CrashIsolation,
            FrozenGeneration = snapshot.FrozenTargetGeneration,
            FrozenRequestedProjects = snapshot.FrozenRequestedProjects ?? new List<string>(),
            FrozenResolvedProjectPackageIds = snapshot.FrozenResolvedProjectPackageIds ?? new List<string>(),
            FrozenResolvedMods = snapshot.FrozenResolvedMods ?? new List<string>(),
            FrozenTestInputs = TestGenerationInputs.CloneValues(snapshot.FrozenTestInputs),
            FrozenProfileFingerprint = snapshot.FrozenProfileFingerprint,
            FrozenBaselineFingerprint = snapshot.FrozenBaselineFingerprint,
            AggregateFreezePending = snapshot.AggregateFreezePending,
            FrozenLaunchOwner = snapshot.FrozenLaunchOwner,
            FrozenLaunchRequestKey = snapshot.FrozenLaunchRequestKey,
            FrozenRegistrationIds = (snapshot.FrozenRegistrations ?? new List<ProjectIntentSnapshot>())
                .Select(value => value.Id).ToList(),
            FrozenRegistrations = (snapshot.FrozenRegistrations ?? new List<ProjectIntentSnapshot>()).ToList(),
            ActiveProjectIntents = (snapshot.ProjectIntents ?? new List<ProjectIntentRegistration>())
                .Where(value => value != null && string.Equals(value.Status, "ACTIVE", StringComparison.Ordinal))
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(ToJsonProjectIntent).ToList(),
            QueuedProjectIntents = QueuedProjectIntents(snapshot).Select(ToJsonProjectIntent).ToList(),
            AggregateGenerations = snapshot.AggregateGenerations ?? new List<AggregateGenerationEvidence>(),
            MissingProjects = MissingProjectsFor(snapshot, request),
            Agent = request.Agent,
            Leases = snapshot.Leases
                .OrderBy(value => value.StartedUtc)
                .Select(ToJsonLease)
                .ToList(),
            Checks = messages
                .Where(value => value.StartsWith("PASS ", StringComparison.Ordinal) ||
                                value.StartsWith("FAIL ", StringComparison.Ordinal) ||
                                value.StartsWith("WARN ", StringComparison.Ordinal))
                .Select(DiagnosticRedactor.Text)
                .ToList()
        };

        if (operationalHistory != null)
            response.CurrentGenerationTrust = CurrentGenerationTrust(operationalHistory, snapshot);
        response.NextGenerationConfig = nextGenerationConfig;

        if (string.Equals(request.Command, "restart", StringComparison.OrdinalIgnoreCase))
        {
            response.Accepted = effectiveExitCode == 0 && messages.Any(value =>
                value.StartsWith("Restart accepted", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("Restart already accepted", StringComparison.OrdinalIgnoreCase));
        }

        if ((string.Equals(request.Command, "stop", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(request.Command, "ensure-ready", StringComparison.OrdinalIgnoreCase)) && effectiveExitCode == 0)
            response.Accepted = true;

        string subcommand = request.Arguments.Count > 0 ? request.Arguments[0] : string.Empty;
        if (string.Equals(request.Command, "test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(subcommand, "begin", StringComparison.OrdinalIgnoreCase) && effectiveExitCode == 0)
        {
            TestLease lease = snapshot.Leases
                .Where(value => string.Equals(value.Agent, request.Agent, StringComparison.Ordinal) &&
                                value.ClientProcessId == request.ClientProcessId)
                .OrderByDescending(value => value.StartedUtc)
                .FirstOrDefault();
            response.LeaseId = lease?.Id;
        }
        else if (string.Equals(request.Command, "test", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(subcommand, "end", StringComparison.OrdinalIgnoreCase) &&
                 request.Arguments.Count > 1)
        {
            response.LeaseId = request.Arguments[1];
        }
        else if (string.Equals(request.Command, "test", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(subcommand, "renew", StringComparison.OrdinalIgnoreCase) &&
                 request.Arguments.Count > 1)
        {
            response.LeaseId = request.Arguments[1];
        }
        else if ((string.Equals(request.Command, "stop", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(request.Command, "ensure-ready", StringComparison.OrdinalIgnoreCase)) &&
                 request.Arguments.Count > 0)
        {
            response.LeaseId = request.Arguments[0];
        }

        bool explicitBridgeEndpoint = (string.Equals(request.Command, "bridge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Command, "rimbridge", StringComparison.OrdinalIgnoreCase)) &&
            request.Arguments.Count > 0 &&
            string.Equals(request.Arguments[0], "endpoint", StringComparison.OrdinalIgnoreCase);
        if (explicitBridgeEndpoint && effectiveExitCode == 0)
        {
            RimBridgeEndpoint endpoint = RimBridgeEndpointStore.Load(runtimeRoot);
            if (ValidateRimBridgeEndpointForResponse(snapshot, endpoint))
                response.RimBridgeEndpoint = JsonRimBridgeEndpoint.From(endpoint);
        }

        response.Error = request.RimBridgeRouteResult != null && !request.RimBridgeRouteResult.Success
            ? request.RimBridgeRouteResult.Error
            : !string.IsNullOrWhiteSpace(snapshot.Error)
            ? snapshot.Error
            : !string.IsNullOrWhiteSpace(snapshot.ProfileError)
                ? snapshot.ProfileError
            : !string.IsNullOrWhiteSpace(snapshot.RimBridge?.Error) &&
              snapshot.RimBridge.ConfiguredMode != RimBridgeModes.Text(RimBridgeMode.Off)
                ? snapshot.RimBridge.Error
            : effectiveExitCode == 0
                ? null
                : messages.LastOrDefault(value => !value.StartsWith("Next action:", StringComparison.Ordinal));
        response.ErrorCode = request.RimBridgeRouteResult != null && !request.RimBridgeRouteResult.Success
            ? request.RimBridgeRouteResult.ErrorCode
            : snapshot.ErrorCode ?? snapshot.ProfileErrorCode ?? snapshot.RimBridge?.ErrorCode;

        if (!string.IsNullOrWhiteSpace(request.TestInputErrorCode))
        {
            response.ErrorCode = request.TestInputErrorCode;
            response.Error = request.TestInputError;
        }

        response.Error = DiagnosticRedactor.Text(response.Error);
        response.ProfileConflict = DiagnosticRedactor.Text(response.ProfileConflict);
        response.TerminalFailureDetail = DiagnosticRedactor.Text(response.TerminalFailureDetail);
        response.TerminalFailureExceptionType = DiagnosticRedactor.Text(response.TerminalFailureExceptionType);
        response.TerminalFailureExceptionMessage = DiagnosticRedactor.Text(response.TerminalFailureExceptionMessage);
        response.TerminalFailureDiagnosticDetail = DiagnosticRedactor.Text(response.TerminalFailureDiagnosticDetail);
        response.NextAction = DiagnosticRedactor.Text(JsonNextAction(request, snapshot, effectiveExitCode, response.LeaseId));

        if (historyCommand)
        {
            response.GenerationHistory = request.HistoryResult;
            if (request.HistoryResult?.Corrupt == true)
            {
                response.Success = false;
                response.ExitCode = effectiveExitCode == 0 ? 4 : effectiveExitCode;
                response.ErrorCode = request.HistoryResult.ErrorCode;
                response.Error = request.HistoryResult.Error;
            }
        }

        if (projectResolveCommand && request.ProjectResolutionResult != null)
        {
            ProjectResolutionResult plan = request.ProjectResolutionResult;
            response.ProjectResolution = plan;
            response.CurrentGenerationTrust = plan.CurrentGenerationTrust;
            response.NextGenerationConfig = plan.NextGenerationConfig;
            response.Success = plan.Success && effectiveExitCode == 0;
            response.ExitCode = response.Success ? effectiveExitCode : 4;
            if (plan.Success)
            {
                response.ErrorCode = null;
                response.Error = null;
            }
            else
            {
                response.ErrorCode = plan.ErrorCode ?? plan.Errors.FirstOrDefault()?.Code;
                response.Error = plan.Errors.FirstOrDefault()?.Message ??
                    "Project resolution failed; no runtime state was changed.";
            }
        }

        int doctorFindingsTotalCount = 0;
        if (doctorCommand)
        {
            DoctorAuditReport audit = request.DoctorAudit ?? new DoctorAuditReport();
            response.SchemaVersion = audit.SchemaVersion;
            response.Healthy = audit.Healthy && effectiveExitCode == 0;
            response.Findings = audit.Findings;
            response.Components = audit.Components ?? ComponentVersions.Current;
            response.OperationalState = audit.OperationalState;
            response.GenerationHistory = audit.GenerationHistory;
            response.NextActions = audit.NextActions;
            response.CurrentGenerationTrust = audit.OperationalState?.CurrentGenerationTrust;
            response.NextGenerationConfig = audit.NextGenerationConfig ??
                audit.OperationalState?.NextGenerationConfig;
            doctorFindingsTotalCount = audit.FindingsTotalCount;
            if (audit.FirstError != null)
            {
                response.ErrorCode = audit.FirstError.Code;
                response.Error = audit.FirstError.Message;
            }
            else
            {
                response.ErrorCode = null;
                response.Error = null;
            }
            response.Success = response.Healthy == true;
            response.ExitCode = response.Success ? 0 : 1;
            response.NextAction = response.NextActions.Count == 0 ? response.NextAction :
                response.NextActions[0].DisplayCommand();
        }
        else
        {
            response.NextActions = RecoveryGuidance.For(response.ErrorCode, response.Error);
        }

        if (request.ViewportResponse != null)
        {
            response.Viewport = request.ViewportResponse;
            response.Success = request.ViewportResponse.Success && effectiveExitCode == 0;
            response.ExitCode = response.Success
                ? 0
                : effectiveExitCode == 0 ? 4 : effectiveExitCode;
            response.ErrorCode = request.ViewportResponse.ErrorCode;
            response.Error = DiagnosticRedactor.Text(request.ViewportResponse.Error);
            response.NextAction = DiagnosticRedactor.Text(request.ViewportResponse.NextAction);
            response.NextActions = response.Success || string.IsNullOrWhiteSpace(response.NextAction)
                ? new List<DoctorNextAction>()
                : RecoveryGuidance.For(response.ErrorCode, response.Error);
        }

        if (doctorCommand || statusCommand)
        {
            string unbounded = JsonSerializer.Serialize(response, CoordinatorSerialization.JsonOptions);
            if (Encoding.UTF8.GetByteCount(unbounded) >
                DevBridgeSchemaVersions.CoordinatorMaxOutputPayloadBytes)
            {
                BoundDiagnosticResponse(response, doctorCommand ? "doctor" : "status",
                    doctorFindingsTotalCount);
            }
        }

        if (doctorCommand || statusCommand || historyCommand || projectResolveCommand)
        {
            string serialized = JsonSerializer.Serialize(response, CoordinatorSerialization.JsonOptions);
            string redacted = DiagnosticRedactor.Json(serialized);
            response = JsonSerializer.Deserialize<JsonCommandResponse>(redacted, CoordinatorSerialization.JsonOptions) ?? response;
        }
        return response;
    }

    private static void BoundDiagnosticResponse(JsonCommandResponse response, string operation,
        int findingsTotalCount = 0)
    {
        DiagnosticPayloadMetadata metadata = new()
        {
            Operation = operation,
            ConfiguredLimitBytes = DevBridgeSchemaVersions.CoordinatorMaxOutputPayloadBytes,
            Summarized = true
        };
        response.PayloadMetadata = metadata;

        response.RequestedProjects = BoundDiagnosticObject(response.RequestedProjects, metadata, "requestedProjects");
        response.ResolvedProjectPackageIds = BoundDiagnosticObject(response.ResolvedProjectPackageIds, metadata,
            "resolvedProjectPackageIds");
        response.ResolvedMods = BoundDiagnosticObject(response.ResolvedMods, metadata, "resolvedMods");
        response.TestInputs = BoundDiagnosticObject(response.TestInputs, metadata, "testInputs");
        response.FrozenRequestedProjects = BoundDiagnosticObject(response.FrozenRequestedProjects, metadata,
            "frozenRequestedProjects");
        response.FrozenResolvedProjectPackageIds = BoundDiagnosticObject(
            response.FrozenResolvedProjectPackageIds, metadata, "frozenResolvedProjectPackageIds");
        response.FrozenResolvedMods = BoundDiagnosticObject(response.FrozenResolvedMods, metadata,
            "frozenResolvedMods");
        response.FrozenTestInputs = BoundDiagnosticObject(response.FrozenTestInputs, metadata, "frozenTestInputs");
        response.FrozenRegistrationIds = BoundDiagnosticObject(response.FrozenRegistrationIds, metadata,
            "frozenRegistrationIds");
        response.FrozenRegistrations = BoundDiagnosticObject(response.FrozenRegistrations, metadata,
            "frozenRegistrations");
        response.Findings = BoundDiagnosticObject(response.Findings, metadata, "findings",
            DiagnosticResponseLimits.MaxFindingCount);
        response.ActiveProjectIntents = BoundDiagnosticObject(response.ActiveProjectIntents, metadata,
            "activeProjectIntents");
        response.QueuedProjectIntents = BoundDiagnosticObject(response.QueuedProjectIntents, metadata,
            "queuedProjectIntents");
        response.AggregateGenerations = BoundDiagnosticObject(response.AggregateGenerations, metadata,
            "aggregateGenerations");
        response.MissingProjects = BoundDiagnosticObject(response.MissingProjects, metadata, "missingProjects");
        response.Leases = BoundDiagnosticObject(response.Leases, metadata, "leases");
        response.Checks = BoundDiagnosticObject(response.Checks, metadata, "checks");
        response.CrashIsolation = BoundDiagnosticObject(response.CrashIsolation, metadata, "crashIsolation");
        response.GenerationHistory = BoundDiagnosticObject(response.GenerationHistory, metadata,
            "generationHistory");
        response.NextActions = BoundDiagnosticObject(response.NextActions, metadata, "nextActions");
        response.Error = BoundDiagnosticText(response.Error);
        response.ProfileConflict = BoundDiagnosticText(response.ProfileConflict);
        response.TerminalFailureDetail = BoundDiagnosticText(response.TerminalFailureDetail);
        response.TerminalFailureExceptionMessage = BoundDiagnosticText(response.TerminalFailureExceptionMessage);
        response.TerminalFailureDiagnosticDetail = BoundDiagnosticText(response.TerminalFailureDiagnosticDetail);

        if (findingsTotalCount > 0)
        {
            metadata.Collections["findings"] = new DiagnosticCollectionSummary
            {
                TotalCount = findingsTotalCount,
                SampleCount = response.Findings?.Count ?? 0,
                Truncated = findingsTotalCount > (response.Findings?.Count ?? 0)
            };
        }
        metadata.Truncated = metadata.Collections.Values.Any(value => value.Truncated);
        metadata.EstimatedSerializedBytes = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(response, CoordinatorSerialization.JsonOptions));
    }

    private static T BoundDiagnosticObject<T>(T source, DiagnosticPayloadMetadata metadata, string path,
        int maxSampleCount = DiagnosticResponseLimits.MaxSampleCount)
    {
        if (source is null)
            return source;

        string json = JsonSerializer.Serialize(source, CoordinatorSerialization.JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteBoundedDiagnosticJson(document.RootElement, writer, metadata, path, maxSampleCount);
            writer.Flush();
        }

        return JsonSerializer.Deserialize<T>(stream.ToArray(), CoordinatorSerialization.JsonOptions);
    }

    private static void WriteBoundedDiagnosticJson(JsonElement element, Utf8JsonWriter writer,
        DiagnosticPayloadMetadata metadata, string path, int maxSampleCount = DiagnosticResponseLimits.MaxSampleCount)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                IEnumerable<JsonProperty> properties = element.EnumerateObject();
                if (path.EndsWith("projectRequesters", StringComparison.Ordinal) ||
                    path.EndsWith("originalDiagnosticMetadata", StringComparison.Ordinal))
                {
                    List<JsonProperty> selected = properties.OrderBy(value => value.Name, StringComparer.Ordinal)
                        .Take(DiagnosticResponseLimits.MaxSampleCount).ToList();
                    RecordDiagnosticCollection(metadata, path, element.EnumerateObject().Count(), selected.Count);
                    properties = selected;
                }
                foreach (JsonProperty property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    string childPath = path + "." + property.Name;
                    WriteBoundedDiagnosticJson(property.Value, writer, metadata, childPath);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                int total = element.GetArrayLength();
                int start = Math.Max(0, total - maxSampleCount);
                RecordDiagnosticCollection(metadata, path, total, total - start);
                writer.WriteStartArray();
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (index++ < start)
                        continue;
                    WriteBoundedDiagnosticJson(item, writer, metadata, path + "[]");
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(BoundDiagnosticText(element.GetString()));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void RecordDiagnosticCollection(DiagnosticPayloadMetadata metadata, string path,
        int total, int sample)
    {
        metadata.Collections[path] = new DiagnosticCollectionSummary
        {
            TotalCount = total,
            SampleCount = sample,
            Truncated = total > sample
        };
    }

    private static string BoundDiagnosticText(string value)
    {
        string redacted = DiagnosticRedactor.Text(value);
        if (string.IsNullOrEmpty(redacted) ||
            redacted.Length <= DiagnosticResponseLimits.MaxDiagnosticStringLength)
            return redacted;
        const string suffix = "...[diagnostic text truncated]";
        return redacted[..(DiagnosticResponseLimits.MaxDiagnosticStringLength - suffix.Length)] + suffix;
    }

    private static RimBridgeIntegrationState RedactedRimBridge(RimBridgeIntegrationState source)
    {
        if (source == null)
            return null;
        RimBridgeIntegrationState copy = source.Clone();
        copy.Error = DiagnosticRedactor.Text(copy.Error);
        copy.CompanionError = DiagnosticRedactor.Text(copy.CompanionError);
        copy.CompanionDiagnosticCode = RimBridgeCompanionDiagnostics.Code(copy);
        copy.CompanionDiagnosticReason = DiagnosticRedactor.Text(
            RimBridgeCompanionDiagnostics.Reason(copy));
        return copy;
    }

    private static bool ValidateRimBridgeEndpointForResponse(PersistedState snapshot,
        RimBridgeEndpoint endpoint)
    {
        return endpoint != null && endpoint.IsValid && snapshot?.RimBridge != null &&
            snapshot.RimBridge.TokenAvailable &&
            string.Equals(endpoint.LaunchId, snapshot.RimBridge.LaunchId, StringComparison.Ordinal) &&
            endpoint.Generation == snapshot.RimBridge.Generation &&
            endpoint.ProcessId == snapshot.ProcessId &&
            endpoint.ProcessStartUtcTicks == snapshot.ProcessStartUtcTicks &&
            string.Equals(endpoint.LaunchId, snapshot.LaunchId, StringComparison.Ordinal);
    }

    private static ProjectIntentRegistrationInfo ToJsonProjectIntent(ProjectIntentRegistration registration) => new()
    {
        Id = registration.Id,
        Owner = registration.Owner,
        SessionId = registration.SessionId,
        RequestedProjects = (registration.RequestedProjects ?? new List<string>()).ToList(),
        Status = registration.Status,
        CreatedUtc = registration.CreatedUtc,
        LastHeartbeatUtc = registration.LastHeartbeatUtc,
        ExpiresUtc = registration.ExpiresUtc,
        ReleasedUtc = registration.ReleasedUtc,
        ReleaseReason = registration.ReleaseReason,
        ClientProcessId = registration.ClientProcessId
    };

    private static List<ProjectIntentRegistration> QueuedProjectIntents(PersistedState snapshot)
    {
        HashSet<string> included = new((snapshot.FrozenRegistrations ?? new List<ProjectIntentSnapshot>())
            .Select(value => value.Id), StringComparer.Ordinal);
        return (snapshot.ProjectIntents ?? new List<ProjectIntentRegistration>())
            .Where(value => value != null && string.Equals(value.Status, "ACTIVE", StringComparison.Ordinal) &&
                            !included.Contains(value.Id))
            .OrderBy(value => value.Id, StringComparer.Ordinal).ToList();
    }

    private static List<string> MissingProjectsFor(PersistedState snapshot, BridgeRequest request)
    {
        string owner = StableProjectOwner(request);
        string session = StableProjectSession(request);
        List<string> requested = CanonicalProjectUnion((snapshot.ProjectIntents ?? new List<ProjectIntentRegistration>())
            .Where(value => value != null && string.Equals(value.Status, "ACTIVE", StringComparison.Ordinal) &&
                            string.Equals(value.Owner, owner, StringComparison.Ordinal) &&
                            string.Equals(value.SessionId, session, StringComparison.Ordinal))
            .SelectMany(value => value.RequestedProjects));
        List<string> included = CanonicalProjectUnion(snapshot.RequestedProjects ?? new List<string>());
        return requested.Where(value => !included.Contains(value, StringComparer.Ordinal)).ToList();
    }

    private string JsonNextAction(BridgeRequest request, PersistedState snapshot,
        int exitCode, string leaseId)
    {
        string command = request.Command ?? string.Empty;
        string subcommand = request.Arguments.Count > 0 ? request.Arguments[0] : string.Empty;

        if (request.RimBridgeRouteResult != null && !request.RimBridgeRouteResult.Success)
        {
            return request.RimBridgeRouteResult.ErrorCode == "RIMBRIDGE_OPERATION_BLOCKED_BY_DEVBRIDGE_POLICY"
                ? "The routed RimBridge call was not forwarded because DevBridge policy blocked " +
                  request.RimBridgeRouteResult.ToolName + " for generation " +
                  request.RimBridgeRouteResult.Generation + ". Run: DevBridge.cmd bridge policy"
                : "RimBridge route failed with " + request.RimBridgeRouteResult.ErrorCode +
                  ". Run: DevBridge.cmd bridge status; no automatic restart was requested.";
        }

        if (snapshot.ExternalModsConfigMutation != null ||
            snapshot.ErrorCode == "PROFILE_EXTERNAL_MUTATION")
            return "Run: DevBridge.cmd mods status, then perform explicit baseline/profile reconciliation before restarting. DevBridge will not absorb the changed ModsConfig.xml.";

        if (snapshot.ErrorCode == ProcessInspection.ErrorCode ||
            snapshot.ErrorCode == "MAINTENANCE_PROCESS_PRESENT")
            return "Run: DevBridge.cmd doctor";

        if (snapshot.Phase == BridgePhase.ISOLATING ||
            (snapshot.CrashIsolation != null &&
             !IsTerminalIsolationStatus(snapshot.CrashIsolation.Status)))
        {
            return "Crash isolation is running; Do not retry, restart, or kill RimWorld, and do not change ModsConfig.xml. Run: DevBridge.cmd status and keep waiting.";
        }

        List<string> missingProjects = MissingProjectsFor(snapshot, request);
        if (missingProjects.Count > 0 && !snapshot.RestartPending &&
            snapshot.Phase != BridgePhase.ERROR)
        {
            return "Aggregate-first: requested projects are not in the READY profile (missing: " +
                string.Join(", ", missingProjects) + "). Run: DevBridge.cmd restart to combine all active registrations, then verify status --json before testing. Do not wait for other registrations to clear or request an exclusive profile unless isolating a failure or honoring a known incompatibility.";
        }

        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(subcommand, "begin", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
            return "Test your mod; this lease expires two minutes after its last heartbeat. Renew before expiresUtc, or start long-running work with test session, then run: DevBridge.cmd test end " + leaseId;

        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(subcommand, "end", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
        {
            return snapshot.RestartPending
                ? WaitingNextAction(snapshot)
                : "Continue your workflow; run DevBridge.cmd restart only after a change requiring a fresh process.";
        }

        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(subcommand, "renew", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
            return "Continue testing; renew the lease before expiresUtc, or keep a connected test session.";

        if (string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
            return "Replace the assembly, verify its hash, then run: DevBridge.cmd ensure-ready " + leaseId;

        if (string.Equals(command, "ensure-ready", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
            return "Run: DevBridge.cmd test end " + leaseId;

        if (exitCode != 0)
        {
            if (string.Equals(command, "doctor", StringComparison.OrdinalIgnoreCase))
                return "Fix the failing doctor check, then run: DevBridge.cmd restart";
            return "Run: DevBridge.cmd doctor";
        }

        if (snapshot.Phase == BridgePhase.ERROR)
            return "Run: DevBridge.cmd doctor";
        if (snapshot.MaintenanceReady)
            return "Replace the assembly, verify its hash, then run: DevBridge.cmd ensure-ready " + leaseId;
        if (snapshot.Phase == BridgePhase.READY && !snapshot.RestartPending)
        {
            string owner = StableProjectOwner(request);
            string session = StableProjectSession(request);
            bool callerHasProjectIntent = (snapshot.ProjectIntents ?? new List<ProjectIntentRegistration>())
                .Any(value => value != null && string.Equals(value.Status, "ACTIVE", StringComparison.Ordinal) &&
                              string.Equals(value.Owner, owner, StringComparison.Ordinal) &&
                              string.Equals(value.SessionId, session, StringComparison.Ordinal));
            if (!callerHasProjectIntent)
            {
                string activeTestGuidance = snapshot.Leases.Count == 0 ? string.Empty :
                    " Active tests delay a replacement launch or your test start; they do not block registration.";
                return "Aggregate-first: if you are testing a managed project, register its intent now so DevBridge combines it with existing registrations." +
                    activeTestGuidance +
                    " Do not wait for an exclusive profile unless isolating a failure or honoring a known incompatibility.";
            }
            return "Run: DevBridge.cmd test begin";
        }
        if (snapshot.RestartPending || snapshot.Phase == BridgePhase.DRAINING ||
            snapshot.Phase == BridgePhase.RESTARTING || snapshot.Phase == BridgePhase.LOADING)
            return WaitingNextAction(snapshot);
        if (snapshot.Phase == BridgePhase.STOPPED && snapshot.Generation > 0)
            return "Run: DevBridge.cmd restart";
        return "Run: DevBridge.cmd wait-ready";
    }

    private string WaitingNextAction(PersistedState snapshot)
    {
        TestLease next = snapshot.Leases.OrderBy(value => LeaseExpiresUtc(value)).FirstOrDefault();
        if (next == null)
            return "Restart is queued and owned by DevBridge; reconnect with DevBridge.cmd wait-ready and keep waiting. Do not end the task.";

        DateTime expiresUtc = LeaseExpiresUtc(next);
        return "Restart is queued and owned by DevBridge; reconnect with DevBridge.cmd wait-ready and keep waiting. The next blocking lease can expire at " +
            FormatUtc(expiresUtc) + " (retryAfterSeconds=" + RetryAfterSeconds(expiresUtc, clock.UtcNow) + "). Do not end the task.";
    }

    private JsonLeaseInfo ToJsonLease(TestLease lease)
    {
        DateTime expiresUtc = LeaseExpiresUtc(lease);
        return new JsonLeaseInfo
        {
            Id = lease.Id,
            Agent = lease.Agent,
            Generation = lease.Generation,
            StartedUtc = lease.StartedUtc,
            LastHeartbeatUtc = LeaseActivityUtc(lease),
            ExpiresUtc = expiresUtc,
            RetryAfterSeconds = RetryAfterSeconds(expiresUtc, clock.UtcNow),
            Age = FormatAge(lease.StartedUtc)
        };
    }

    private DateTime LeaseExpiresUtc(TestLease lease)
    {
        return LeaseActivityUtc(lease).Add(options.LeaseDuration);
    }

    private static int RetryAfterSeconds(DateTime expiresUtc, DateTime nowUtc)
    {
        double seconds = (expiresUtc.ToUniversalTime() - nowUtc.ToUniversalTime()).TotalSeconds;
        if (seconds <= 0)
            return 0;
        return (int)Math.Min(int.MaxValue, Math.Ceiling(seconds));
    }

    private static DateTime LeaseActivityUtc(TestLease lease)
    {
        return (lease.LastHeartbeatUtc == default ? lease.StartedUtc : lease.LastHeartbeatUtc)
            .ToUniversalTime();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        if (duration.TotalHours >= 1)
            return ((int)duration.TotalHours).ToString("00") + ":" + duration.Minutes.ToString("00") +
                ":" + duration.Seconds.ToString("00");
        return duration.Minutes.ToString("00") + ":" + duration.Seconds.ToString("00");
    }

    private string FormatAge(DateTime startedUtc)
    {
        TimeSpan age = clock.UtcNow - startedUtc.ToUniversalTime();
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        return FormatDuration(age);
    }

    private static string FormatUtc(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}

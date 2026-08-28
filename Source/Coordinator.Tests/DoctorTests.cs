using System.Text;
using System.Text.Json;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestDoctorHealthyContract()
    {
        using Fixture fixture = new(new PersistedState { Phase = BridgePhase.STOPPED });
        JsonCommandResponse response = RunDoctor(fixture, out int exitCode, out _);
        string json = JsonSerializer.Serialize(response, Program.JsonOptions);

        Assert(exitCode == 0 && response.Success && response.Healthy == true,
            "a complete idle fixture should produce a healthy doctor result");
        Assert(response.SchemaVersion == DevBridgeSchemaVersions.Doctor && response.Findings != null &&
               response.Findings.Count > 0 && response.Components != null && response.OperationalState != null &&
               response.NextActions != null,
            "doctor JSON must expose its stable schema, findings, components, operational state, and nextActions");
        Assert(json.Contains("\"schemaVersion\"", StringComparison.Ordinal) &&
               json.Contains("\"healthy\"", StringComparison.Ordinal) &&
               json.Contains("\"findings\"", StringComparison.Ordinal) &&
               json.Contains("\"components\"", StringComparison.Ordinal) &&
               json.Contains("\"operationalState\"", StringComparison.Ordinal) &&
               json.Contains("\"nextActions\"", StringComparison.Ordinal),
            "doctor JSON must be machine-readable without parsing human prose");
        Assert(json == JsonSerializer.Serialize(response, Program.JsonOptions),
            "doctor JSON serialization must be deterministic");
    }

    private static void TestDoctorCollectsIndependentFindings()
    {
        using Fixture fixture = new(new PersistedState { Phase = BridgePhase.STOPPED });
        File.Delete(fixture.RimWorldPath);
        File.Delete(Path.Combine(fixture.Root, "ModsConfig.xml"));
        File.WriteAllText(Path.Combine(fixture.Root, "Runtime", "readiness.json"), "{ malformed", Encoding.UTF8);

        JsonCommandResponse response = RunDoctor(fixture, out int exitCode, out _);
        HashSet<string> codes = response.Findings.Select(value => value.Code).ToHashSet(StringComparer.Ordinal);
        Assert(exitCode == 1 && !response.Healthy == true,
            "independent diagnostic failures must make doctor unhealthy");
        Assert(codes.Contains("RIMWORLD_EXECUTABLE_MISSING") &&
               codes.Contains("MODSCONFIG_MISSING") && codes.Contains("READINESS_MALFORMED"),
            "one failed check must not suppress independent coordinator, ModsConfig, or readiness findings");
    }

    private static void TestDoctorAuditsPermissions()
    {
        using Fixture fixture = new(new PersistedState { Phase = BridgePhase.STOPPED });
        JsonCommandResponse response = RunDoctor(fixture, out int exitCode, out _);
        DoctorFinding finding = response.Findings.FirstOrDefault(value =>
            value.Code == "FILE_READ_ACCESS_CHECKED");
        Assert(exitCode == 0 && finding != null && finding.Details.TryGetValue("writeProbe", out string probe) &&
               probe == "not-performed",
            "doctor must audit present artifact read access without creating or modifying probe files");
    }

    private static void TestDoctorDetectsStaleReadiness()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        string readinessPath = Path.Combine(fixture.Root, "Runtime", "readiness.json");
        File.WriteAllText(readinessPath, JsonSerializer.Serialize(new ReadinessRecord
        {
            LaunchId = "launch-ready",
            Generation = 1,
            ProcessId = 101,
            TimestampUtc = ClockStart.AddHours(-1)
        }, Program.JsonOptions));

        JsonCommandResponse response = RunDoctor(fixture, out int exitCode, out _);
        Assert(exitCode == 1 && response.Findings.Any(value => value.Code == "READINESS_STALE"),
            "doctor must report readiness outside the authoritative launch window");
    }

    private static void TestDoctorFindingsAreDeterministic()
    {
        using Fixture fixture = new(new PersistedState { Phase = BridgePhase.STOPPED });
        JsonCommandResponse first = RunDoctor(fixture, out _, out _);
        JsonCommandResponse second = RunDoctor(fixture, out _, out _);
        string firstJson = JsonSerializer.Serialize(first, Program.JsonOptions);
        string secondJson = JsonSerializer.Serialize(second, Program.JsonOptions);
        Assert(firstJson == secondJson && first.Findings.Select(value => value.StableKey())
                   .SequenceEqual(second.Findings.Select(value => value.StableKey())),
            "repeated doctor audits must produce deterministic findings and JSON");
    }

    private static void TestDoctorDetectsProcessIdentityAmbiguity()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        fixture.Adapter.Add(new FakeProcess(102, 1002, fixture.RimWorldPath));
        JsonCommandResponse response = RunDoctor(fixture, out int exitCode, out _);
        HashSet<string> codes = response.Findings.Select(value => value.Code).ToHashSet(StringComparer.Ordinal);
        Assert(exitCode == 1 && codes.Contains("PROCESS_MULTIPLE_MATCHING") &&
               codes.Contains("PROCESS_OWNERSHIP_AMBIGUOUS"),
            "doctor must report both multiple matching and unmanaged process ownership ambiguity");
    }

    private static void TestDoctorDetectsExternalModsConfigMutation()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "doctor mutation test requires a baseline");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(Request("restart", "agent", 1, "--projects", "horticulture"),
            _ => { }, () => true) == 0, "doctor mutation test requires an accepted generation");
        File.AppendAllText(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"), "\n<!-- drift -->\n",
            new UTF8Encoding(false));

        JsonCommandResponse response = RunDoctor(setup.Fixture, out int exitCode, out _);
        DoctorFinding finding = response.Findings.FirstOrDefault(value =>
            value.Code == "PROFILE_EXTERNAL_MUTATION");
        Assert(exitCode == 1 && finding != null && response.OperationalState.ModsConfigSafeToWrite == false,
            "doctor must diagnose external ModsConfig mutation and block writes");
        Assert(finding.NextActions.All(action => !action.Arguments.Contains("restart", StringComparer.OrdinalIgnoreCase)),
            "external mutation guidance must not suggest a blind restart");
    }

    private static void TestDoctorRejectsUnsupportedStateSchema()
    {
        using Fixture fixture = new(new PersistedState { Phase = BridgePhase.STOPPED });
        string statePath = Path.Combine(fixture.Root, "Runtime", "state.json");
        const string unsupported = "{\"SchemaVersion\":99,\"Phase\":\"READY\",\"Generation\":5}";
        File.WriteAllText(statePath, unsupported, Encoding.UTF8);
        fixture.State = fixture.Reload();

        JsonCommandResponse response = RunDoctor(fixture, out int exitCode, out _);
        Assert(exitCode == 1 && response.Findings.Any(value =>
                   value.Code == "PERSISTED_STATE_SCHEMA_UNSUPPORTED") &&
               File.ReadAllText(statePath) == unsupported,
            "doctor must diagnose newer state schemas without overwriting the source artifact");
    }

    private static void TestDoctorRejectsUnsupportedGeneratedConfigSchema()
    {
        using Fixture fixture = new(new PersistedState { Phase = BridgePhase.STOPPED });
        string statePath = Path.Combine(fixture.Root, "Runtime", "state.json");
        string stateBefore = File.ReadAllText(statePath);
        string manifestPath = Path.Combine(fixture.Root, "Runtime", "ModsConfig.generated.json");
        File.WriteAllText(manifestPath,
            "{\"SchemaVersion\":99,\"Hash\":\"" + new string('A', 64) + "\",\"Generation\":4}",
            Encoding.UTF8);

        JsonCommandResponse response = RunDoctor(fixture, out int exitCode, out _);
        Assert(exitCode == 1 && response.Findings.Any(value =>
                   value.Code == "GENERATED_MODS_CONFIG_SCHEMA_UNSUPPORTED") &&
               File.ReadAllText(statePath) == stateBefore,
            "doctor must diagnose newer generated-config schemas without persisting a derived error");
    }

    private static void TestDoctorReportsLeaseAndMaintenanceConflicts()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.READY,
            MaintenanceReady = true,
            Leases = new List<TestLease>
            {
                new() { Id = "duplicate", Agent = "a", StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart },
                new() { Id = "duplicate", Agent = "b", StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart }
            }
        });
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        JsonCommandResponse response = RunDoctor(fixture, out int exitCode, out _);
        HashSet<string> codes = response.Findings.Select(value => value.Code).ToHashSet(StringComparer.Ordinal);
        Assert(exitCode == 1 && codes.Contains("LEASE_EXPIRED") &&
               codes.Contains("LEASE_OWNERSHIP_CONFLICT") && codes.Contains("MAINTENANCE_STATE_INVALID"),
            "doctor must report expired/conflicting lease metadata and invalid maintenance state");
    }

    private static void TestDoctorReportsCrashIsolationAndSafeActions()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.ISOLATING,
            CrashIsolation = new CrashIsolationIncident
            {
                Status = "ACTIVE",
                Stage = "candidate",
                Diagnosis = "token=do-not-return"
            }
        });
        JsonCommandResponse response = RunDoctor(fixture, out int exitCode, out _);
        Assert(exitCode == 1 && response.Findings.Any(value => value.Code == "CRASH_ISOLATION_ACTIVE") &&
               response.OperationalState.MutationCommandsAllowed == false,
            "doctor must report active crash isolation and block mutation commands");
        Assert(response.NextActions.All(IsSafeDiagnosticAction),
            "doctor nextActions must contain only safe diagnostic/recovery commands");
    }

    private static void TestDoctorRedactsSecrets()
    {
        using Fixture fixture = new(new PersistedState
        {
            Phase = BridgePhase.ERROR,
            ErrorCode = "RIMBRIDGE_AUTH_FAILED",
            Error = "token=super-secret password:another-secret",
            RimBridge = new RimBridgeIntegrationState
            {
                ErrorCode = "RIMBRIDGE_AUTH_FAILED",
                Error = "Bearer bridge-secret"
            },
            CrashIsolation = new CrashIsolationIncident
            {
                Status = "ACTIVE",
                Diagnosis = "token=nested-secret"
            }
        });
        JsonCommandResponse response = RunDoctor(fixture, out _, out _);
        string json = JsonSerializer.Serialize(response, Program.JsonOptions);
        JsonCommandResponse decoded = JsonSerializer.Deserialize<JsonCommandResponse>(json, Program.JsonOptions)
            ?? throw new InvalidOperationException("doctor response did not deserialize");
        string decodedDiagnostics = string.Join(" ", new[]
        {
            decoded.Error,
            decoded.RimBridge?.Error,
            decoded.RimBridge?.CompanionError,
            decoded.CrashIsolation?.Diagnosis
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        Assert(!json.Contains("super-secret", StringComparison.Ordinal) &&
               !json.Contains("another-secret", StringComparison.Ordinal) &&
               !json.Contains("bridge-secret", StringComparison.Ordinal) &&
               !json.Contains("nested-secret", StringComparison.Ordinal) &&
               decodedDiagnostics.Contains("<redacted>", StringComparison.Ordinal),
            "doctor/status diagnostics must redact secret-shaped values");
    }

    private static void TestStructuredRecoveryGuidance()
    {
        List<DoctorNextAction> actions = RecoveryGuidance.For("READINESS_TIMEOUT", "timeout");
        Assert(actions.Any(value => value.Arguments.SequenceEqual(new[] { "status", "--json" })) &&
               actions.Any(value => value.Arguments.Contains("<lease-id>") && value.RequiresLeaseId),
            "readiness timeout guidance must include diagnostics and a parameterized holder action");
        Assert(actions.All(IsSafeDiagnosticAction), "central recovery guidance must exclude unsafe commands");
    }

    private static void TestDoctorBoundsAccumulatedDiagnosticState()
    {
        using Fixture fixture = new(new PersistedState
        {
            Phase = BridgePhase.STOPPED,
            AggregateGenerations = Enumerable.Range(1, 300).Select(generation => new AggregateGenerationEvidence
            {
                Generation = generation,
                RequestedProjects = new List<string> { "project-" + generation },
                ResolvedMods = Enumerable.Range(1, 20).Select(value => "mod-" + value).ToList()
            }).ToList()
        });
        DoctorAuditReport audit = new()
        {
            GenerationHistory = new GenerationHistoryView
            {
                Records = Enumerable.Range(1, 300).Select(generation => new GenerationHistoryRecord
                {
                    Generation = generation,
                    Status = "FAILED",
                    TerminalFailureCode = "ACCUMULATED_DIAGNOSTIC_FAILURE",
                    TerminalFailureDetail = new string('x', 1000)
                }).ToList()
            }
        };
        audit.AddFinding(DoctorSeverities.Error, "ACCUMULATED_DIAGNOSTIC_FAILURE",
            "The accumulated diagnostic state is unhealthy.", "Generation history");
        audit.Complete();

        BridgeRequest request = Request("doctor");
        request.Json = true;
        request.DoctorAudit = audit;
        JsonCommandResponse response = fixture.State.CreateJsonResponse(request, 1, new List<string>());
        string json = JsonSerializer.Serialize(response, Program.JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        DiagnosticPayloadMetadata metadata = response.PayloadMetadata;

        Assert(Encoding.UTF8.GetByteCount(json) <= CoordinatorIpcProtocol.MaxOutputPayloadLength &&
               document.RootElement.ValueKind == JsonValueKind.Object &&
               response.Healthy.HasValue && response.Healthy.Value == false &&
               response.ErrorCode == "ACCUMULATED_DIAGNOSTIC_FAILURE",
            "large doctor state must remain valid, bounded, and unhealthy with its real error code");
        Assert(metadata.Collections["generationHistory.records"].TotalCount == 300 &&
               metadata.Collections["generationHistory.records"].SampleCount <= DiagnosticResponseLimits.MaxSampleCount &&
               metadata.Collections["generationHistory.records"].Truncated &&
               metadata.Collections["aggregateGenerations"].TotalCount == 300 &&
               response.GenerationHistory.Records.Count <= DiagnosticResponseLimits.MaxSampleCount &&
               response.AggregateGenerations.Count <= DiagnosticResponseLimits.MaxSampleCount,
            "large diagnostic collections must expose bounded recent samples and truncation metadata");
    }

    private static void TestOversizedDiagnosticFallbackIsBounded()
    {
        CoordinatorBuildIdentity identity = CoordinatorBuildIdentity.FromInformationalVersion(
            "1.2.4+same-revision", "Release");
        CoordinatorIpcFrame frame = CoordinatorIpcProtocol.Result("request", 0,
            new JsonCommandResponse
            {
                Command = "doctor",
                Checks = new List<string> { new string('p', CoordinatorIpcProtocol.MaxOutputPayloadLength + 1000) }
            }, identity, identity, true);
        string payload = frame.Payload?.GetRawText();
        JsonCommandResponse fallback = JsonSerializer.Deserialize<JsonCommandResponse>(payload, Program.JsonOptions);

        Assert(frame.ExitCode == 2 && Encoding.UTF8.GetByteCount(payload) < CoordinatorIpcProtocol.MaxOutputPayloadLength &&
               fallback != null && fallback.Healthy == false &&
               fallback.ErrorCode == "OUTPUT_TOO_LARGE" &&
               fallback.PayloadMetadata?.Fallback == true &&
               fallback.PayloadMetadata.Operation == "doctor" &&
               !fallback.NextAction.Contains("Update the client", StringComparison.Ordinal),
            "an oversized same-version doctor result must become a small truthful fallback envelope");
    }

    private static JsonCommandResponse RunDoctor(Fixture fixture, out int exitCode,
        out List<string> messages)
    {
        BridgeRequest request = Request("doctor");
        request.Json = true;
        messages = new List<string>();
        exitCode = fixture.State.Execute(request, messages.Add, () => true);
        return fixture.State.CreateJsonResponse(request, exitCode, messages);
    }

    private static bool IsSafeDiagnosticAction(DoctorNextAction action)
    {
        string command = string.Join(" ", new[] { action.Command }
            .Concat(action.Arguments ?? new List<string>()));
        return !command.Contains("kill", StringComparison.OrdinalIgnoreCase) &&
               !command.Contains("restart", StringComparison.OrdinalIgnoreCase) &&
               !command.Contains("edit", StringComparison.OrdinalIgnoreCase) &&
               !command.Contains("rm ", StringComparison.OrdinalIgnoreCase);
    }
}

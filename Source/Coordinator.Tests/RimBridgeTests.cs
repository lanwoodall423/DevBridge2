using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DevBridge2;
using DevBridge2.BridgeTools;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestRimBridgeProfileModes()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        string baseline = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"))));

        string rimBridgeAbout = Path.Combine(setup.MetadataRoot,
            RimBridgeIntegrationConstants.PackageId, "About", "About.xml");
        File.WriteAllText(rimBridgeAbout, "<ModMetaData><packageId>" +
            RimBridgeIntegrationConstants.PackageId + "</packageId><modVersion>9.8.7</modVersion></ModMetaData>");

        ModProfile off = ModProfileResolver.Resolve(setup.Fixture.Root, baseline, Array.Empty<string>(),
            setup.Fixture.InstalledModsRoots, RimBridgeMode.Off);
        ModProfile optional = ModProfileResolver.Resolve(setup.Fixture.Root, baseline, Array.Empty<string>(),
            setup.Fixture.InstalledModsRoots, RimBridgeMode.Optional);
        ModProfile required = ModProfileResolver.Resolve(setup.Fixture.Root, baseline, Array.Empty<string>(),
            setup.Fixture.InstalledModsRoots, RimBridgeMode.Required);
        Assert(off.ResolvedMods.Contains(RimBridgeIntegrationConstants.PackageId,
                   StringComparer.OrdinalIgnoreCase) &&
               off.RimBridgeVersion == null,
            "off mode must keep RimBridgeServer in the base profile without resolving an endpoint version");
        Assert(optional.ResolvedMods.Contains(RimBridgeIntegrationConstants.PackageId,
                   StringComparer.OrdinalIgnoreCase) && optional.RimBridgeVersion == "9.8.7",
            "optional mode must include and resolve the base RimBridgeServer package");
        Assert(required.ResolvedMods.Contains(RimBridgeIntegrationConstants.PackageId,
                   StringComparer.OrdinalIgnoreCase) && required.RimBridgeVersion == "9.8.7",
            "required mode must include the installed RimBridge package and version");

        Directory.Delete(Path.Combine(setup.MetadataRoot, RimBridgeIntegrationConstants.PackageId), true);
        try
        {
            ModProfileResolver.Resolve(setup.Fixture.Root, baseline, Array.Empty<string>(),
                setup.Fixture.InstalledModsRoots, RimBridgeMode.Optional);
            throw new InvalidOperationException("optional mode unexpectedly resolved a missing base package");
        }
        catch (ProfileException exception)
        {
            Assert(exception.Code == "RIMBRIDGE_NOT_INSTALLED",
                "missing base RimBridgeServer must use the stable missing-package error code");
        }

        Assert(off.ProfileFingerprint != required.ProfileFingerprint &&
               optional.ProfileFingerprint != required.ProfileFingerprint,
            "non-off mode fingerprints must distinguish their configured participation policy");
    }

    private static void TestRimBridgeLogDiscovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "DevBridge2-rimbridge-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "Player.log");
        try
        {
            File.WriteAllText(path, new string('x', 512) + "\n" +
                "[RimBridge] GABP server running standalone on port 5174\n" +
                "[RimBridge] Bridge token: stale-token\n");
            RimBridgeLogBoundary boundary = RimBridgeLogDiscovery.CaptureBoundary(path, ClockStart);
            File.AppendAllText(path, "[RimBridge] GABP server running standalone on port 59123\n" +
                "[RimBridge] Bridge token: current-token\n");
            RimBridgeLogDiscoveryResult current = RimBridgeLogDiscovery.Discover(boundary,
                "launch-current", 4, 123, 456, ClockStart.AddSeconds(1));
            Assert(current.Endpoint != null && current.Endpoint.Port == 59123 &&
                   current.Endpoint.Token == "current-token" && current.Endpoint.Host == "127.0.0.1" &&
                   current.Endpoint.Generation == 4 && current.Endpoint.ProcessId == 123,
                "discovery must parse only the append-only current segment and bind its identity");
            Assert(current.Endpoint.Port != 5174, "discovery must not use the historical default port");

            File.WriteAllText(path, "x");
            RimBridgeLogDiscoveryResult rotated = RimBridgeLogDiscovery.Discover(boundary,
                "launch-current", 4, 123, 456, ClockStart.AddSeconds(2));
            Assert(rotated.Endpoint == null && !rotated.BoundaryInvalid &&
                   rotated.ErrorCode == null,
                "a shortened log before the fresh startup marker must remain pending");

            File.WriteAllText(path, "RimWorld 1.6.4871 rev591\n" +
                "[RimBridge] GABP server running standalone on port 59125\n" +
                "[RimBridge] Bridge token: rebased-token\n");
            RimBridgeLogDiscoveryResult rebased = RimBridgeLogDiscovery.Discover(boundary,
                "launch-rebased", 4, 123, 456, ClockStart.AddSeconds(2));
            Assert(rebased.Endpoint != null && rebased.Endpoint.Port == 59125 &&
                   rebased.Endpoint.Token == "rebased-token" && !rebased.BoundaryInvalid,
                "a shortened RimWorld log with a fresh startup marker must be rebased");

            File.WriteAllText(path, "old-file-prefix\n[RimBridge] Bridge token: old-token\n");
            RimBridgeLogBoundary largerRotationBoundary = RimBridgeLogDiscovery.CaptureBoundary(
                path, ClockStart.AddSeconds(2));
            File.WriteAllText(path, "replacement-file-prefix\n[RimBridge] GABP server running standalone on port 59124\n" +
                "[RimBridge] Bridge token: replacement-token\n");
            RimBridgeLogDiscoveryResult largerRotation = RimBridgeLogDiscovery.Discover(largerRotationBoundary,
                "launch-current", 4, 123, 456, ClockStart.AddSeconds(2));
            Assert(largerRotation.Endpoint == null && largerRotation.BoundaryInvalid,
                "a larger replacement log must also reject the old append boundary");

            File.WriteAllText(path, "[RimBridge] GABP server running standalone on port nope\n" +
                "[RimBridge] Bridge token: malformed\n");
            RimBridgeLogBoundary malformedBoundary = RimBridgeLogDiscovery.CaptureBoundary(path, ClockStart);
            RimBridgeLogDiscoveryResult malformed = RimBridgeLogDiscovery.Discover(malformedBoundary,
                "launch-malformed", 5, 124, 457, ClockStart.AddSeconds(3));
            Assert(malformed.Endpoint == null && !malformed.BoundaryInvalid,
                "malformed port/token lines must not create an endpoint");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestRimBridgeRequiredReadinessTimeout()
    {
        using Fixture fixture = Fixture.LoadingWithLease();
        fixture.RimBridgeMode = RimBridgeMode.Required;
        fixture.WriteState(new PersistedState
        {
            Generation = 0,
            Phase = BridgePhase.LOADING,
            LaunchId = "launch-1",
            LaunchGeneration = 1,
            TargetGeneration = 1,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            LaunchStartedUtc = ClockStart,
            RimBridge = new RimBridgeIntegrationState
            {
                ConfiguredMode = "required",
                LifecycleState = RimBridgeLifecycleState.WAITING,
                LaunchId = "launch-1",
                Generation = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LogBoundaryTimestampUtc = ClockStart,
                LogBoundaryPosition = 0,
                LogExistedAtBoundary = false
            },
            Leases = new List<TestLease>
            {
                new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 0,
                    StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart }
            }
        });
        fixture.State = fixture.Reload();
        fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
        int exitCode = fixture.State.Execute(Request("wait-ready", "holder", 77), _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status"), exitCode,
            Array.Empty<string>());
        Assert(exitCode != 0 && response.ErrorCode == RimBridgeIntegrationConstants.StartupTimeoutCode,
            "required bridge readiness must fail with a bounded startup timeout");
        Assert(fixture.Adapter.LaunchCalls == 0,
            "required bridge readiness timeout must not trigger a replacement launch");
    }

    private static void TestRimBridgeOptionalFailureNonblocking()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.RimBridgeMode = RimBridgeMode.Optional;
        fixture.WriteState(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.READY,
            LaunchId = "launch-ready",
            LaunchGeneration = 1,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            LaunchStartedUtc = ClockStart,
            RimBridge = new RimBridgeIntegrationState
            {
                ConfiguredMode = "optional",
                LifecycleState = RimBridgeLifecycleState.NOT_INSTALLED,
                PackageId = RimBridgeIntegrationConstants.PackageId,
                ErrorCode = "RIMBRIDGE_NOT_INSTALLED",
                Error = "optional package is unavailable",
                LaunchId = "launch-ready",
                Generation = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001
            }
        });
        fixture.State = fixture.Reload();
        List<string> output = new();
        int exitCode = fixture.State.Execute(Request("status"), output.Add, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status"), exitCode,
            Array.Empty<string>());
        Assert(exitCode == 0 && response.State == "READY" &&
               response.RimBridge?.ErrorCode == "RIMBRIDGE_NOT_INSTALLED" &&
               response.RimBridge.LifecycleState == RimBridgeLifecycleState.NOT_INSTALLED,
            "optional bridge profile failures must remain visible without blocking base readiness");
    }

    private static void TestRimBridgeTokenIsNotInStatus()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.RimBridgeMode = RimBridgeMode.Optional;
        fixture.WriteState(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.READY,
            LaunchId = "launch-ready",
            LaunchGeneration = 1,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            LaunchStartedUtc = ClockStart,
            RimBridge = new RimBridgeIntegrationState
            {
                ConfiguredMode = "optional",
                LifecycleState = RimBridgeLifecycleState.READY,
                PackageId = RimBridgeIntegrationConstants.PackageId,
                TokenAvailable = true,
                Host = "127.0.0.1",
                Port = 59001,
                LaunchId = "launch-ready",
                Generation = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                CompanionAvailable = true,
                CompanionVerified = true,
                CompanionToolName = RimBridgeIntegrationConstants.CompanionToolName,
                CompanionLaunchId = "launch-ready",
                CompanionGeneration = 1,
                CompanionProcessId = 101
            }
        });
        fixture.State = fixture.Reload();
        RimBridgeEndpointStore.Save(Path.Combine(fixture.Root, "Runtime"), new RimBridgeEndpoint
        {
            Host = "127.0.0.1", Port = 59001, Token = "secret-status-token", LaunchId = "launch-ready",
            Generation = 1, ProcessId = 101, ProcessStartUtcTicks = 1001, DiscoveredUtc = ClockStart
        });
        BridgeRequest status = Request("status");
        JsonCommandResponse response = fixture.State.CreateJsonResponse(status, 0, Array.Empty<string>());
        string json = JsonSerializer.Serialize(response, Program.JsonOptions);
        List<string> output = new();
        fixture.State.Execute(status, output.Add, () => true);
        Assert(!json.Contains("secret-status-token", StringComparison.Ordinal) &&
               !string.Join("\n", output).Contains("secret-status-token", StringComparison.Ordinal),
            "ordinary human and JSON status must never reveal the bridge token");
        Assert(response.RimBridgeEndpoint == null,
            "ordinary status JSON must not populate the explicit endpoint field");
    }

    private static void TestRimBridgeEndpointIdentityInvalidation()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.RimBridgeMode = RimBridgeMode.Optional;
        fixture.WriteState(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.READY,
            LaunchId = "launch-ready",
            LaunchGeneration = 1,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            LaunchStartedUtc = ClockStart,
            RimBridge = new RimBridgeIntegrationState
            {
                ConfiguredMode = "optional", LifecycleState = RimBridgeLifecycleState.READY,
                TokenAvailable = true, Host = "127.0.0.1", Port = 59002,
                LaunchId = "launch-ready", Generation = 1, ProcessId = 101, ProcessStartUtcTicks = 1001
            }
        });
        fixture.State = fixture.Reload();
        string runtime = Path.Combine(fixture.Root, "Runtime");
        RimBridgeEndpointStore.Save(runtime, new RimBridgeEndpoint
        {
            Host = "127.0.0.1", Port = 59002, Token = "stale-identity-token", LaunchId = "launch-ready",
            Generation = 1, ProcessId = 101, ProcessStartUtcTicks = 1001, DiscoveredUtc = ClockStart
        });
        fixture.Adapter.Replace(101, 1002);
        List<string> output = new();
        int exitCode = fixture.State.Execute(Request("bridge", "agent", 1, "endpoint"), output.Add, () => true);
        Assert(exitCode != 0 && !File.Exists(RimBridgeEndpointStore.PathFor(runtime)) &&
               !string.Join("\n", output).Contains("stale-identity-token", StringComparison.Ordinal),
            "identity changes must invalidate the persisted endpoint without printing its token");
    }

    private static void TestRimBridgeRouteForwarding()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        FakeRimBridgeClient client = new()
        {
            ListResult = WireSuccess("{\"tools\":[{\"name\":\"rimworld/get_game_state\"}] }"),
            CallResult = WireSuccess("{\"success\":true,\"state\":\"ok\",\"evidence\":{\"opaque\":\"keep\"}}")
        };
        ConfigureRoutedFixture(fixture, client);

        BridgeRequest call = Request("bridge", "holder", 77, "call", "rimworld/get_game_state",
            "{\"include\":\"colonists\"}", "--lease", "T001");
        List<string> output = new();
        int exitCode = fixture.State.Execute(call, output.Add, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(call, exitCode, output);
        string json = JsonSerializer.Serialize(response, Program.JsonOptions);

        Assert(exitCode == 0 && client.CallCalls == 1,
            "a valid routed call must be forwarded exactly once");
        Assert(client.LastToolName == "rimworld/get_game_state" &&
               client.LastArguments.GetProperty("include").GetString() == "colonists",
            "the routed tool name and JSON arguments must be preserved");
        Assert(response.RimBridgeRoute?.Success == true &&
               response.RimBridgeRoute.Provenance.Generation == 1 &&
               response.RimBridgeRoute.Provenance.LaunchId == "launch-ready" &&
               response.RimBridgeRoute.Provenance.ProcessId == 101 &&
               response.RimBridgeRoute.Provenance.EndpointPort == 59101,
            "successful routes must attach generation, launch, PID, endpoint, and timestamp provenance");
        Assert(response.RimBridgeRoute.OpaqueEvidence.HasValue &&
               response.RimBridgeRoute.OpaqueEvidence.Value.GetProperty("opaque").GetString() == "keep",
            "opaque bridge evidence metadata must pass through without reinterpretation");
        Assert(json.Contains("profile-route", StringComparison.Ordinal) &&
               !json.Contains("route-secret", StringComparison.Ordinal),
            "route JSON may include profile provenance but never credentials");

        BridgeRequest tools = Request("bridge", "holder", 77, "tools");
        int toolsExit = fixture.State.Execute(tools, _ => { }, () => true);
        Assert(toolsExit == 0 && client.ListCalls == 1,
            "the read-only tools listing must use the same validated route");
    }

    private static void TestRimBridgeRoutePolicyBlocks()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        FakeRimBridgeClient client = new() { CallResult = WireSuccess("{\"success\":true}") };
        ConfigureRoutedFixture(fixture, client);

        foreach (string tool in new[]
        {
            "rimworld/set_mod_enabled",
            "rimworld/reorder_mod",
            "rimworld/restart"
        })
        {
            BridgeRequest request = Request("bridge", "holder", 77, "call", tool,
                "{}", "--lease", "T001");
            int exitCode = fixture.State.Execute(request, _ => { }, () => true);
            JsonCommandResponse response = fixture.State.CreateJsonResponse(request, exitCode,
                Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode ==
                   "RIMBRIDGE_OPERATION_BLOCKED_BY_DEVBRIDGE_POLICY" && client.CallCalls == 0,
                tool + " must be blocked centrally before forwarding");
        }

        Assert(fixture.Adapter.LaunchCalls == 0,
            "a blocked routed lifecycle operation must never trigger an automatic restart");
    }

    private static void TestRimBridgeRouteIdentitySafety()
    {
        using (Fixture generationFixture = Fixture.ReadyWithLease())
        {
            FakeRimBridgeClient client = new() { CallResult = WireSuccess("{\"ok\":true}") };
            ConfigureRoutedFixture(generationFixture, client);
            string runtime = Path.Combine(generationFixture.Root, "Runtime");
            RimBridgeEndpointStore.Save(runtime, new RimBridgeEndpoint
            {
                Host = "127.0.0.1", Port = 59101, Token = "route-secret", LaunchId = "launch-ready",
                Generation = 2, ProcessId = 101, ProcessStartUtcTicks = 1001, DiscoveredUtc = ClockStart
            });
            BridgeRequest request = Request("bridge", "holder", 77, "call", "rimworld/get_game_state",
                "{}", "--lease", "T001");
            int exitCode = generationFixture.State.Execute(request, _ => { }, () => true);
            JsonCommandResponse response = generationFixture.State.CreateJsonResponse(request,
                exitCode, Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == "RIMBRIDGE_ENDPOINT_STALE" &&
                   client.CallCalls == 0 && !File.Exists(RimBridgeEndpointStore.PathFor(runtime)),
                "an endpoint from another generation must be rejected and deleted");
        }

        using (Fixture processFixture = Fixture.ReadyWithLease())
        {
            FakeRimBridgeClient client = new() { CallResult = WireSuccess("{\"ok\":true}") };
            ConfigureRoutedFixture(processFixture, client);
            processFixture.Adapter.Replace(101, 1002);
            string runtime = Path.Combine(processFixture.Root, "Runtime");
            BridgeRequest request = Request("bridge", "holder", 77, "call", "rimworld/get_game_state",
                "{}", "--lease", "T001");
            int exitCode = processFixture.State.Execute(request, _ => { }, () => true);
            JsonCommandResponse response = processFixture.State.CreateJsonResponse(request,
                exitCode, Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == "RIMBRIDGE_PROCESS_IDENTITY_MISMATCH" &&
                   client.CallCalls == 0 && !File.Exists(RimBridgeEndpointStore.PathFor(runtime)),
                "a changed RimWorld PID/start identity must reject and invalidate the endpoint");
        }
    }

    private static void TestRimBridgeRouteLeaseSafety()
    {
        using (Fixture noLease = Fixture.ReadyWithoutLease())
        {
            FakeRimBridgeClient client = new() { CallResult = WireSuccess("{\"ok\":true}") };
            ConfigureRoutedFixture(noLease, client, includeLease: false);
            BridgeRequest request = Request("bridge", "unleased", 88, "call", "rimworld/get_game_state", "{}");
            int exitCode = noLease.State.Execute(request, _ => { }, () => true);
            JsonCommandResponse response = noLease.State.CreateJsonResponse(request, exitCode,
                Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == "RIMBRIDGE_LEASE_REQUIRED" &&
                   client.CallCalls == 0,
                "a routed call without a shared DevBridge lease must be denied");
        }

        using Fixture fixture = Fixture.ReadyWithLeases();
        FakeRimBridgeClient sharedClient = new() { CallResult = WireSuccess("{\"ok\":true}") };
        ConfigureRoutedFixture(fixture, sharedClient, includeLease: true, sharedLeases: true);
        BridgeRequest wrongAgent = Request("bridge", "not-holder", 99, "call", "rimworld/get_game_state",
            "{}", "--lease", "T001");
        int denied = fixture.State.Execute(wrongAgent, _ => { }, () => true);
        JsonCommandResponse deniedResponse = fixture.State.CreateJsonResponse(wrongAgent, denied,
            Array.Empty<string>());
        Assert(deniedResponse.ErrorCode == "RIMBRIDGE_LEASE_REQUIRED" && sharedClient.CallCalls == 0,
            "a lease ID cannot be reused by another durable agent identity");

        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        BridgeRequest expired = Request("bridge", "holder-a", 77, "call", "rimworld/get_game_state",
            "{}", "--lease", "T001");
        int expiredExit = fixture.State.Execute(expired, _ => { }, () => true);
        JsonCommandResponse expiredResponse = fixture.State.CreateJsonResponse(expired, expiredExit,
            Array.Empty<string>());
        Assert(expiredExit != 0 && expiredResponse.ErrorCode == "RIMBRIDGE_LEASE_REQUIRED" &&
               sharedClient.CallCalls == 0,
            "an expired lease must not authorize a routed call");
    }

    private static void TestRimBridgeRouteAuthRedaction()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        FakeRimBridgeClient client = new()
        {
            CallResult = new RimBridgeWireResult
            {
                ErrorCode = "RIMBRIDGE_AUTH_FAILED",
                Error = "server rejected route-secret",
                AuthenticationFailure = true,
                Payload = JsonDocument.Parse("{\"token\":\"route-secret\",\"detail\":\"route-secret\"}").RootElement.Clone()
            }
        };
        ConfigureRoutedFixture(fixture, client);
        string runtime = Path.Combine(fixture.Root, "Runtime");
        BridgeRequest request = Request("bridge", "holder", 77, "call", "rimworld/get_game_state",
            "{}", "--lease", "T001");
        List<string> output = new();
        int exitCode = fixture.State.Execute(request, output.Add, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(request, exitCode, output);
        string json = JsonSerializer.Serialize(response, Program.JsonOptions);
        Assert(exitCode != 0 && response.ErrorCode == "RIMBRIDGE_AUTH_FAILED" &&
               !File.Exists(RimBridgeEndpointStore.PathFor(runtime)) &&
               !json.Contains("route-secret", StringComparison.Ordinal) &&
               !string.Join("\n", output).Contains("route-secret", StringComparison.Ordinal),
            "authentication failure must clear the matching endpoint and redact credentials everywhere");
    }

    private static void TestRimBridgeRouteUnavailableModes()
    {
        using (Fixture disabled = Fixture.ReadyWithLease())
        {
            FakeRimBridgeClient client = new() { CallResult = WireSuccess("{\"ok\":true}") };
            disabled.RouteClient = client;
            BridgeRequest request = Request("bridge", "holder", 77, "call", "rimworld/get_game_state", "{}");
            int exitCode = disabled.State.Execute(request, _ => { }, () => true);
            JsonCommandResponse response = disabled.State.CreateJsonResponse(request, exitCode,
                Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == "RIMBRIDGE_DISABLED" && client.CallCalls == 0,
                "RimBridge-off routing must be denied without contacting a server");
        }

        using Fixture unavailable = Fixture.ReadyWithLease();
        FakeRimBridgeClient unavailableClient = new();
        ConfigureRoutedFixture(unavailable, unavailableClient);
        RimBridgeEndpointStore.Delete(Path.Combine(unavailable.Root, "Runtime"));
        BridgeRequest unavailableRequest = Request("bridge", "holder", 77, "call",
            "rimworld/get_game_state", "{}");
        int unavailableExit = unavailable.State.Execute(unavailableRequest, _ => { }, () => true);
        JsonCommandResponse unavailableResponse = unavailable.State.CreateJsonResponse(unavailableRequest,
            unavailableExit, Array.Empty<string>());
        Assert(unavailableExit != 0 && unavailableResponse.ErrorCode == "RIMBRIDGE_ENDPOINT_NOT_FOUND" &&
               unavailableClient.CallCalls == 0,
            "an optional but unavailable bridge must fail closed without a replacement launch");
    }

    private static void TestRimBridgeWireFailures()
    {
        RimBridgeWireResult auth = RunWireCase((stream, helloId) =>
            WriteTestFrame(stream, new
            {
                v = "gabp/1", type = "response", id = helloId,
                error = new { code = -31000, message = "bad wire-secret" }
            }), TimeSpan.FromSeconds(2));
        Assert(auth.ErrorCode == "RIMBRIDGE_AUTH_FAILED" && auth.AuthenticationFailure &&
               !auth.Error.Contains("wire-secret", StringComparison.Ordinal),
            "GABP authentication errors must be mapped and must not echo credentials");

        RimBridgeWireResult missing = RunWireCase((stream, helloId) =>
        {
            WriteTestFrame(stream, new { v = "gabp/1", type = "response", id = helloId,
                result = new { agentId = "wire-agent" } });
            string callId = FrameId(ReadTestFrame(stream));
            WriteTestFrame(stream, new
            {
                v = "gabp/1", type = "response", id = callId,
                error = new { code = -32601, message = "missing tool" }
            });
        }, TimeSpan.FromSeconds(2), call: true);
        Assert(missing.ErrorCode == "RIMBRIDGE_TOOL_NOT_FOUND",
            "GABP tool-not-found errors must remain deterministic");

        RimBridgeWireResult timeout = RunWireCase((stream, helloId) =>
        {
            WriteTestFrame(stream, new { v = "gabp/1", type = "response", id = helloId,
                result = new { agentId = "wire-agent" } });
            ReadTestFrame(stream);
            Thread.Sleep(250);
        }, TimeSpan.FromMilliseconds(50), call: true);
        Assert(timeout.Timeout && timeout.ErrorCode == "RIMBRIDGE_CALL_TIMEOUT",
            "a stalled GABP response must produce the bounded timeout result");
    }

    private static void TestRimBridgeWireClientInfo()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception serverFailure = null;
        Task server = Task.Run(() =>
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                using NetworkStream stream = client.GetStream();
                using JsonDocument hello = JsonDocument.Parse(ReadTestFrame(stream));
                JsonElement clientInfo = hello.RootElement.GetProperty("params")
                    .GetProperty("clientInfo");
                if (clientInfo.ValueKind != JsonValueKind.Object ||
                    clientInfo.GetProperty("name").GetString() != "DevBridge2.Coordinator" ||
                    string.IsNullOrWhiteSpace(clientInfo.GetProperty("version").GetString()))
                    throw new InvalidOperationException(
                        "session/hello must send GabpClientInfo-shaped client metadata");

                string helloId = hello.RootElement.GetProperty("id").GetString();
                WriteTestFrame(stream, new
                {
                    v = "gabp/1", type = "response", id = helloId,
                    result = new { agentId = "wire-agent" }
                });

                using JsonDocument list = JsonDocument.Parse(ReadTestFrame(stream));
                string listId = list.RootElement.GetProperty("id").GetString();
                WriteTestFrame(stream, new
                {
                    v = "gabp/1", type = "response", id = listId,
                    result = new { tools = Array.Empty<object>() }
                });
            }
            catch (Exception exception)
            {
                serverFailure = exception;
            }
        });

        try
        {
            RimBridgeWireResult result = new RimBridgeClient().ListTools(new RimBridgeEndpoint
            {
                Host = "127.0.0.1",
                Port = port,
                Token = "wire-secret",
                LaunchId = "wire-launch",
                Generation = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                DiscoveredUtc = ClockStart
            }, "wire-launch", TimeSpan.FromSeconds(2));
            Assert(result.Success, "structured RimBridge client information must complete hello");
        }
        finally
        {
            try { server.Wait(TimeSpan.FromSeconds(2)); } catch { }
            listener.Stop();
            if (serverFailure != null)
                throw new InvalidOperationException("wire client-info test server failed", serverFailure);
        }
    }

    private static RimBridgeWireResult RunWireCase(Action<NetworkStream, string> handler,
        TimeSpan timeout, bool call = false)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception serverFailure = null;
        Task server = Task.Run(() =>
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                using NetworkStream stream = client.GetStream();
                string helloId = FrameId(ReadTestFrame(stream));
                handler(stream, helloId);
            }
            catch (Exception exception)
            {
                serverFailure = exception;
            }
        });

        try
        {
            RimBridgeEndpoint endpoint = new()
            {
                Host = "127.0.0.1", Port = port, Token = "wire-secret",
                LaunchId = "wire-launch", Generation = 1, ProcessId = 101,
                ProcessStartUtcTicks = 1001, DiscoveredUtc = ClockStart
            };
            RimBridgeClient client = new();
            if (!call)
                return client.ListTools(endpoint, "wire-launch", timeout);
            using JsonDocument arguments = JsonDocument.Parse("{}");
            return client.CallTool(endpoint, "wire-launch", "rimworld/get_game_state",
                arguments.RootElement, timeout);
        }
        finally
        {
            try { server.Wait(TimeSpan.FromSeconds(2)); } catch { }
            listener.Stop();
            if (serverFailure != null)
                throw new InvalidOperationException("wire test server failed", serverFailure);
        }
    }

    private static string FrameId(string frame)
    {
        using JsonDocument document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("id").GetString();
    }

    private static RimBridgeWireResult WireSuccess(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new RimBridgeWireResult { Success = true, Payload = document.RootElement.Clone() };
    }

    private static void ConfigureRoutedFixture(Fixture fixture, FakeRimBridgeClient client,
        bool includeLease = true, bool sharedLeases = false)
    {
        fixture.RimBridgeMode = RimBridgeMode.Optional;
        fixture.RouteClient = client;
        fixture.RouteVerifier = new FakeRimBridgeGenerationVerifier
        {
            Result = new RimBridgeCompanionVerification
            {
                Status = RimBridgeCompanionVerificationStatus.Match,
                Code = "MATCH",
                LaunchId = "launch-ready",
                Generation = 1,
                ProcessId = 101
            }
        };
        List<TestLease> leases = includeLease
            ? sharedLeases
                ? new List<TestLease>
                {
                    new() { Id = "T001", Agent = "holder-a", ClientProcessId = 77, Generation = 1,
                        StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart },
                    new() { Id = "T002", Agent = "holder-b", ClientProcessId = 78, Generation = 1,
                        StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart }
                }
                : new List<TestLease>
                {
                    new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 1,
                        StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart }
                }
            : new List<TestLease>();
        fixture.WriteState(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.READY,
            LaunchId = "launch-ready",
            LaunchGeneration = 1,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            LaunchStartedUtc = ClockStart,
            ProfileMode = ModProfile.LegacyMode,
            LaunchProfileFingerprint = "profile-route",
            Leases = leases,
            RimBridge = new RimBridgeIntegrationState
            {
                ConfiguredMode = "optional",
                LifecycleState = RimBridgeLifecycleState.READY,
                PackageId = RimBridgeIntegrationConstants.PackageId,
                TokenAvailable = true,
                Host = "127.0.0.1",
                Port = 59101,
                LaunchId = "launch-ready",
                Generation = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                CompanionAvailable = true,
                CompanionVerified = true,
                CompanionToolName = RimBridgeIntegrationConstants.CompanionToolName,
                CompanionLaunchId = "launch-ready",
                CompanionGeneration = 1,
                CompanionProcessId = 101
            }
        });
        fixture.State = fixture.Reload();
        RimBridgeEndpointStore.Save(Path.Combine(fixture.Root, "Runtime"), new RimBridgeEndpoint
        {
            Host = "127.0.0.1", Port = 59101, Token = "route-secret", LaunchId = "launch-ready",
            Generation = 1, ProcessId = 101, ProcessStartUtcTicks = 1001, DiscoveredUtc = ClockStart
        });
    }

    private static void TestDevBridgeGenerationContext()
    {
        string root = Path.Combine(Path.GetTempPath(), "DevBridge2-generation-context-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "About"));
        File.WriteAllText(Path.Combine(root, "About", "About.xml"),
            "<ModMetaData><modVersion>1.2.4</modVersion></ModMetaData>");
        try
        {
            Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase)
            {
                ["DEVBRIDGE_ROOT"] = root,
                ["DEVBRIDGE_LAUNCH_ID"] = "launch-context",
                ["DEVBRIDGE_GENERATION"] = "17",
                ["DEVBRIDGE_PROFILE_FINGERPRINT"] = "profile-hash",
                ["DEVBRIDGE_BASELINE_FINGERPRINT"] = "baseline-hash",
                ["DEVBRIDGE_PROFILE_MODE"] = "projects"
            };
            DevBridgeGenerationContextPayload context = DevBridgeGenerationContext.Read(
                environment, null, 700, 800);
            string json = JsonSerializer.Serialize(context, Program.JsonOptions);
            DataContractJsonSerializer toolSerializer = new(typeof(DevBridgeGenerationContextPayload));
            using MemoryStream toolStream = new();
            toolSerializer.WriteObject(toolStream, context);
            string toolJson = Encoding.UTF8.GetString(toolStream.ToArray());
            Assert(context.Success && context.Available && context.LaunchId == "launch-context" &&
                   context.Generation == 17 && context.ProcessId == 700 &&
                   context.ProcessStartUtcTicks == 800 && context.DevBridge2ModVersion == "1.2.4" &&
                   context.RimBridgeIntegrationSchemaVersion == "rimbridge-integration/v1" &&
                   json.Contains("profile-hash", StringComparison.Ordinal) &&
                   toolJson.Contains("\"launchId\"", StringComparison.Ordinal) &&
                   !json.Contains("token", StringComparison.OrdinalIgnoreCase),
                "complete generation context must serialize identity and fingerprints without credentials");

            Dictionary<string, string> missing = new(environment);
            missing.Remove("DEVBRIDGE_LAUNCH_ID");
            DevBridgeGenerationContextPayload missingContext = DevBridgeGenerationContext.Read(
                missing, null, 700, 800);
            Assert(!missingContext.Success && missingContext.ErrorCode == "DEVBRIDGE_CONTEXT_INCOMPLETE",
                "missing launch identity must be reported as an incomplete context");

            Dictionary<string, string> malformed = new(environment)
            {
                ["DEVBRIDGE_GENERATION"] = "not-a-generation"
            };
            DevBridgeGenerationContextPayload malformedContext = DevBridgeGenerationContext.Read(
                malformed, null, 700, 800);
            Assert(!malformedContext.Success && malformedContext.ErrorCode == "DEVBRIDGE_GENERATION_INVALID",
                "malformed generation values must fail closed");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestRimBridgeCompanionIdentityValidation()
    {
        RimBridgeCompanionVerification match = VerifyCompanionContext(
            "launch-context", 17, 700, "launch-context", 17, 700);
        Assert(match.Status == RimBridgeCompanionVerificationStatus.Match,
            "matching companion identity must be accepted");

        RimBridgeCompanionVerification hostSerialized = VerifyCompanionContext(
            "launch-context", 17, 700, "launch-context", 17, 700, pascalCase: true);
        Assert(hostSerialized.Status == RimBridgeCompanionVerificationStatus.Match,
            "host-serialized PascalCase companion identity must be accepted");

        RimBridgeCompanionVerification generationMismatch = VerifyCompanionContext(
            "launch-context", 18, 700, "launch-context", 17, 700);
        Assert(generationMismatch.Status == RimBridgeCompanionVerificationStatus.Mismatch &&
               generationMismatch.Code == RimBridgeIntegrationConstants.CompanionIdentityMismatchCode,
            "generation mismatch must return the stable companion identity error");

        RimBridgeCompanionVerification launchMismatch = VerifyCompanionContext(
            "other-launch", 17, 700, "launch-context", 17, 700);
        Assert(launchMismatch.Status == RimBridgeCompanionVerificationStatus.Mismatch,
            "launch ID mismatch must fail closed");

        RimBridgeCompanionVerification processMismatch = VerifyCompanionContext(
            "launch-context", 17, 701, "launch-context", 17, 700);
        Assert(processMismatch.Status == RimBridgeCompanionVerificationStatus.Mismatch,
               "PID mismatch must fail closed");
    }

    private static void TestRimBridgeCompanionUnavailable()
    {
        RimBridgeCompanionVerification result = RimBridgeCompanionClient.Verify(new RimBridgeEndpoint
        {
            Host = "127.0.0.1",
            Port = 1,
            Token = "test-token",
            LaunchId = "launch-context",
            Generation = 17,
            ProcessId = 700,
            ProcessStartUtcTicks = 800,
            DiscoveredUtc = ClockStart
        }, "launch-context", 17, 700, TimeSpan.FromMilliseconds(100));

        Assert(result.Status == RimBridgeCompanionVerificationStatus.Unavailable,
            "an absent companion must be reported as unavailable rather than treated as identity proof");
    }

    private static void TestCoreModRemainsSdkFree()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName,
            "Source", "Mod", "DevBridge2.csproj")))
            directory = directory.Parent;
        Assert(directory != null, "the repository root must be discoverable from the test output");

        string project = File.ReadAllText(Path.Combine(directory.FullName,
            "Source", "Mod", "DevBridge2.csproj"));
        Assert(!project.Contains("RimBridgeServer.Sdk", StringComparison.OrdinalIgnoreCase),
            "the core mod project must not reference RimBridgeServer.Sdk");
        foreach (string source in Directory.EnumerateFiles(Path.Combine(directory.FullName,
                     "Source", "Mod"), "*.cs", SearchOption.TopDirectoryOnly))
            Assert(!File.ReadAllText(source).Contains("RimBridgeServer", StringComparison.OrdinalIgnoreCase),
                "the core mod source must not acquire a RimBridgeServer dependency");
    }

    private static RimBridgeCompanionVerification VerifyCompanionContext(
        string reportedLaunchId, int reportedGeneration, int reportedProcessId,
        string expectedLaunchId, int expectedGeneration, int expectedProcessId,
        bool pascalCase = false)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception serverFailure = null;
        Task server = Task.Run(() =>
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                using NetworkStream stream = client.GetStream();
                ReadTestFrame(stream);
                WriteTestFrame(stream, new
                {
                    v = "gabp/1",
                    type = "response",
                    id = "hello",
                    result = new { agentId = "test-agent" }
                });
                ReadTestFrame(stream);
                Dictionary<string, object> context = pascalCase
                    ? new Dictionary<string, object>
                    {
                        ["Success"] = true,
                        ["Available"] = true,
                        ["SchemaVersion"] = RimBridgeIntegrationConstants.CompanionSchemaVersion,
                        ["LaunchId"] = reportedLaunchId,
                        ["Generation"] = reportedGeneration,
                        ["ProcessId"] = reportedProcessId,
                        ["RimBridgeIntegrationSchemaVersion"] = "rimbridge-integration/v1"
                    }
                    : new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["available"] = true,
                        ["schemaVersion"] = RimBridgeIntegrationConstants.CompanionSchemaVersion,
                        ["launchId"] = reportedLaunchId,
                        ["generation"] = reportedGeneration,
                        ["processId"] = reportedProcessId,
                        ["rimBridgeIntegrationSchemaVersion"] = "rimbridge-integration/v1"
                    };
                WriteTestFrame(stream, new
                {
                    v = "gabp/1",
                    type = "response",
                    id = "context",
                    result = context
                });
            }
            catch (Exception exception)
            {
                serverFailure = exception;
            }
        });

        try
        {
            return RimBridgeCompanionClient.Verify(new RimBridgeEndpoint
            {
                Host = "127.0.0.1",
                Port = port,
                Token = "test-token",
                LaunchId = expectedLaunchId,
                Generation = expectedGeneration,
                ProcessId = expectedProcessId,
                ProcessStartUtcTicks = 800,
                DiscoveredUtc = ClockStart
            }, expectedLaunchId, expectedGeneration, expectedProcessId,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            try { server.Wait(TimeSpan.FromSeconds(2)); } catch { }
            listener.Stop();
            if (serverFailure != null)
                throw new InvalidOperationException("companion test server failed", serverFailure);
        }
    }

    private static string ReadTestFrame(NetworkStream stream)
    {
        List<byte> header = new();
        while (true)
        {
            int value = stream.ReadByte();
            if (value < 0)
                throw new IOException("companion test server received an incomplete frame");
            header.Add((byte)value);
            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' &&
                header[^2] == '\r' && header[^1] == '\n')
                break;
        }
        string headerText = Encoding.ASCII.GetString(header.ToArray());
        int length = int.Parse(headerText.Split(new[] { "Content-Length:" },
            StringSplitOptions.None)[1].Split(new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries)[0].Trim(), CultureInfo.InvariantCulture);
        byte[] body = new byte[length];
        int offset = 0;
        while (offset < body.Length)
        {
            int read = stream.Read(body, offset, body.Length - offset);
            if (read <= 0)
                throw new IOException("companion test server received an incomplete body");
            offset += read;
        }
        return Encoding.UTF8.GetString(body);
    }

    private static void WriteTestFrame(NetworkStream stream, object value)
    {
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Program.JsonOptions));
        byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length +
            "\r\nContent-Type: application/json\r\n\r\n");
        stream.Write(header, 0, header.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

}

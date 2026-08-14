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
    private static void TestCoordinatorRootArgumentForms()
    {
        string root = Path.Combine(Path.GetTempPath(), "DevBridge2-argument-" + Guid.NewGuid().ToString("N"));
        ParsedArguments separated = ParsedArguments.Parse(new[] { "--coordinator-root", root, "--json", "status" });
        ParsedArguments equals = ParsedArguments.Parse(new[] { "--coordinator-root=" + root, "--json", "status" });
        Assert(separated.Command.SequenceEqual(new[] { "--json", "status" }) &&
            equals.Command.SequenceEqual(new[] { "--json", "status" }),
            "both coordinator-root forms must preserve command forwarding");
    }

    private static void TestPlainRestartUsesAggregateControlProfile()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        byte[] original = File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"));
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = setup.Fixture.State.Execute(Request("restart", "agent", 1), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());
        Assert(exitCode == 0, "plain restart must launch the aggregate control profile");
        Assert(!File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")).SequenceEqual(original),
            "plain restart must not preserve the production ModsConfig implicitly");
        Assert(ActiveMods(setup.Fixture.Root).SequenceEqual(ModProfileResolver.AlwaysOnPackageIds,
                   StringComparer.OrdinalIgnoreCase),
            "plain restart with no active intent must install exactly the minimal control profile");
        Assert(response.LaunchProfileMode == "aggregate-minimal-control" &&
               response.ResolverProfileMode == ModProfile.BaselineMode &&
               response.ProfileMode == ModProfile.BaselineMode &&
               response.FrozenGeneration == 1 && response.FrozenRegistrationIds.Count == 0 &&
               !response.AggregateFreezePending,
            "plain restart must expose an immutable, completed minimal-control freeze");
    }

    private static void TestProjectIntentAggregationAndFreeze()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "aggregate intent: baseline capture must succeed");
        BridgeRequest first = Request("project", "agent-a", 101, "register", "--id", "reg-a", "HORTICULTURE");
        first.SessionId = "session-a";
        Assert(setup.Fixture.State.Execute(first, _ => { }, () => true) == 0,
            "first project intent must register");
        BridgeRequest duplicate = Request("project", "agent-a", 102, "register", "--id", "reg-a", "horticulture");
        duplicate.SessionId = "session-a";
        Assert(setup.Fixture.State.Execute(duplicate, _ => { }, () => true) == 0,
            "an identical owner/session/alias request must renew rather than duplicate");

        BridgeRequest second = Request("project", "agent-b", 202, "register", "--id", "reg-b", "AQUACULTURE");
        second.SessionId = "session-b";
        Assert(setup.Fixture.State.Execute(second, _ => { }, () => true) == 0,
            "a distinct requester must retain a distinct project intent");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        BridgeRequest restart = Request("restart", "agent-a", 101);
        restart.SessionId = "session-a";
        Assert(setup.Fixture.State.Execute(restart, _ => { }, () => true) == 0,
            "aggregate restart must launch the union of active intents");

        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status", "agent-a", 101), 0, Array.Empty<string>());
        Assert(response.LaunchProfileMode == "aggregate-projects" &&
               response.RequestedProjects.SequenceEqual(new[] { "aquaculture", "horticulture" }) &&
               response.FrozenRequestedProjects.SequenceEqual(response.RequestedProjects),
            "aggregate aliases must be canonical, case-insensitive, and order-independent");
        Assert(response.ActiveProjectIntents.Count == 2 && response.FrozenRegistrationIds.SequenceEqual(new[] { "reg-a", "reg-b" }),
            "all distinct requesters must be included and frozen by stable registration ID");
        Assert(response.FrozenRegistrations.Select(value => value.Owner)
                   .SequenceEqual(new[] { "agent-a", "agent-b" }),
            "frozen registration evidence must retain owner attribution");
        Assert(response.FrozenResolvedMods.Contains("lan.horticulture.novelseeds", StringComparer.OrdinalIgnoreCase) &&
               response.FrozenResolvedMods.Contains("lan.aquaculture.fishing", StringComparer.OrdinalIgnoreCase) &&
               response.FrozenResolvedMods.Distinct(StringComparer.OrdinalIgnoreCase).Count() == response.FrozenResolvedMods.Count,
            "frozen aggregate evidence must contain the complete deduplicated closure");
        Assert(response.FrozenLaunchOwner == "agent-a@101" && !string.IsNullOrWhiteSpace(response.FrozenLaunchRequestKey) &&
               response.AggregateGenerations.Any(value => value.Generation == response.FrozenGeneration),
            "freeze evidence must preserve launch ownership, request key, and generation history");
    }

    private static void TestLateProjectIntentQueuesAndDeniesTest()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "late intent: baseline capture must succeed");
        BridgeRequest first = Request("project", "agent-a", 101, "register", "--id", "reg-a", "horticulture");
        first.SessionId = "session-a";
        Assert(setup.Fixture.State.Execute(first, _ => { }, () => true) == 0,
            "late intent: first registration must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        BridgeRequest restart = Request("restart", "agent-a", 101);
        restart.SessionId = "session-a";
        Assert(setup.Fixture.State.Execute(restart, _ => { }, () => true) == 0,
            "late intent: first generation must become ready");

        BridgeRequest late = Request("project", "agent-b", 202, "register", "--id", "reg-b", "aquaculture");
        late.SessionId = "session-b";
        Assert(setup.Fixture.State.Execute(late, _ => { }, () => true) == 0,
            "late intent: registration after freeze must be accepted for the next generation");
        BridgeRequest queuedStatusRequest = Request("status", "agent-b", 202);
        queuedStatusRequest.SessionId = "session-b";
        JsonCommandResponse queued = setup.Fixture.State.CreateJsonResponse(
            queuedStatusRequest, 0, Array.Empty<string>());
        Assert(queued.FrozenRegistrationIds.SequenceEqual(new[] { "reg-a" }) &&
               queued.QueuedProjectIntents.Count == 1 && queued.QueuedProjectIntents[0].Id == "reg-b" &&
               queued.MissingProjects.SequenceEqual(new[] { "aquaculture" }),
            "late registration must be visibly queued without changing the frozen generation");

        BridgeRequest waitReady = Request("wait-ready", "agent-b", 202);
        waitReady.SessionId = "session-b";
        List<string> waitReadyMessages = new();
        Assert(setup.Fixture.State.Execute(waitReady, waitReadyMessages.Add, () => true) == 0 &&
               waitReadyMessages.Any(value => value.Contains("DevBridge.cmd restart", StringComparison.OrdinalIgnoreCase)),
            "wait-ready must direct a requester with queued projects to restart before testing");

        BridgeRequest testBegin = Request("test", "agent-b", 202, "begin");
        testBegin.SessionId = "session-b";
        int denied = setup.Fixture.State.Execute(testBegin, _ => { }, () => true);
        JsonCommandResponse deniedResponse = setup.Fixture.State.CreateJsonResponse(
            testBegin, denied, Array.Empty<string>());
        JsonCommandResponse queuedJson = setup.Fixture.State.CreateJsonResponse(
            queuedStatusRequest, 0, Array.Empty<string>());
        List<string> statusMessages = new();
        setup.Fixture.State.Execute(queuedStatusRequest, statusMessages.Add, () => true);
        List<string> doctorMessages = new();
        BridgeRequest doctorRequest = Request("doctor", "agent-b", 202);
        doctorRequest.SessionId = "session-b";
        setup.Fixture.State.Execute(doctorRequest, doctorMessages.Add, () => true);
        Assert(denied != 0 && deniedResponse.ErrorCode == "PROJECT_PROFILE_MISSING" &&
               deniedResponse.NextAction.Contains("restart", StringComparison.OrdinalIgnoreCase) &&
               queuedJson.NextAction.Contains("restart", StringComparison.OrdinalIgnoreCase) &&
               statusMessages.Any(value => value.Contains("DevBridge.cmd restart", StringComparison.OrdinalIgnoreCase)) &&
               doctorMessages.Any(value => value.Contains("DevBridge.cmd restart", StringComparison.OrdinalIgnoreCase)) &&
               setup.Fixture.Adapter.LaunchCalls == 1,
            "test begin must be denied for a queued registration without a replacement launch");

        PersistedState pending = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        pending.RestartPending = true;
        pending.TargetGeneration = pending.Generation + 1;
        pending.Phase = BridgePhase.DRAINING;
        pending.LaunchOwner = "agent-a@101";
        pending.LaunchRequestKey = "restart-" + pending.TargetGeneration;
        pending.RestartRequestedUtc = setup.Fixture.Clock.UtcNow;
        setup.Fixture.WriteState(pending);
        setup.Fixture.State = setup.Fixture.Reload();

        BridgeRequest invalidLateRequest = Request(
            "restart", "agent-a", 101, "--projects", "not-a-managed-project");
        invalidLateRequest.SessionId = "session-a";
        int invalidLate = setup.Fixture.State.Execute(
            invalidLateRequest, _ => { }, () => true);
        JsonCommandResponse invalidLateResponse = setup.Fixture.State.CreateJsonResponse(
            queuedStatusRequest, invalidLate, Array.Empty<string>());
            Assert(invalidLate != 0 && invalidLateResponse.ErrorCode == "PROFILE_UNKNOWN_PROJECT" &&
                invalidLateResponse.FrozenRegistrationIds.SequenceEqual(new[] { "reg-a" }) &&
                invalidLateResponse.QueuedProjectIntents.Count == 1 &&
                setup.Fixture.Adapter.LaunchCalls == 1,
            "an invalid late project request must fail closed without mutating the frozen or queued evidence");
    }

    private static void TestAggregateFirstGuidanceDuringActiveTest()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "aggregate-first: baseline capture must succeed");
        BridgeRequest first = Request("project", "agent-a", 101, "register", "--id", "reg-a", "horticulture");
        first.SessionId = "session-a";
        Assert(setup.Fixture.State.Execute(first, _ => { }, () => true) == 0,
            "aggregate-first: initial project intent must register");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        BridgeRequest restart = Request("restart", "agent-a", 101);
        restart.SessionId = "session-a";
        Assert(setup.Fixture.State.Execute(restart, _ => { }, () => true) == 0,
            "aggregate-first: initial profile must become ready");

        BridgeRequest begin = Request("test", "agent-a", 101, "begin");
        begin.SessionId = "session-a";
        Assert(setup.Fixture.State.Execute(begin, _ => { }, () => true) == 0,
            "aggregate-first: another agent's test lease must be active");

        BridgeRequest observer = Request("status", "agent-b", 202);
        observer.SessionId = "session-b";
        JsonCommandResponse beforeRegistration = setup.Fixture.State.CreateJsonResponse(
            observer, 0, Array.Empty<string>());
        Assert(beforeRegistration.ActiveTests == 1 && beforeRegistration.AggregateAllowed &&
               beforeRegistration.ProfileStrategy == "aggregate-first" &&
               beforeRegistration.NextAction.Contains("register", StringComparison.OrdinalIgnoreCase) &&
               beforeRegistration.NextAction.Contains("do not block registration", StringComparison.OrdinalIgnoreCase),
            "status must tell an unregistered agent to join the aggregate despite an active test");

        BridgeRequest second = Request("project", "agent-b", 202, "register", "--id", "reg-b", "aquaculture");
        second.SessionId = "session-b";
        List<string> messages = new();
        Assert(setup.Fixture.State.Execute(second, messages.Add, () => true) == 0 &&
               messages.Any(value => value.Contains("Aggregate-first", StringComparison.OrdinalIgnoreCase)) &&
               messages.Any(value => value.Contains("do not block project registration", StringComparison.OrdinalIgnoreCase)),
            "an active test must not prevent a second agent from registering into the aggregate");

        JsonCommandResponse queued = setup.Fixture.State.CreateJsonResponse(observer, 0, Array.Empty<string>());
        Assert(queued.QueuedProjectIntents.Any(value => value.Id == "reg-b") &&
               queued.NextAction.Contains("combine all active registrations", StringComparison.OrdinalIgnoreCase) &&
               queued.NextAction.Contains("Do not wait", StringComparison.OrdinalIgnoreCase),
            "queued aggregate guidance must prefer a combined restart over an exclusive run");
    }

    private static void TestConcurrentProjectIntentAggregation()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "concurrent intent: baseline capture must succeed");
        using ManualResetEventSlim start = new(false);
        BridgeRequest first = Request("project", "agent-a", 101, "register", "--id", "reg-a", "WILDLIFE");
        first.SessionId = "session-a";
        BridgeRequest second = Request("project", "agent-b", 202, "register", "--id", "reg-b", "deferred-reality");
        second.SessionId = "session-b";
        Task<int> firstTask = Task.Run(() =>
        {
            start.Wait();
            return setup.Fixture.State.Execute(first, _ => { }, () => true);
        });
        Task<int> secondTask = Task.Run(() =>
        {
            start.Wait();
            return setup.Fixture.State.Execute(second, _ => { }, () => true);
        });
        start.Set();
        Task.WaitAll(firstTask, secondTask);
        Assert(firstTask.Result == 0 && secondTask.Result == 0,
            "concurrent project registrations must both commit");

        setup.Fixture.Adapter.ReadyOnLaunch = true;
        BridgeRequest restart = Request("restart", "agent-b", 202);
        restart.SessionId = "session-b";
        Assert(setup.Fixture.State.Execute(restart, _ => { }, () => true) == 0,
            "concurrent intent: aggregate restart must complete");
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(response.FrozenRegistrationIds.SequenceEqual(new[] { "reg-a", "reg-b" }) &&
               response.FrozenRequestedProjects.SequenceEqual(new[] { "deferred-reality", "wildlife" }) &&
               response.FrozenRegistrations.Select(value => value.Owner)
                   .SequenceEqual(new[] { "agent-a", "agent-b" }),
            "concurrent aggregation must retain every requester and deterministic alias/ID order");
    }

    private static void TestProjectIntentReleaseAndExpiry()
    {
        using ProfileSetup released = ProfileSetup.Create();
        Assert(released.CaptureBaseline(), "release: baseline capture must succeed");
        BridgeRequest registration = Request("project", "agent-a", 101, "register", "--id", "reg-release", "horticulture");
        registration.SessionId = "session-a";
        Assert(released.Fixture.State.Execute(registration, _ => { }, () => true) == 0,
            "release: registration must succeed");
        BridgeRequest releaseRequest = Request("project", "agent-a", 101, "release", "reg-release");
        releaseRequest.SessionId = "session-a";
        Assert(released.Fixture.State.Execute(releaseRequest, _ => { }, () => true) == 0,
            "release: owner must be able to release its intent");
        released.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(released.Fixture.State.Execute(Request("restart", "agent-a", 101), _ => { }, () => true) == 0,
            "release: a future restart must proceed after release");
        JsonCommandResponse releaseResponse = released.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(releaseResponse.LaunchProfileMode == "aggregate-minimal-control" &&
               releaseResponse.FrozenRegistrationIds.Count == 0 &&
               releaseResponse.AggregateGenerations.Count >= 1,
            "release must affect only the future generation while preserving generation evidence");

        using ProfileSetup expired = ProfileSetup.Create();
        Assert(expired.CaptureBaseline(), "expiry: baseline capture must succeed");
        BridgeRequest expiring = Request("project", "agent-a", 101, "register", "--id", "reg-expire", "wildlife");
        expiring.SessionId = "session-a";
        Assert(expired.Fixture.State.Execute(expiring, _ => { }, () => true) == 0,
            "expiry: registration must succeed");
        expired.Fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        JsonCommandResponse expiredStatus = expired.Fixture.State.CreateJsonResponse(
            Request("status", "agent-a", 101), 0, Array.Empty<string>());
        Assert(expiredStatus.ActiveProjectIntents.Count == 0 &&
               expiredStatus.QueuedProjectIntents.Count == 0,
            "expired intent must be removed from future aggregate requests");
        expired.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(expired.Fixture.State.Execute(Request("restart", "agent-a", 101), _ => { }, () => true) == 0,
            "expiry: a future restart must still launch the minimal control profile");
        Assert(ActiveMods(expired.Fixture.Root).SequenceEqual(ModProfileResolver.AlwaysOnPackageIds,
                   StringComparer.OrdinalIgnoreCase),
            "expiry must not leave an expired project in the launched profile");
    }

    private static void TestLegacyProductionSafety()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "legacy safety: baseline capture must succeed");
        BridgeRequest registration = Request("project", "agent-a", 101, "register", "--id", "reg-legacy", "horticulture");
        registration.SessionId = "session-a";
        Assert(setup.Fixture.State.Execute(registration, _ => { }, () => true) == 0,
            "legacy safety: project intent must register");
        byte[] original = File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"));
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "human", 404, "--legacy-production"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());
        Assert(exitCode != 0 && response.ErrorCode == "PROFILE_LEGACY_CONFLICT" &&
               setup.Fixture.Adapter.LaunchCalls == 0 &&
               File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")).SequenceEqual(original),
            "legacy production must be explicit, never override active intent, and fail before writes or launch");
    }

}

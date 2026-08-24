using System.Text;
using System.Text.Json;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestCanonicalIdentityContract()
    {
        using Fixture fixture = new(new PersistedState
        {
            InstallationId = "stable-installation",
            Generation = 1,
            Phase = BridgePhase.READY,
            LaunchId = "launch-1",
            LaunchGeneration = 1,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001
        });

        JsonCommandResponse first = fixture.State.CreateJsonResponse(Request("status"), 0,
            Array.Empty<string>());
        CoordinatorState restarted = fixture.Reload();
        JsonCommandResponse second = restarted.CreateJsonResponse(Request("status"), 0,
            Array.Empty<string>());
        Assert(first.Identity != null && second.Identity != null &&
               first.Identity.InstallationId == "stable-installation" &&
               first.Identity.InstallationId == second.Identity.InstallationId &&
               first.Identity.OwnerId == second.Identity.OwnerId &&
               first.Identity.Coordinator.InstanceId != second.Identity.Coordinator.InstanceId,
            "coordinator restart must preserve installation/owner identity while changing only coordinator identity");

        PersistedState next = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        next.Generation = 2;
        next.LaunchGeneration = 2;
        next.LaunchId = "launch-2";
        next.ProcessId = 202;
        next.ProcessStartUtcTicks = 2002;
        next.Phase = BridgePhase.READY;
        fixture.WriteState(next);
        fixture.State = fixture.Reload();
        JsonCommandResponse newer = fixture.State.CreateJsonResponse(Request("status"), 0,
            Array.Empty<string>());
        Assert(newer.Identity.InstallationId == first.Identity.InstallationId &&
               newer.Identity.Runtime.Generation == 2 &&
               newer.Identity.ExpectedRimWorldProcess.ProcessId == 202 &&
               newer.Identity.ExpectedRimWorldProcess.StartIdentity == 2002,
            "RimWorld restart must change generation/process identity without changing installation identity");

        next = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        next.RestartPending = true;
        next.TargetGeneration = 3;
        next.Phase = BridgePhase.RESTARTING;
        next.ProjectIntents = new List<ProjectIntentRegistration>
        {
            new() { Id = "retired", Status = "EXPIRED" }
        };
        fixture.WriteState(next);
        fixture.State = fixture.Reload();
        JsonCommandResponse transition = fixture.State.CreateJsonResponse(Request("status"), 0,
            Array.Empty<string>());
        Assert(transition.Identity.Runtime.Transition == "replacing-generation" &&
               transition.Identity.Runtime.TargetGeneration == 3 &&
               transition.Identity.StaleState.RetiredRegistrationCount == 1 &&
               transition.State != BridgePhase.READY.ToString(),
            "replacement must be explicit transitional state and must not fabricate READY");

        string alternateRoot = Path.Combine(Directory.GetParent(fixture.Root).FullName, "Alternate");
        Directory.CreateDirectory(Path.Combine(alternateRoot, "Runtime"));
        File.WriteAllText(Path.Combine(alternateRoot, "Runtime", "state.json"),
            "{\"CoordinatorRoot\":\"" + alternateRoot.Replace("\\", "\\\\") +
            "\",\"InstallationId\":\"different-installation\"}", Encoding.UTF8);
        BridgeRequest doctor = Request("doctor");
        int doctorExit = fixture.State.Execute(doctor, _ => { }, () => true);
        JsonCommandResponse doctorResponse = fixture.State.CreateJsonResponse(doctor, doctorExit,
            Array.Empty<string>());
        string doctorJson = JsonSerializer.Serialize(doctorResponse, Program.JsonOptions);
        using JsonDocument document = JsonDocument.Parse(doctorJson);
        Assert(document.RootElement.GetProperty("identity").GetProperty("installationId").GetString() ==
                   "stable-installation" &&
               document.RootElement.GetProperty("identity").GetProperty("alternateRoots").GetArrayLength() == 1 &&
               document.RootElement.GetProperty("findings").EnumerateArray().Any(value =>
                   value.GetProperty("code").GetString() == "DUPLICATE_INSTALLATION_ROOT"),
            "doctor JSON must diagnose an alternate root without adopting its owner");
    }
}

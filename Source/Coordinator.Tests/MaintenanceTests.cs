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
    private static void TestStatusSnapshotConsistency()
    {
        using Fixture fixture = Fixture.MaintenanceWithLease();
        fixture.Adapter.AddExtraMatchingProcessOnSecondEnumeration = true;
        BridgeRequest request = Request("status", "holder", 77);
        List<string> output = new();
        int exitCode = fixture.State.Execute(request, output.Add, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(request, exitCode, output);

        Assert(exitCode == 0 && response.MaintenanceReady,
            "a clean authoritative snapshot must report maintenanceReady");
        Assert(fixture.Adapter.EnumerationCalls == 1,
            "status must not perform a second independent enumeration");
        Assert(fixture.Adapter.TerminationRequests == 0 && fixture.Adapter.LaunchCalls == 0,
            "status snapshotting must make zero termination and launch calls");
    }

    private static void TestMaintenanceInspectionNoLaunch()
    {
        using (Fixture statusFixture = Fixture.MaintenanceWithLease())
        {
            statusFixture.Adapter.EnumerationIncomplete = true;
            int statusExit = statusFixture.State.Execute(Request("status", "holder", 77), _ => { }, () => true);
            JsonCommandResponse status = statusFixture.State.CreateJsonResponse(Request("status", "holder", 77), statusExit,
                Array.Empty<string>());
            Assert(statusExit == 0 && !status.MaintenanceReady &&
                status.ErrorCode == ProcessInspection.ErrorCode,
                "status must report persisted maintenance state as non-copy-safe when re-enumeration is uncertain");
            Assert(statusFixture.Adapter.TerminationRequests == 0 && statusFixture.Adapter.LaunchCalls == 0,
                "uncertain status reconciliation must make zero termination and launch calls");
        }

        using (Fixture ensureFixture = Fixture.MaintenanceWithLease())
        {
            ensureFixture.Adapter.EnumerationIncomplete = true;
            int ensure = ensureFixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true);
            Assert(ensure != 0 && ensureFixture.Adapter.TerminationRequests == 0 &&
                ensureFixture.Adapter.LaunchCalls == 0,
                "uncertain ensure-ready must make zero termination and launch calls");
        }

        using (Fixture restartFixture = Fixture.MaintenanceWithLease())
        {
            restartFixture.Adapter.EnumerationIncomplete = true;
            int restart = restartFixture.State.Execute(Request("restart", "holder", 77, "T001"), _ => { }, () => true);
            Assert(restart != 0 && restartFixture.Adapter.TerminationRequests == 0 &&
                restartFixture.Adapter.LaunchCalls == 0,
                "uncertain restart must make zero termination and launch calls");
        }
    }

}

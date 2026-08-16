using System.Collections.Concurrent;
using System.Text.Json;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestCoordinatorTraceLifecycleOrder()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);

        harness.Send("status");
        harness.Shutdown();

        List<TraceView> events = ReadTrace(fixture.Root);
        TraceView startup = events.FirstOrDefault(value =>
            value.Event == "coordinator.process.started");
        Assert(startup != null && startup.BuildIdentity != null &&
            startup.ProtocolVersion == DevBridgeSchemaVersions.CoordinatorProtocolMajor,
            "coordinator startup trace did not include the build/protocol identity");

        TraceView accepted = events.FirstOrDefault(value =>
            value.Event == "ipc.request.accepted" && value.Command == "status");
        Assert(accepted != null && !string.IsNullOrWhiteSpace(accepted.RequestId),
            "status request was not accepted with a trace requestId");

        string[] requestSequence =
        {
            "ipc.request.accepted",
            "command.dispatch.started",
            "command.dispatch.completed",
            "ipc.response.serialization.started",
            "ipc.response.serialization.completed",
            "ipc.terminal_result.write"
        };
        int previous = -1;
        foreach (string eventName in requestSequence)
        {
            int current = IndexOf(events, eventName, accepted.RequestId);
            Assert(current > previous, eventName + " was missing or out of order for status request");
            previous = current;
        }

        Assert(events.Any(value => value.Event == "coordinator.shutdown.accepted" &&
            value.RequestId != null), "shutdown acceptance was not traced with request correlation");
        Assert(events.Any(value => value.Event == "coordinator.process.shutting_down"),
            "coordinator process shutdown boundary was not traced");
        Assert(events.Any(value => value.Event == "coordinator.process.shutdown.completed"),
            "coordinator process shutdown completion was not traced");
    }

    private static void TestCoordinatorTraceSeparatesStoppedPersistenceFromResult()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);

        harness.Send("stop", "T001");
        harness.Shutdown();

        List<TraceView> events = ReadTrace(fixture.Root);
        TraceView accepted = events.FirstOrDefault(value =>
            value.Event == "ipc.request.accepted" && value.Command == "stop");
        Assert(accepted != null, "stop request was not accepted");

        int phase = IndexOf(events, "lifecycle.phase.transition", accepted.RequestId,
            value => value.Phase == "STOPPED" && value.Detail == "READY->STOPPED");
        int persisted = IndexOf(events, "state.save.completed", accepted.RequestId);
        int result = IndexOf(events, "ipc.terminal_result.write", accepted.RequestId);
        Assert(phase >= 0 && persisted > phase && result > persisted,
            "STOPPED transition/persistence was not distinguishable before terminal result write");

        TraceView completed = events[persisted];
        Assert(completed.Success == true && completed.DurationMs.HasValue,
            "state persistence completion lacked bounded timing metadata");
    }

    private static void TestCoordinatorTraceSecretSafetyAndBounds()
    {
        string root = Path.Combine(Path.GetTempPath(), "DevBridge2-trace-secret-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CoordinatorTrace trace = new(root, maxFileBytes: 4096, maxRetainedFiles: 2);
            string secret = "rimbridge-secret-value-9f1c";
            trace.Record(new CoordinatorTraceEvent
            {
                Event = "test.safe_projection",
                RequestId = "trace-test",
                Detail = "{\"token\":\"" + secret + "\",\"authorization\":\"Bearer " +
                    secret + "\",\"safe\":\"" + new string('x', 5000) + "\"}"
            });

            string serialized = File.ReadAllText(trace.FilePath);
            Assert(!serialized.Contains(secret, StringComparison.Ordinal),
                "diagnostic trace contained a RimBridge token-shaped secret");
            TraceView record = JsonSerializer.Deserialize<TraceView>(serialized,
                Program.JsonOptions);
            Assert(record.Detail == null || record.Detail.Length <= 512,
                "diagnostic detail exceeded its bounded length");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void TestCoordinatorTraceRotation()
    {
        string root = Path.Combine(Path.GetTempPath(), "DevBridge2-trace-rotation-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CoordinatorTrace trace = new(root, maxFileBytes: 512, maxRetainedFiles: 2);
            for (int index = 0; index < 40; index++)
            {
                trace.Record(new CoordinatorTraceEvent
                {
                    Event = "test.rotation",
                    RequestId = "request-" + index,
                    Detail = new string('x', 180)
                });
            }

            string[] files = Directory.GetFiles(root, CoordinatorTrace.FileName + "*");
            Assert(files.Length <= 3, "trace rotation exceeded the current plus two retained files");
            Assert(!File.Exists(trace.FilePath + ".3"), "trace rotation retained an unexpected oldest file");
            Assert(files.All(value => new FileInfo(value).Length <= 512),
                "a rotated diagnostic file exceeded its configured byte bound");
            Assert(!trace.DisabledForTesting, "normal diagnostic rotation disabled tracing");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void TestCoordinatorTraceWriteFailureIsNonFatal()
    {
        string parent = Path.Combine(Path.GetTempPath(), "DevBridge2-trace-failure-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        string blockingPath = Path.Combine(parent, "runtime-file");
        File.WriteAllText(blockingPath, "preserve-me");
        try
        {
            CoordinatorTrace trace = new(blockingPath);
            trace.Record(new CoordinatorTraceEvent { Event = "test.write_failure" });
            Assert(trace.DisabledForTesting, "diagnostic write failure did not disable the sink");
            Assert(File.ReadAllText(blockingPath) == "preserve-me",
                "diagnostic failure changed the blocking path through an unsafe fallback");
            Assert(!File.Exists(blockingPath + "\\" + CoordinatorTrace.FileName),
                "diagnostic failure created an unsafe fallback log");
        }
        finally
        {
            TryDeleteDirectory(parent);
        }
    }

    private static void TestCoordinatorTraceConcurrentRequestCorrelation()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        ConcurrentBag<string> responses = new();

        Task<int>[] requests = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            CoordinatorClient.Run(fixture.Root, new[] { "status", "--json" }, harness.Slot,
                null, responses.Add, TimeSpan.FromSeconds(5)))).ToArray();
        Task.WaitAll(requests);
        Assert(requests.All(value => value.Result == 0), "concurrent status requests did not complete");

        harness.Shutdown();
        List<TraceView> events = ReadTrace(fixture.Root);
        List<string> requestIds = events
            .Where(value => value.Event == "ipc.request.accepted" && value.Command == "status")
            .Select(value => value.RequestId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert(requestIds.Count >= 2, "concurrent requests did not retain distinct request IDs");
        foreach (string requestId in requestIds.Take(2))
            Assert(IndexOf(events, "ipc.terminal_result.write", requestId) >= 0,
                "request " + requestId + " lacked a terminal result trace");
    }

    private sealed class TraceView
    {
        public DateTime TimestampUtc { get; set; }
        public string Event { get; set; }
        public string RequestId { get; set; }
        public string Command { get; set; }
        public string RuntimeSlotId { get; set; }
        public int? Generation { get; set; }
        public string Phase { get; set; }
        public long? DurationMs { get; set; }
        public bool? Success { get; set; }
        public string ErrorCode { get; set; }
        public string Detail { get; set; }
        public string Category { get; set; }
        public int? ProtocolVersion { get; set; }
        public CoordinatorBuildIdentity BuildIdentity { get; set; }
    }

    private static List<TraceView> ReadTrace(string root)
    {
        string path = Path.Combine(root, "Runtime", CoordinatorTrace.FileName);
        if (!File.Exists(path))
            return new List<TraceView>();
        return File.ReadAllLines(path)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => JsonSerializer.Deserialize<TraceView>(value, Program.JsonOptions))
            .ToList();
    }

    private static int IndexOf(IEnumerable<TraceView> events, string eventName,
        string requestId = null, Func<TraceView, bool> predicate = null)
    {
        int index = 0;
        foreach (TraceView value in events)
        {
            if (string.Equals(value.Event, eventName, StringComparison.Ordinal) &&
                (requestId == null || string.Equals(value.RequestId, requestId, StringComparison.Ordinal)) &&
                (predicate == null || predicate(value)))
                return index;
            index++;
        }
        return -1;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}

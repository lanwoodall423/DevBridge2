using System.Text.Json;

namespace DevBridge.Coordinator;

internal static class OfflineTests
{
    private static readonly DateTime ClockStart = new(2026, 8, 11, 16, 0, 0, DateTimeKind.Utc);
    private static int failures;

    private static int Main()
    {
        Run("readiness timeout retains process and accepts late same-process readiness", TestReadinessTimeoutContract);
        Run("stop authorization and identity checks", TestStopAuthorization);
        Run("stop requires confirmed exit and fails closed", TestStopFailsClosed);
        Run("stop succeeds, retains lease, marks dirty, and makes no launches", TestSuccessfulStop);
        Run("maintenance state has no background launch and expiry leaves it stopped", TestMaintenanceNoLaunch);
        Run("other test holders queue during maintenance", TestMaintenanceQueue);
        Run("stop serializes ensure-ready and restart", TestStopSerialization);
        Run("ensure-ready launches exactly once after maintenance", TestEnsureReadyLaunch);
        Run("restart retains immediate launch behavior", TestImmediateRestart);
        Run("duplicate stop is idempotent", TestDuplicateStop);

        Console.WriteLine(failures == 0 ? "OFFLINE TESTS PASS" : "OFFLINE TESTS FAIL: " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine("FAIL " + name + ": " + exception.Message);
        }
    }

    private static void TestReadinessTimeoutContract()
    {
        using Fixture fixture = Fixture.LoadingWithLease();
        BridgeRequest wait = Request("wait-ready", "waiter", 88);
        List<string> output = new();
        int exitCode = fixture.State.Execute(wait, output.Add, () => true);
        JsonCommandResponse timedOut = fixture.State.CreateJsonResponse(wait, exitCode, output);

        Assert(exitCode != 0, "the original wait must fail");
        Assert(output.Any(line => line.Contains("READINESS_TIMEOUT", StringComparison.Ordinal)),
            "the original wait must report READINESS_TIMEOUT");
        Assert(timedOut.ErrorCode == "READINESS_TIMEOUT", "JSON state must expose READINESS_TIMEOUT");
        Assert(fixture.Adapter.LaunchCalls == 0, "timeout must make zero replacement launch calls");
        Assert(timedOut.RimWorldPid == 101 && timedOut.RimWorldProcessStartIdentity == 1001,
            "timeout must retain the exact PID/start identity");
        Assert(timedOut.LaunchGeneration == 1, "timeout must retain launch generation");

        fixture.Adapter.Replace(101, 1002);
        fixture.WriteReadiness("launch-1", 1, 101);
        int rejected = fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true);
        Assert(rejected != 0, "different process start identity must be rejected");
        Assert(fixture.Adapter.LaunchCalls == 0, "rejected readiness must not launch");

        fixture.Adapter.Replace(101, 1001);
        int accepted = fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), output.Add, () => true);
        Assert(accepted == 0, "late readiness from the original process must be accepted");
        Assert(fixture.Adapter.LaunchCalls == 0, "late readiness acceptance must not launch");

        int reused = fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), output.Add, () => true);
        Assert(reused == 0, "next ensure-ready must reuse the ready process");
        Assert(fixture.Adapter.LaunchCalls == 0, "next ensure-ready must make zero launch calls");
    }

    private static void TestStopAuthorization()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        int missing = fixture.State.Execute(Request("stop", "holder", 77, "MISSING"), _ => { }, () => true);
        Assert(missing != 0 && fixture.Adapter.TerminationRequests == 0, "missing token must not stop");

        int nonHolder = fixture.State.Execute(Request("stop", "other", 78, "T001"), _ => { }, () => true);
        Assert(nonHolder != 0 && fixture.Adapter.TerminationRequests == 0, "non-holder must not stop");

        fixture.State = fixture.ReloadWithLease(ClockStart.AddHours(-2));
        int expired = fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        Assert(expired != 0 && fixture.Adapter.TerminationRequests == 0, "expired token must not stop");
    }

    private static void TestStopFailsClosed()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        fixture.Adapter.Current.WaitExits = false;
        int exitCode = fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "holder", 77), exitCode, Array.Empty<string>());
        Assert(exitCode != 0 && response.ErrorCode == "STOP_FAILED", "unconfirmed exit must fail structurally");
        Assert(!response.MaintenanceReady, "failed stop must not claim maintenance safety");
        Assert(fixture.Adapter.LaunchCalls == 0, "failed stop must not launch");

        using Fixture ambiguous = Fixture.ReadyWithLease();
        ambiguous.Adapter.ExtraMatchingProcess = true;
        int ambiguousExit = ambiguous.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        JsonCommandResponse ambiguousResponse = ambiguous.State.CreateJsonResponse(Request("status", "holder", 77), ambiguousExit, Array.Empty<string>());
        Assert(ambiguousExit != 0 && !ambiguousResponse.MaintenanceReady,
            "ambiguous post-stop enumeration must fail closed");

        using Fixture pidZero = Fixture.ReadyWithLease();
        pidZero.State = pidZero.ReloadWithLease(ClockStart);
        pidZero.WriteState(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.STOPPED,
            MaintenanceReady = false,
            ProcessId = 0,
            ProcessStartUtcTicks = 0,
            Leases = new List<TestLease> { pidZero.Lease(ClockStart) }
        });
        pidZero.State = pidZero.Reload();
        int pidZeroExit = pidZero.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        JsonCommandResponse pidZeroResponse = pidZero.State.CreateJsonResponse(Request("status", "holder", 77), pidZeroExit, Array.Empty<string>());
        Assert(pidZeroExit != 0 && !pidZeroResponse.MaintenanceReady,
            "PID zero alone must never establish maintenanceReady");
    }

    private static void TestSuccessfulStop()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        int exitCode = fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "holder", 77), exitCode, Array.Empty<string>());
        Assert(exitCode == 0 && response.State == "STOPPED", "stop must return stopped");
        Assert(response.MaintenanceReady && response.LeaseState == "HELD", "stop must retain lease and safety state");
        Assert(response.SessionDirty, "stop must mark the session dirty");
        Assert(fixture.Adapter.LaunchCalls == 0, "stop must make zero launch calls");
        Assert(fixture.Adapter.TerminationRequests == 1 && fixture.Adapter.Current.HasExited,
            "stop must request and confirm exact process exit");
    }

    private static void TestMaintenanceNoLaunch()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "fixture stop must succeed");
        int launchCount = fixture.Adapter.LaunchCalls;
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        fixture.State.Execute(Request("status", "other", 88), _ => { }, () => true);
        fixture.State.Execute(Request("wait-ready", "other", 88), _ => { }, () => true);
        Assert(fixture.Adapter.LaunchCalls == launchCount, "maintenance state must not background-launch");
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "other", 88), 0, Array.Empty<string>());
        Assert(response.State == "STOPPED" && response.MaintenanceReady && response.SessionDirty && response.ActiveTests == 0,
            "lease expiry must leave the game stopped and dirty without launching");
    }

    private static void TestStopSerialization()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        fixture.Adapter.BlockWaitForExit = true;
        Task<int> stop = Task.Run(() => fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true));
        Assert(fixture.Adapter.TerminationRequested.Wait(TimeSpan.FromSeconds(2)), "stop did not reach termination");

        Task<int> restart = Task.Run(() => fixture.State.Execute(Request("restart"), _ => { }, () => true));
        Thread.Sleep(100);
        Task<int> ensure = Task.Run(() => fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true));
        Assert(!ensure.IsCompleted && !restart.IsCompleted && fixture.Adapter.LaunchCalls == 0,
            "ensure-ready/restart must not launch during stop");

        fixture.Adapter.ReleaseWait.Set();
        Assert(stop.Wait(TimeSpan.FromSeconds(2)), "stop did not complete");
        Assert(ensure.Wait(TimeSpan.FromSeconds(2)), "ensure-ready did not complete after stop");
        Assert(restart.Wait(TimeSpan.FromSeconds(2)), "restart did not complete after stop");
        Assert(fixture.Adapter.LaunchCalls == 1, "only explicit ensure-ready may launch after maintenance");
    }

    private static void TestMaintenanceQueue()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "fixture stop must succeed");

        Task<int> queued = Task.Run(() => fixture.State.Execute(
            Request("test", "other", 88, "begin"), _ => { }, () => true));
        Thread.Sleep(100);
        Assert(!queued.IsCompleted && fixture.Adapter.LaunchCalls == 0,
            "other test holders must wait without launching during maintenance");

        fixture.Adapter.ReadyOnLaunch = true;
        Assert(fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "lease holder must be able to release maintenance");
        Assert(queued.Wait(TimeSpan.FromSeconds(2)) && queued.Result == 0,
            "queued test holder must acquire after ensure-ready");
    }

    private static void TestEnsureReadyLaunch()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        fixture.Adapter.ReadyOnLaunch = true;
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "fixture stop must succeed");
        int exitCode = fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "holder", 77), exitCode, Array.Empty<string>());
        Assert(exitCode == 0 && response.State == "READY", "ensure-ready must reach ready");
        Assert(fixture.Adapter.LaunchCalls == 1, "ensure-ready must perform exactly one launch");
        Assert(fixture.Adapter.LastLaunchArguments.Count == 0 &&
            fixture.Adapter.LastLaunchEnvironment.TryGetValue("DEVBRIDGE_QUICKTEST_REQUESTED", out string requested) &&
            requested == "1", "launch must use normal startup with built-in quicktest activation");
        Assert(response.ActiveTests == 1 && response.LeaseState == "HELD", "maintenance lease must survive ensure-ready");
    }

    private static void TestImmediateRestart()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = fixture.State.Execute(Request("restart"), _ => { }, () => true);
        Assert(exitCode == 0, "existing restart must still complete");
        Assert(fixture.Adapter.LaunchCalls == 1, "restart must make one launch call");
        Assert(fixture.Adapter.LastLaunchArguments.Count == 0, "restart must not use command-line quicktest");
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status"), exitCode, Array.Empty<string>());
        Assert(response.State == "READY" && response.Generation == 2, "restart must produce the next ready generation");
    }

    private static void TestDuplicateStop()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "first stop must succeed");
        int terminations = fixture.Adapter.TerminationRequests;
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "duplicate stop must be idempotent");
        Assert(fixture.Adapter.TerminationRequests == terminations && fixture.Adapter.LaunchCalls == 0,
            "duplicate stop must not terminate or launch again");
    }

    private static BridgeRequest Request(string command, string agent = "agent", int pid = 1, params string[] arguments)
    {
        return new BridgeRequest
        {
            Command = command,
            Agent = agent,
            ClientProcessId = pid,
            Arguments = arguments?.ToList() ?? new List<string>()
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        internal readonly string RimWorldPath;
        internal readonly FakeClock Clock;
        internal readonly FakeProcessAdapter Adapter;
        internal CoordinatorState State;

        private Fixture(PersistedState initial)
        {
            Root = Path.Combine(Path.GetTempPath(), "DevBridge2-offline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "Runtime"));
            RimWorldPath = Path.Combine(Root, "RimWorldWin64.exe");
            File.WriteAllText(RimWorldPath, "offline-test-executable");
            File.WriteAllText(Path.Combine(Root, "ModsConfig.xml"), "<activeMods><li>lan.devbridge2</li></activeMods>");
            Clock = new FakeClock(ClockStart);
            Adapter = new FakeProcessAdapter(RimWorldPath, Root, Clock);
            WriteState(initial);
            State = Reload();
        }

        internal TestLease Lease(DateTime started) => new()
        {
            Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 1, StartedUtc = started
        };

        internal static Fixture LoadingWithLease()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 0,
                Phase = BridgePhase.LOADING,
                LaunchId = "launch-1",
                LaunchGeneration = 1,
                TargetGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart,
                Leases = new List<TestLease>
                {
                    new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 0, StartedUtc = ClockStart }
                }
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal static Fixture ReadyWithLease()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.READY,
                LaunchId = "launch-ready",
                LaunchGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart,
                Leases = new List<TestLease> { new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 1, StartedUtc = ClockStart } }
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal static Fixture ReadyWithoutLease()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.READY,
                LaunchId = "launch-ready",
                LaunchGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal CoordinatorState ReloadWithLease(DateTime started)
        {
            WriteState(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.READY,
                LaunchId = "launch-ready",
                LaunchGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart,
                Leases = new List<TestLease> { Lease(started) }
            });
            Adapter.Replace(101, 1001);
            return Reload();
        }

        internal void WriteReadiness(string launchId, int generation, int processId)
        {
            File.WriteAllText(Path.Combine(Root, "Runtime", "readiness.json"), JsonSerializer.Serialize(new ReadinessRecord
            {
                LaunchId = launchId,
                Generation = generation,
                ProcessId = processId,
                TimestampUtc = Clock.UtcNow
            }, Program.JsonOptions));
        }

        internal void WriteState(PersistedState value)
        {
            File.WriteAllText(Path.Combine(Root, "Runtime", "state.json"), JsonSerializer.Serialize(value, Program.JsonOptions));
        }

        internal CoordinatorState Reload()
        {
            return new CoordinatorState(Root, new CoordinatorOptions
            {
                ReadinessTimeout = TimeSpan.FromSeconds(3),
                ProcessExitTimeout = TimeSpan.FromSeconds(1),
                ProcessAdapter = Adapter,
                Clock = Clock,
                RimWorldExecutablePath = RimWorldPath,
                ModsConfigPath = Path.Combine(Root, "ModsConfig.xml")
            });
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch { }
        }
    }

    private sealed class FakeClock : ICoordinatorClock
    {
        private DateTime now;
        internal FakeClock(DateTime start) => now = start;
        public DateTime UtcNow => now;
        public void Sleep(TimeSpan duration) => now = now.Add(duration);
        internal void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private sealed class FakeProcessAdapter : IProcessAdapter
    {
        private readonly string executablePath;
        private readonly string root;
        private readonly FakeClock clock;
        private readonly Dictionary<int, FakeProcess> processes = new();
        private int nextPid = 200;
        private long nextStart = 2000;

        internal int LaunchCalls { get; private set; }
        internal IReadOnlyList<string> LastLaunchArguments { get; private set; } = Array.Empty<string>();
        internal IReadOnlyDictionary<string, string> LastLaunchEnvironment { get; private set; } =
            new Dictionary<string, string>();
        internal int TerminationRequests => processes.Values.Sum(value => value.TerminationRequests);
        internal FakeProcess Current => processes.Values.OrderByDescending(value => value.Id).First();
        internal bool ReadyOnLaunch { get; set; }
        internal bool ExtraMatchingProcess { get; set; }
        internal bool BlockWaitForExit
        {
            get => Current.BlockWait;
            set
            {
                Current.BlockWait = value;
                Current.WaitSignal = ReleaseWait;
            }
        }
        internal ManualResetEventSlim TerminationRequested { get; } = new(false);
        internal ManualResetEventSlim ReleaseWait { get; } = new(false);

        internal FakeProcessAdapter(string executablePath, string root, FakeClock clock)
        {
            this.executablePath = executablePath;
            this.root = root;
            this.clock = clock;
        }

        internal void Add(FakeProcess process)
        {
            process.TerminationSignal = TerminationRequested;
            processes[process.Id] = process;
        }

        internal void Replace(int id, long startIdentity)
        {
            Add(new FakeProcess(id, startIdentity, executablePath));
        }

        public IManagedProcess Open(int processId)
        {
            processes.TryGetValue(processId, out FakeProcess process);
            return process;
        }

        public ProcessEnumeration EnumerateRimWorld(string configuredPath)
        {
            if (ExtraMatchingProcess)
                Add(new FakeProcess(999, 9999, executablePath));
            return new ProcessEnumeration
            {
                Complete = true,
                Processes = processes.Values.Where(value => !value.HasExited &&
                    string.Equals(value.ExecutablePath, configuredPath, StringComparison.OrdinalIgnoreCase)).Cast<IManagedProcess>().ToList()
            };
        }

        public IManagedProcess Launch(ProcessLaunchRequest request)
        {
            LaunchCalls++;
            LastLaunchArguments = request.Arguments?.ToArray() ?? Array.Empty<string>();
            LastLaunchEnvironment = new Dictionary<string, string>(request.Environment ??
                new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            FakeProcess process = new(nextPid++, nextStart++, executablePath)
            {
                WaitExits = true
            };
            process.TerminationSignal = TerminationRequested;
            processes[process.Id] = process;
            if (ReadyOnLaunch)
            {
                File.WriteAllText(Path.Combine(root, "Runtime", "readiness.json"), JsonSerializer.Serialize(new ReadinessRecord
                {
                    LaunchId = request.Environment["DEVBRIDGE_LAUNCH_ID"],
                    Generation = int.Parse(request.Environment["DEVBRIDGE_GENERATION"]),
                    ProcessId = process.Id,
                    TimestampUtc = clock.UtcNow
                }, Program.JsonOptions));
            }
            return process;
        }
    }

    private sealed class FakeProcess : IManagedProcess
    {
        internal ManualResetEventSlim WaitSignal { get; set; } = new(true);
        internal int TerminationRequests { get; private set; }
        internal bool WaitExits { get; set; } = true;
        internal bool BlockWait { get; set; }
        internal ManualResetEventSlim TerminationSignal { get; set; }
        private bool exited;

        internal FakeProcess(int id, long startIdentity, string executablePath)
        {
            Id = id;
            StartIdentity = startIdentity;
            ExecutablePath = executablePath;
        }

        public int Id { get; }
        public long StartIdentity { get; }
        public string ExecutablePath { get; }
        public bool HasExited => exited;

        public bool RequestTermination()
        {
            TerminationRequests++;
            TerminationSignal?.Set();
            return true;
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            if (BlockWait)
                WaitSignal.Wait(timeout);
            if (WaitExits)
                exited = true;
            return exited;
        }

        public bool ForceTerminate()
        {
            exited = true;
            return true;
        }

        public void Dispose() { }
    }
}

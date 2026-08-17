using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBridge.FakeRimWorld;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private static async Task<int> Main()
    {
        string root = Environment.GetEnvironmentVariable("DEVBRIDGE_ROOT") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
            return 2;

        string runtime = Path.Combine(root, "Runtime");
        Directory.CreateDirectory(runtime);
        FakeScenario scenario = FakeScenario.Load(runtime);
        string launchId = Environment.GetEnvironmentVariable("DEVBRIDGE_LAUNCH_ID") ?? "fake-launch";
        int generation = ParseInt("DEVBRIDGE_GENERATION", 1);
        bool quicktest = IsTrue(Environment.GetEnvironmentVariable("DEVBRIDGE_QUICKTEST_REQUESTED"));
        Process process = Process.GetCurrentProcess();
        int processId = process.Id;
        long processStartIdentity = process.StartTime.ToUniversalTime().Ticks;
        string logPath = scenario.PlayerLogPath ?? Path.Combine(root, "Player.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? root);

        using CancellationTokenSource stop = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            if (!scenario.IgnoreStop)
            {
                eventArgs.Cancel = true;
                stop.Cancel();
            }
            else
                eventArgs.Cancel = true;
        };

        using FakeGabpServer? bridge = await FakeGabpServer.StartAsync(
            scenario, launchId, generation, processId, processStartIdentity, stop.Token);
        if (bridge != null)
        {
            Append(logPath, $"[RimBridge] GABP server running standalone on port {bridge.Port}");
            Append(logPath, $"[RimBridge] Bridge token: {bridge.Token}");
        }

        Append(logPath, $"[FakeRimWorld] scenario={scenario.Name} launch={launchId} generation={generation}");
        AppendRepeatedDiagnostics(logPath, scenario);

        if (scenario.Name.Equals("crash-before-ready", StringComparison.OrdinalIgnoreCase))
            return 31;

        if (quicktest && scenario.Name.Equals("quicktest-failure", StringComparison.OrdinalIgnoreCase))
        {
            WriteQuicktestFailure(runtime, launchId, generation, processId, processStartIdentity,
                scenario, logPath);
            await Task.Delay(scenario.CrashDelayMs, CancellationToken.None);
            return 32;
        }

        string stopSignalPath = Environment.GetEnvironmentVariable("DEVBRIDGE_TEST_GRACEFUL_STOP_SIGNAL") ?? string.Empty;
        if (scenario.Name.Equals("never-ready", StringComparison.OrdinalIgnoreCase))
            return await WaitForStop(stop.Token, scenario.IgnoreStop, stopSignalPath);

        int readyDelay = Math.Max(0, scenario.ReadyAfterMs);
        if (readyDelay > 0)
            await Task.Delay(readyDelay, stop.Token);

        await WaitForReadyGate(scenario, stop.Token);

        WriteReadiness(runtime, launchId, generation, processId, processStartIdentity, scenario);
        Append(logPath, "[FakeRimWorld] readiness accepted");

        if (scenario.Name.Equals("player-log-rotation", StringComparison.OrdinalIgnoreCase))
            _ = RotateLogAfterDelay(logPath, scenario.LogRotationDelayMs, stop.Token);

        if (scenario.Name.Equals("crash-after-ready", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(scenario.CrashDelayMs, stop.Token);
            return 33;
        }

        return await WaitForStop(stop.Token, scenario.IgnoreStop, stopSignalPath);
    }

    private static async Task<int> WaitForStop(CancellationToken token, bool ignoreStop, string stopSignalPath)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!ignoreStop && !string.IsNullOrWhiteSpace(stopSignalPath) && File.Exists(stopSignalPath))
                {
                    try { File.Delete(stopSignalPath); } catch { }
                    return 0;
                }
                await Task.Delay(50, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }

    private static async Task WaitForReadyGate(FakeScenario scenario, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(scenario.ReadyGatePath))
            return;

        string waitingPath = string.IsNullOrWhiteSpace(scenario.ReadyWaitingPath)
            ? scenario.ReadyGatePath + ".waiting"
            : scenario.ReadyWaitingPath;
        File.WriteAllText(waitingPath, "ready-waiting", Encoding.UTF8);
        while (!File.Exists(scenario.ReadyGatePath))
            await Task.Delay(25, token);
    }

    private static async Task RotateLogAfterDelay(string path, int delayMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(Math.Max(1, delayMs), token);
            File.WriteAllText(path, "[FakeRimWorld] Player.log rotated\r\n", Encoding.UTF8);
            Append(path, "[Error] [FakeRimWorld] repeated fake error after rotation");
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static void WriteReadiness(string runtime, string launchId, int generation, int processId,
        long processStartIdentity, FakeScenario scenario)
    {
        string effectiveLaunch = scenario.Name.Equals("stale-launch-readiness", StringComparison.OrdinalIgnoreCase)
            ? "stale-" + launchId : launchId;
        int effectiveGeneration = scenario.Name.Equals("stale-generation-readiness", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, generation - 1) : generation;
        int effectivePid = scenario.Name.Equals("pid-identity-mismatch", StringComparison.OrdinalIgnoreCase)
            ? processId + 10000 : processId;

        string path = Path.Combine(runtime, "readiness.json");
        if (scenario.Name.Equals("malformed-readiness", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, "{\"schemaVersion\":1,\"launchId\":", Encoding.UTF8);
            return;
        }

        var record = new
        {
            schemaVersion = 1,
            launchId = effectiveLaunch,
            generation = effectiveGeneration,
            processId = effectivePid,
            timestampUtc = DateTime.UtcNow,
            processStartUtcTicks = processStartIdentity
        };
        AtomicWrite(path, JsonSerializer.Serialize(record, JsonOptions));
    }

    private static void WriteQuicktestFailure(string runtime, string launchId, int generation, int processId,
        long processStartIdentity, FakeScenario scenario, string logPath)
    {
        var record = new
        {
            schemaVersion = 1,
            launchId,
            generation,
            processId,
            processStartUtcTicks = processStartIdentity,
            profileFingerprint = Environment.GetEnvironmentVariable("DEVBRIDGE_PROFILE_FINGERPRINT") ?? string.Empty,
            baselineFingerprint = Environment.GetEnvironmentVariable("DEVBRIDGE_BASELINE_FINGERPRINT") ?? string.Empty,
            profileMode = Environment.GetEnvironmentVariable("DEVBRIDGE_PROFILE_MODE") ?? string.Empty,
            timestampUtc = DateTime.UtcNow,
            failurePhase = "QUICKTEST",
            failureCode = "QUICKTEST_GENERATION_FAILED",
            exceptionType = "FakeRimWorld.QuicktestException",
            exceptionMessage = "The deterministic fake quicktest failed.",
            diagnosticDetail = "fake repeated failure for integration evidence"
        };
        AtomicWrite(Path.Combine(runtime, "quicktest-failure.json"),
            JsonSerializer.Serialize(record, JsonOptions));
        Append(logPath, "[Error] [FakeRimWorld] QUICKTEST_GENERATION_FAILED");
    }

    private static void AppendRepeatedDiagnostics(string path, FakeScenario scenario)
    {
        if (!scenario.RepeatLogErrors)
            return;
        for (int index = 0; index < 3; index++)
        {
            Append(path, "[Error] [FakeRimWorld] repeated deterministic error");
            Append(path, "   at FakeRimWorld.IntegrationScenario.Run() in FakeRimWorld.cs:line 42");
        }
    }

    private static void Append(string path, string line)
    {
        try
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void AtomicWrite(string path, string text)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static int ParseInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    private static bool IsTrue(string? value) =>
        value is "1" or "true" or "TRUE" or "True";
}

internal sealed class FakeScenario
{
    public int SchemaVersion { get; set; } = 1;
    public string Contract { get; set; } = "devbridge-fake-rimworld/v1";
    public string Name { get; set; } = "ready-immediately";
    public int ReadyAfterMs { get; set; }
    public int CrashDelayMs { get; set; } = 150;
    public int LogRotationDelayMs { get; set; } = 100;
    public bool IgnoreStop { get; set; }
    public bool RepeatLogErrors { get; set; }
    public bool RimBridgeAvailable { get; set; } = true;
    public bool AuthFailure { get; set; }
    public bool CompanionAvailable { get; set; } = true;
    public bool CompanionGenerationMismatch { get; set; }
    public int ResponseDelayMs { get; set; }
    public string? PlayerLogPath { get; set; }
    public string? ReadyGatePath { get; set; }
    public string? ReadyWaitingPath { get; set; }

    public static FakeScenario Load(string runtime)
    {
        string path = Path.Combine(runtime, "fake-rimworld-scenario.json");
        FakeScenario value = new();
        try
        {
            if (File.Exists(path))
                value = JsonSerializer.Deserialize<FakeScenario>(File.ReadAllText(path), ProgramJson.Options) ?? value;
        }
        catch
        {
        }
        value.ApplyNameDefaults();
        return value;
    }

    private void ApplyNameDefaults()
    {
        switch (Name.ToLowerInvariant())
        {
            case "ready-delayed": ReadyAfterMs = ReadyAfterMs == 0 ? 100 : ReadyAfterMs; break;
            case "never-ready": break;
            case "crash-before-ready": break;
            case "crash-after-ready": break;
            case "quicktest-failure": RepeatLogErrors = true; break;
            case "repeat-log-errors": RepeatLogErrors = true; break;
            case "player-log-rotation": RepeatLogErrors = true; break;
            case "rimbridge-unavailable": RimBridgeAvailable = false; break;
            case "rimbridge-auth-failure": AuthFailure = true; break;
            case "rimbridge-companion-unavailable": CompanionAvailable = false; break;
            case "rimbridge-companion-generation-mismatch": CompanionGenerationMismatch = true; break;
            case "graceful-stop": IgnoreStop = false; break;
            case "hung-stop": IgnoreStop = true; break;
        }
    }
}

internal static class ProgramJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}

internal sealed class FakeGabpServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly FakeScenario scenario;
    private readonly string launchId;
    private readonly int generation;
    private readonly int processId;
    private readonly long processStartIdentity;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task acceptLoop;
    private int fixtureMutationCount;
    private string fixtureValue = "initial";
    private bool disposed;

    private FakeGabpServer(TcpListener listener, FakeScenario scenario, string launchId, int generation,
        int processId, long processStartIdentity)
    {
        this.listener = listener;
        this.scenario = scenario;
        this.launchId = launchId;
        this.generation = generation;
        this.processId = processId;
        this.processStartIdentity = processStartIdentity;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Token = "fake-token";
        acceptLoop = AcceptLoop();
    }

    internal int Port { get; }
    internal string Token { get; }

    internal static async Task<FakeGabpServer?> StartAsync(FakeScenario scenario, string launchId,
        int generation, int processId, long processStartIdentity, CancellationToken stop)
    {
        if (!scenario.RimBridgeAvailable)
            return null;
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        FakeGabpServer server = new(listener, scenario, launchId, generation, processId, processStartIdentity);
        stop.Register(server.Dispose);
        await Task.Yield();
        return server;
    }

    private async Task AcceptLoop()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(lifetime.Token);
                _ = HandleClient(client);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                client?.Dispose();
                if (!lifetime.IsCancellationRequested)
                    await Task.Delay(10);
            }
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            while (!lifetime.IsCancellationRequested && client.Connected)
            {
                JsonDocument? request = await ReadFrame(stream);
                if (request == null)
                    return;
                using (request)
                {
                    JsonElement root = request.RootElement;
                    string method = root.TryGetProperty("method", out JsonElement methodValue)
                        ? methodValue.GetString() ?? string.Empty : string.Empty;
                    string id = root.TryGetProperty("id", out JsonElement idValue)
                        ? idValue.GetString() ?? string.Empty : string.Empty;
                    if (scenario.ResponseDelayMs > 0)
                        await Task.Delay(scenario.ResponseDelayMs);
                    object response = CreateResponse(id, method, root);
                    await WriteFrame(stream, response);
                }
            }
        }
    }

    private object CreateResponse(string id, string method, JsonElement request)
    {
        if (scenario.AuthFailure && method == "session/hello")
            return Error(id, -32001, "authentication failed");
        if (method == "session/hello")
            return Result(id, new { sessionId = "fake-session", success = true });
        if (method == "tools/list")
        {
        object[] tools = scenario.CompanionAvailable
                ? new object[]
                {
                    new { name = "devbridge/get_generation_context" },
                    new { name = "rimworld/fixture_mutate" },
                    new { name = "rimworld/inspect_fixture" }
                }
                : Array.Empty<object>();
            return Result(id, new { tools });
        }
        if (method == "tools/call")
        {
            string name = string.Empty;
            if (request.TryGetProperty("params", out JsonElement parameters) &&
                parameters.TryGetProperty("name", out JsonElement nameValue))
                name = nameValue.GetString() ?? string.Empty;
            if (!scenario.CompanionAvailable)
                return Error(id, -32601, "tool not found");
            if (string.Equals(name, "rimworld/fixture_mutate", StringComparison.Ordinal))
            {
                string value = "mutated";
                if (request.TryGetProperty("params", out JsonElement callParameters) &&
                    callParameters.TryGetProperty("arguments", out JsonElement arguments) &&
                    arguments.ValueKind == JsonValueKind.Object &&
                    arguments.TryGetProperty("value", out JsonElement requestedValue))
                    value = requestedValue.GetString() ?? string.Empty;
                if (value.Length > 128)
                    return Error(id, -32602, "fixture value is bounded");
                lock (this)
                {
                    fixtureValue = value;
                    fixtureMutationCount++;
                    return Result(id, new
                    {
                        success = true,
                        action = "fixture-mutated",
                        value = fixtureValue,
                        mutationCount = fixtureMutationCount
                    });
                }
            }
            if (string.Equals(name, "rimworld/inspect_fixture", StringComparison.Ordinal))
            {
                lock (this)
                {
                    return Result(id, new
                    {
                        success = true,
                        action = "fixture-observed",
                        value = fixtureValue,
                        mutationCount = fixtureMutationCount
                    });
                }
            }
            if (!string.Equals(name, "devbridge/get_generation_context", StringComparison.Ordinal))
                return Error(id, -32601, "tool not found");
            int reportedGeneration = scenario.CompanionGenerationMismatch ? generation + 1 : generation;
            return Result(id, new
            {
                success = true,
                available = true,
                schemaVersion = "devbridge-generation-context/v1",
                launchId,
                generation = reportedGeneration,
                processId,
                processStartUtcTicks = processStartIdentity
            });
        }
        return Error(id, -32601, "method not found");
    }

    private static object Result(string id, object result) => new
    {
        v = "gabp/1",
        type = "response",
        id,
        result
    };

    private static object Error(string id, int code, string message) => new
    {
        v = "gabp/1",
        type = "response",
        id,
        error = new { code, message }
    };

    private static async Task<JsonDocument?> ReadFrame(NetworkStream stream)
    {
        List<byte> header = new();
        byte[] singleByte = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(singleByte.AsMemory(0, 1));
            if (read <= 0)
                return null;
            int value = singleByte[0];
            header.Add((byte)value);
            if (header.Count > 8192)
                return null;
            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' &&
                header[^2] == '\r' && header[^1] == '\n')
                break;
        }

        int length = 0;
        foreach (string line in Encoding.ASCII.GetString(header.ToArray())
                     .Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line.Substring("Content-Length:".Length).Trim(), out length);
        }
        if (length <= 0 || length > 256 * 1024)
            return null;
        byte[] body = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(body.AsMemory(offset, length - offset));
            if (read <= 0)
                return null;
            offset += read;
        }
        try { return JsonDocument.Parse(body); }
        catch { return null; }
    }

    private static async Task WriteFrame(NetworkStream stream, object value)
    {
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, ProgramJson.Options));
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\nContent-Type: application/json\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        try { listener.Stop(); } catch { }
        lifetime.Dispose();
    }
}

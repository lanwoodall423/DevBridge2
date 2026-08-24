using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DevBridge2
{
    public sealed class DevBridge2Mod : Mod
    {
        public DevBridge2Mod(ModContentPack content) : base(content)
        {
            DevBridgeReadiness.Configure();
            DevBridgeQuicktestActivation.Configure();
            if (!DevBridgeReadiness.IsConfigured)
                Log.Warning("[DevBridge2] DEVBRIDGE_ROOT or DEVBRIDGE_LAUNCH_ID is missing; readiness reporting is disabled.");
        }
    }

    public sealed class DevBridge2GameComponent : GameComponent
    {
        private bool reported;

        public DevBridge2GameComponent(Game game)
        {
        }

        public override void GameComponentTick()
        {
            if (reported || !DevBridgeReadiness.IsPlayableMap())
                return;

            reported = DevBridgeReadiness.TryWriteReadiness();
        }
    }

    internal static class DevBridgeQuicktestActivation
    {
        private const int DefaultActivationTimeoutSeconds = 60;
        private const int MinActivationTimeoutSeconds = 5;
        private const int MaxActivationTimeoutSeconds = 120;
        private static readonly object FailureGate = new object();
        private static QuicktestActivationController controller;
        private static bool failureArtifactAttempted;
        private static string configuredRoot;
        private static string configuredLaunchId;
        private static int configuredGeneration;
        private static string configuredProfileFingerprint;
        private static string configuredBaselineFingerprint;
        private static string configuredProfileMode;

        internal static void Configure()
        {
            configuredRoot = Environment.GetEnvironmentVariable("DEVBRIDGE_ROOT");
            configuredLaunchId = Environment.GetEnvironmentVariable("DEVBRIDGE_LAUNCH_ID");
            int.TryParse(Environment.GetEnvironmentVariable("DEVBRIDGE_GENERATION"), out configuredGeneration);
            configuredProfileFingerprint = Environment.GetEnvironmentVariable("DEVBRIDGE_PROFILE_FINGERPRINT");
            configuredBaselineFingerprint = Environment.GetEnvironmentVariable("DEVBRIDGE_BASELINE_FINGERPRINT");
            configuredProfileMode = Environment.GetEnvironmentVariable("DEVBRIDGE_PROFILE_MODE");
            int activationTimeoutSeconds = DefaultActivationTimeoutSeconds;
            string timeoutValue = Environment.GetEnvironmentVariable("DEVBRIDGE_QUICKTEST_TIMEOUT_SECONDS");
            if (int.TryParse(timeoutValue, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int parsedTimeout) && parsedTimeout >= MinActivationTimeoutSeconds &&
                parsedTimeout <= MaxActivationTimeoutSeconds)
                activationTimeoutSeconds = parsedTimeout;

            bool requested = string.Equals(Environment.GetEnvironmentVariable("DEVBRIDGE_QUICKTEST_REQUESTED"), "1",
                StringComparison.Ordinal);
            if (requested)
            {
                controller = new QuicktestActivationController(requested,
                    DevBridgeQuicktestMenuAdapter.IsGenuineMainMenuReady,
                    () => DevBridgeQuicktestMenuAdapter.QueueBuiltInDevQuicktest(
                        ReportActivationFailure), MonotonicMilliseconds,
                    activationTimeoutSeconds * 1000L);
                LongEventHandler.ExecuteWhenFinished(DevBridgeQuicktestActivationDriver.EnsureCreated);
            }
        }

        private static void ReportActivationFailure(Exception exception, string phase)
        {
            bool writeArtifact;
            lock (FailureGate)
            {
                writeArtifact = !failureArtifactAttempted;
                failureArtifactAttempted = true;
            }

            if (writeArtifact)
            {
                try
                {
                    int processId = 0;
                    long processStartUtcTicks = 0;
                    try
                    {
                        using (Process process = Process.GetCurrentProcess())
                        {
                            processId = process.Id;
                            processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                        }
                    }
                    catch
                    {
                        // The callback still reports its in-memory failure and
                        // rethrows if process identity cannot be inspected.
                    }

                    QuicktestFailureArtifact.TryWrite(configuredRoot, new QuicktestFailureRecord
                    {
                        SchemaVersion = QuicktestFailureArtifact.CurrentSchemaVersion,
                        LaunchId = configuredLaunchId,
                        Generation = configuredGeneration,
                        ProcessId = processId,
                        ProcessStartUtcTicks = processStartUtcTicks,
                        ProfileFingerprint = configuredProfileFingerprint,
                        BaselineFingerprint = configuredBaselineFingerprint,
                        ProfileMode = configuredProfileMode,
                        TimestampUtc = DateTime.UtcNow,
                        FailurePhase = phase,
                        FailureCode = QuicktestFailureArtifact.StableFailureCode,
                        ExceptionType = exception?.GetType().FullName,
                        ExceptionMessage = exception?.Message,
                        DiagnosticDetail = exception?.ToString()
                    }, out string writeError);
                    if (!string.IsNullOrWhiteSpace(writeError))
                        Log.Warning("[DevBridge2] Could not persist quicktest failure artifact: " + writeError);
                }
                catch (Exception artifactException)
                {
                    // Artifact persistence is diagnostic. It must never mask
                    // the original exception being rethrown to RimWorld.
                    Log.Warning("[DevBridge2] Could not persist quicktest failure artifact: " +
                        artifactException.Message);
                }
            }

            controller?.ReportActivationFailure(exception);
        }

        internal static bool Tick()
        {
            if (controller == null || !controller.Pending || controller.ActivationRequested || controller.TerminalFailure)
                return false;

            QuicktestActivationResult result = controller.Tick(UnityData.IsInMainThread);
            if (result == QuicktestActivationResult.WaitingForMainMenu)
                return true;

            if (result == QuicktestActivationResult.Requested)
            {
                Log.Message("[DevBridge2] quicktestMainMenu=true; genuine main-menu UI is ready.");
                Log.Message("[DevBridge2] quicktestRequested=true; built-in Dev Quicktest callback queued; no UI button click was performed.");
            }
            else
            {
                Log.Error("[DevBridge2] quicktest activation reached a bounded terminal failure: " +
                    controller.Failure);
            }

            return false;
        }

        private static long MonotonicMilliseconds()
        {
            return (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
        }

    }

    internal sealed class DevBridgeQuicktestActivationDriver : MonoBehaviour
    {
        internal static void EnsureCreated()
        {
            GameObject driverObject = new GameObject("DevBridge2 Quicktest Activation");
            UnityEngine.Object.DontDestroyOnLoad(driverObject);
            driverObject.AddComponent<DevBridgeQuicktestActivationDriver>();
        }

        private void Update()
        {
            if (!DevBridgeQuicktestActivation.Tick())
                UnityEngine.Object.Destroy(gameObject);
        }
    }

    internal static class DevBridgeReadiness
    {
        private static readonly object Gate = new object();
        private static string root;
        private static string configuredInstallationId;
        private static string configuredRuntimeSlotId;
        private static string launchId;
        private static int generation;
        private static bool configured;
        private static bool signaled;

        internal static bool IsConfigured
        {
            get
            {
                lock (Gate)
                    return configured;
            }
        }

        internal static void Configure()
        {
            lock (Gate)
            {
                root = Environment.GetEnvironmentVariable("DEVBRIDGE_ROOT");
                configuredInstallationId = Environment.GetEnvironmentVariable("DEVBRIDGE_INSTALLATION_ID");
                configuredRuntimeSlotId = Environment.GetEnvironmentVariable("DEVBRIDGE_RUNTIME_SLOT_ID");
                launchId = Environment.GetEnvironmentVariable("DEVBRIDGE_LAUNCH_ID");
                int.TryParse(Environment.GetEnvironmentVariable("DEVBRIDGE_GENERATION"), out generation);
                configured = !string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(launchId);
                if (!configured)
                    return;

                try
                {
                    root = Path.GetFullPath(root);
                    Directory.CreateDirectory(Path.Combine(root, "Runtime"));
                }
                catch (Exception exception)
                {
                    configured = false;
                    Log.Warning("[DevBridge2] Could not prepare Runtime: " + exception.Message);
                }
            }
        }

        internal static bool IsPlayableMap()
        {
            lock (Gate)
            {
                if (!configured || signaled)
                    return false;
            }

            try
            {
                return GenScene.InPlayScene && Current.Game != null && Find.CurrentMap != null &&
                    Find.TickManager != null;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryWriteReadiness()
        {
            string configuredRoot;
            string configuredInstallationId;
            string configuredRuntimeSlotId;
            string configuredLaunchId;
            int configuredGeneration;
            lock (Gate)
            {
                if (!configured || signaled)
                    return signaled;
                configuredRoot = root;
                configuredInstallationId = DevBridgeReadiness.configuredInstallationId;
                configuredRuntimeSlotId = DevBridgeReadiness.configuredRuntimeSlotId;
                configuredLaunchId = launchId;
                configuredGeneration = generation;
            }

            string runtime = Path.Combine(configuredRoot, "Runtime");
            string readinessPath = Path.Combine(runtime, "readiness.json");
            string temporaryPath = readinessPath + ".tmp-" + Guid.NewGuid().ToString("N");
            DateTime timestamp = DateTime.UtcNow;
            Process currentProcess = Process.GetCurrentProcess();
            int processId = currentProcess.Id;
            long processStartIdentity = 0;
            try
            {
                processStartIdentity = currentProcess.StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
                // The coordinator independently proves PID/start identity.
            }
            string json = "{\n" +
                "  \"schemaVersion\": " + DevBridgeSchemaVersions.Readiness.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"installationId\": \"" + EscapeJson(configuredInstallationId ?? string.Empty) + "\",\n" +
                "  \"runtimeSlotId\": \"" + EscapeJson(configuredRuntimeSlotId ?? string.Empty) + "\",\n" +
                "  \"launchId\": \"" + EscapeJson(configuredLaunchId) + "\",\n" +
                "  \"generation\": " + configuredGeneration.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"processId\": " + processId.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"processStartUtcTicks\": " + processStartIdentity.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"timestampUtc\": \"" + timestamp.ToString("O", CultureInfo.InvariantCulture) + "\"\n" +
                "}";

            try
            {
                Directory.CreateDirectory(runtime);
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                ReplaceFile(temporaryPath, readinessPath);
                lock (Gate)
                    signaled = true;
                Log.Message("[DevBridge2] quicktestReady=true; quicktest map ready; launch " + configuredLaunchId + ".");
                return true;
            }
            catch (Exception exception)
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // The next tick will retry the readiness signal.
                }

                Log.Warning("[DevBridge2] Could not write readiness: " + exception.Message);
                return false;
            }
        }

        private static void ReplaceFile(string temporaryPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Replace(temporaryPath, destinationPath, null);
                    return;
                }
                catch
                {
                    File.Delete(destinationPath);
                }
            }

            File.Move(temporaryPath, destinationPath);
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}

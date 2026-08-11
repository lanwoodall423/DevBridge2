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
        private static bool requested;
        private static bool attempted;

        internal static void Configure()
        {
            requested = string.Equals(Environment.GetEnvironmentVariable("DEVBRIDGE_QUICKTEST_REQUESTED"), "1",
                StringComparison.Ordinal);
            if (requested)
                LongEventHandler.ExecuteWhenFinished(TryActivate);
        }

        private static void TryActivate()
        {
            if (!requested || attempted)
                return;

            attempted = true;
            if (!GenScene.InEntryScene)
            {
                Log.Warning("[DevBridge2] Built-in Dev Quicktest was requested, but RimWorld was not at the main menu.");
                return;
            }

            try
            {
                PageUtility.InitGameStart();
                Root_Play.SetupForQuickTestPlay();
                Log.Message("[DevBridge2] quicktestRequested=true; built-in Dev Quicktest requested from the main menu.");
            }
            catch (Exception exception)
            {
                Log.Error("[DevBridge2] Built-in Dev Quicktest activation failed: " + exception);
            }
        }
    }

    internal static class DevBridgeReadiness
    {
        private static readonly object Gate = new object();
        private static string root;
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
            string configuredLaunchId;
            int configuredGeneration;
            lock (Gate)
            {
                if (!configured || signaled)
                    return signaled;
                configuredRoot = root;
                configuredLaunchId = launchId;
                configuredGeneration = generation;
            }

            string runtime = Path.Combine(configuredRoot, "Runtime");
            string readinessPath = Path.Combine(runtime, "readiness.json");
            string temporaryPath = readinessPath + ".tmp-" + Guid.NewGuid().ToString("N");
            DateTime timestamp = DateTime.UtcNow;
            int processId = Process.GetCurrentProcess().Id;
            string json = "{\n" +
                "  \"launchId\": \"" + EscapeJson(configuredLaunchId) + "\",\n" +
                "  \"generation\": " + configuredGeneration.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"processId\": " + processId.ToString(CultureInfo.InvariantCulture) + ",\n" +
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

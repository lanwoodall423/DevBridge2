using System.Diagnostics;
using System.Text;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestBridgeToolsPublishContract()
    {
        if (SkipUnselectedBridgeToolsCoverage())
            return;

        string root = FindWorkspaceRoot();
        string companionProject = File.ReadAllText(Path.Combine(root, "Source", "BridgeTools",
            "DevBridge2.BridgeTools.csproj"));
        string companionTools = File.ReadAllText(Path.Combine(root, "Source", "BridgeTools",
            "DevBridgeGenerationTools.cs"));
        string coreProject = File.ReadAllText(Path.Combine(root, "Source", "Mod", "DevBridge2.csproj"));
        string coreSource = string.Join("\n", Directory.EnumerateFiles(
            Path.Combine(root, "Source", "Mod"), "*.cs", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText));
        string publishScript = File.ReadAllText(Path.Combine(root, "Publish-DevBridge.ps1"));

        Assert(companionProject.Contains("RimBridgeServer.Sdk", StringComparison.Ordinal) &&
               companionProject.Contains("PrivateAssets=\"all\"", StringComparison.Ordinal) &&
               companionProject.Contains("ExcludeAssets=\"runtime\"", StringComparison.Ordinal) &&
               !coreProject.Contains("RimBridgeServer.Sdk", StringComparison.OrdinalIgnoreCase) &&
               !coreSource.Contains("RimBridgeServer", StringComparison.OrdinalIgnoreCase),
            "the companion must keep its SDK reference compile-time-only and the core mod must remain SDK-free");
        Assert(companionTools.Contains("public sealed class DevBridgeGenerationTools", StringComparison.Ordinal) &&
               companionTools.Contains("public DevBridgeGenerationContextPayload GetGenerationContext()", StringComparison.Ordinal) &&
               companionTools.Contains("public DevBridgeControlPolicyPayload GetControlPolicy()", StringComparison.Ordinal),
            "the companion tool class must be public, parameterless, and instantiable by RimBridgeServer");

        string releaseOutput = Path.Combine(root, "Source", "BridgeTools", "bin", "Release");
        string companionDll = Path.Combine(releaseOutput, "DevBridge2.BridgeTools.dll");
        Assert(File.Exists(companionDll),
            "the Release companion build must produce DevBridge2.BridgeTools.dll");
        Assert(!Directory.EnumerateFiles(releaseOutput, "RimBridgeServer.Sdk.dll",
                SearchOption.AllDirectories).Any(),
            "the companion build output must not contain RimBridgeServer.Sdk.dll");

        Assert(publishScript.Contains("-t:Rebuild", StringComparison.Ordinal) &&
               publishScript.Contains("-DeployCompanion", StringComparison.Ordinal) &&
               publishScript.Contains("Get-FileSha256", StringComparison.Ordinal) &&
               publishScript.Contains("Move-Item", StringComparison.Ordinal) &&
               publishScript.Contains("RimBridgeSdkPath", StringComparison.Ordinal) &&
               publishScript.Contains("RimBridgeServer.Sdk.dll", StringComparison.Ordinal) &&
               publishScript.Contains("DevBridge2.BridgeTools.dll", StringComparison.Ordinal),
            "the repository publish workflow must rebuild, replace, hash-verify, and exclude the host SDK");
    }

    private static void TestBridgeToolsPublishRefreshesStaleDll()
    {
        if (SkipUnselectedBridgeToolsCoverage())
            return;

        string root = FindWorkspaceRoot();
        string script = Path.Combine(root, "Publish-DevBridge.ps1");
        string target = Path.Combine(Path.GetTempPath(), "DevBridge2-companion-publish-" +
            Guid.NewGuid().ToString("N"));
        string deploymentRoot = Path.Combine(target, "RimWorld", "Mods", "DevBridge2");
        string destination = Path.Combine(target, "RimWorld", "BridgeTools", "DevBridge2");
        string legacyDestination = Path.Combine(deploymentRoot, "BridgeTools");
        Directory.CreateDirectory(Path.Combine(deploymentRoot, "About"));
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(deploymentRoot, "About", "About.xml"),
            "<ModMetaData><packageId>lan.devbridge2</packageId></ModMetaData>", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(destination, "DevBridge2.BridgeTools.dll"),
            Encoding.UTF8.GetBytes("stale companion binary"));
        Directory.CreateDirectory(legacyDestination);
        File.WriteAllBytes(Path.Combine(legacyDestination, "DevBridge2.BridgeTools.dll"),
            Encoding.UTF8.GetBytes("obsolete mod-local companion binary"));

        try
        {
            RunPowerShell(script, "-CompanionOnly", "-DeployCompanion", "-DeploymentRoot", deploymentRoot);
            string built = Path.Combine(root, "Source", "BridgeTools", "bin", "Release",
                "DevBridge2.BridgeTools.dll");
            string deployed = Path.Combine(destination, "DevBridge2.BridgeTools.dll");
            Assert(File.Exists(deployed) &&
                   File.ReadAllBytes(deployed).SequenceEqual(File.ReadAllBytes(built)),
                "successful companion publishing must replace a stale deployed DLL with the rebuilt artifact");
            Assert(!File.Exists(Path.Combine(destination, "RimBridgeServer.Sdk.dll")) &&
                   Directory.GetFiles(destination).Select(Path.GetFileName).SequenceEqual(
                       new[] { "DevBridge2.BridgeTools.dll" }, StringComparer.OrdinalIgnoreCase),
                 "companion deployment must contain exactly the companion DLL and no host SDK");
            Assert(!Directory.Exists(legacyDestination),
                "companion deployment must remove the obsolete mod-local BridgeTools directory");
        }
        finally
        {
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }

    private static void TestBridgeToolsWrongLocationDiagnostic()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 0,
            Phase = BridgePhase.STOPPED
        });
        string wrongLocation = Path.Combine(fixture.Root, "BridgeTools");
        Directory.CreateDirectory(wrongLocation);
        File.WriteAllBytes(Path.Combine(wrongLocation, "DevBridge2.BridgeTools.dll"),
            Encoding.UTF8.GetBytes("mod-local companion"));

        JsonCommandResponse response = RunDoctor(fixture, out _, out _);
        Assert(response.Findings.Any(value => value.Code == "BRIDGETOOLS_WRONG_LOCATION"),
            "doctor must identify a companion deployed inside the mod instead of the sibling global bundle");
    }

    private static void TestRimBridgeCompanionDiagnosticCategory()
    {
        RimBridgeIntegrationState state = new()
        {
            CompanionErrorCode = RimBridgeIntegrationConstants.CompanionUnavailableCode,
            CompanionError = "The optional DevBridge generation-context tool is not registered."
        };
        Assert(RimBridgeCompanionDiagnostics.Code(state) ==
                   RimBridgeIntegrationConstants.CompanionToolNotRegisteredDiagnostic,
            "legacy unavailable state must expose the nonfatal tool-not-registered category");
    }

    private static bool SkipUnselectedBridgeToolsCoverage()
    {
        string scope = Environment.GetEnvironmentVariable("DEVBRIDGE_OFFLINE_TEST_SCOPE") ?? string.Empty;
        if (!scope.Equals("coordinator", StringComparison.OrdinalIgnoreCase))
            return false;

        Console.WriteLine("SKIP BridgeTools deployment coverage: BridgeTools is outside this coordinator-only impact plan.");
        return true;
    }

    private static void RunPowerShell(string script, params string[] arguments)
    {
        ProcessStartInfo start = new()
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("could not start powershell.exe for companion deployment testing");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("companion publish test timed out");
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("companion publish test failed: " + stderr.Result);
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo directory = new(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Source", "BridgeTools",
                    "DevBridge2.BridgeTools.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("DevBridge2 workspace root could not be located");
    }
}

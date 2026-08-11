using System.Diagnostics;

namespace DevBridge.Coordinator;

internal static class DevBridgeWrapperTests
{
    internal static void Run()
    {
        string sourceRoot = FindSourceRoot();
        string tempRoot = Path.Combine(Path.GetTempPath(), "DevBridge-wrapper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "Source", "Coordinator"));
        string wrapper = Path.Combine(tempRoot, "DevBridge.cmd");
        string fakeDotnet = Path.Combine(tempRoot, "dotnet.cmd");
        string capture = Path.Combine(tempRoot, "args.txt");
        try
        {
            File.Copy(Path.Combine(sourceRoot, "DevBridge.cmd"), wrapper);
            File.WriteAllText(Path.Combine(tempRoot, "Source", "Coordinator", "DevBridge.Coordinator.csproj"),
                "<!-- fake project; the fake dotnet command never builds or launches it -->");
            File.WriteAllText(fakeDotnet,
                "@echo off\r\n" +
                "> \"%FAKE_CAPTURE%\" echo %*\r\n" +
                "exit /b %FAKE_EXIT_CODE%\r\n");

            AssertExitCode(wrapper, fakeDotnet, capture, 0);
            AssertExitCode(wrapper, fakeDotnet, capture, 4);
            string forwarded = File.ReadAllText(capture);
            if (!forwarded.Contains("status", StringComparison.OrdinalIgnoreCase) ||
                !forwarded.Contains("--json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("wrapper did not forward command arguments to the coordinator");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); }
            catch { }
        }
    }

    private static void AssertExitCode(string wrapper, string fakeDotnet, string capture, int expected)
    {
        ProcessStartInfo start = new()
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Arguments = "/d /c call \"" + wrapper + "\" status --json";
        start.Environment["FAKE_EXIT_CODE"] = expected.ToString();
        start.Environment["FAKE_CAPTURE"] = capture;
        start.Environment["PATH"] = Path.GetDirectoryName(fakeDotnet) + ";" +
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("cmd did not start");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5000))
            throw new InvalidOperationException("wrapper test process timed out");
        if (process.ExitCode != expected)
            throw new InvalidOperationException("wrapper returned " + process.ExitCode + " instead of " + expected +
                "; stdout=" + stdout + "; stderr=" + stderr);
    }

    private static string FindSourceRoot()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DevBridge.cmd")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("DevBridge.cmd was not found");
    }
}

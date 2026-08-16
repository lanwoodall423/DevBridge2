using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DevBridge2;

// This file is compiled into both the coordinator and the RimWorld mod. Keep it
// free of System.Text.Json and other coordinator-only dependencies: the mod's
// target framework does not provide them.
public sealed class QuicktestFailureRecord
{
    public int SchemaVersion { get; set; }
    public string LaunchId { get; set; }
    public int Generation { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStartUtcTicks { get; set; }
    public string ProfileFingerprint { get; set; }
    public string BaselineFingerprint { get; set; }
    public string ProfileMode { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string FailurePhase { get; set; }
    public string FailureCode { get; set; }
    public string ExceptionType { get; set; }
    public string ExceptionMessage { get; set; }
    public string DiagnosticDetail { get; set; }
}

public static class QuicktestFailureArtifact
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "quicktest-failure.json";
    public const string StableFailureCode = "QUICKTEST_GENERATION_FAILED";
    public const int MaxLaunchIdLength = 128;
    public const int MaxFingerprintLength = 128;
    public const int MaxProfileModeLength = 64;
    public const int MaxPhaseLength = 128;
    public const int MaxCodeLength = 128;
    public const int MaxExceptionTypeLength = 256;
    public const int MaxExceptionMessageLength = 512;
    public const int MaxDiagnosticDetailLength = 2048;

    public static string PathFor(string root)
    {
        return Path.Combine(root ?? string.Empty, "Runtime", FileName);
    }

    public static bool TryWrite(string root, QuicktestFailureRecord record, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(root))
        {
            error = "runtime root is missing";
            return false;
        }
        if (record == null)
        {
            error = "failure record is null";
            return false;
        }

        string path = PathFor(root);
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            string json = Serialize(Bounded(record));
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            ReplaceFile(temporaryPath, path);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + Bounded(exception.Message, 512);
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // The original write error is the useful diagnostic.
            }
            return false;
        }
    }

    public static void Invalidate(string root)
    {
        string path = PathFor(root);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A launch-specific ID still prevents a stale artifact from being
            // accepted. Deletion is best effort at the launch boundary.
        }
    }

    public static bool TryQuarantine(string root, out string quarantinedPath)
    {
        quarantinedPath = null;
        string path = PathFor(root);
        try
        {
            if (!File.Exists(path))
                return false;

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(path) + ".rejected-" +
                Guid.NewGuid().ToString("N") + Path.GetExtension(path);
            quarantinedPath = Path.Combine(directory, name);
            File.Move(path, quarantinedPath);
            return true;
        }
        catch
        {
            quarantinedPath = null;
            return false;
        }
    }

    public static string Bounded(string value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        if (maximumLength <= 0)
            return string.Empty;
        return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
    }

    private static QuicktestFailureRecord Bounded(QuicktestFailureRecord source)
    {
        return new QuicktestFailureRecord
        {
            SchemaVersion = source.SchemaVersion <= 0 ? CurrentSchemaVersion : source.SchemaVersion,
            LaunchId = Bounded(source.LaunchId, MaxLaunchIdLength),
            Generation = source.Generation,
            ProcessId = source.ProcessId,
            ProcessStartUtcTicks = source.ProcessStartUtcTicks,
            ProfileFingerprint = Bounded(source.ProfileFingerprint, MaxFingerprintLength),
            BaselineFingerprint = Bounded(source.BaselineFingerprint, MaxFingerprintLength),
            ProfileMode = Bounded(source.ProfileMode, MaxProfileModeLength),
            TimestampUtc = source.TimestampUtc.ToUniversalTime(),
            FailurePhase = Bounded(source.FailurePhase, MaxPhaseLength),
            FailureCode = Bounded(source.FailureCode, MaxCodeLength),
            ExceptionType = Bounded(source.ExceptionType, MaxExceptionTypeLength),
            ExceptionMessage = Bounded(source.ExceptionMessage, MaxExceptionMessageLength),
            DiagnosticDetail = Bounded(source.DiagnosticDetail, MaxDiagnosticDetailLength)
        };
    }

    private static string Serialize(QuicktestFailureRecord record)
    {
        StringBuilder builder = new();
        builder.Append("{\"schemaVersion\":").Append(record.SchemaVersion);
        AppendString(builder, "launchId", record.LaunchId);
        builder.Append(",\"generation\":").Append(record.Generation);
        builder.Append(",\"processId\":").Append(record.ProcessId);
        builder.Append(",\"processStartUtcTicks\":").Append(record.ProcessStartUtcTicks);
        AppendString(builder, "profileFingerprint", record.ProfileFingerprint);
        AppendString(builder, "baselineFingerprint", record.BaselineFingerprint);
        AppendString(builder, "profileMode", record.ProfileMode);
        AppendString(builder, "timestampUtc", record.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        AppendString(builder, "failurePhase", record.FailurePhase);
        AppendString(builder, "failureCode", record.FailureCode);
        AppendString(builder, "exceptionType", record.ExceptionType);
        AppendString(builder, "exceptionMessage", record.ExceptionMessage);
        AppendString(builder, "diagnosticDetail", record.DiagnosticDetail);
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendString(StringBuilder builder, string name, string value)
    {
        builder.Append(",\"").Append(name).Append("\":");
        if (value == null)
        {
            builder.Append("null");
            return;
        }

        builder.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 32)
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }

    private static void ReplaceFile(string temporaryPath, string path)
    {
        if (File.Exists(path))
        {
            // Do not delete the previous evidence if replacement is refused:
            // a gap between generations would make a crash lose its durable
            // diagnostic. The caller reports the write failure and leaves the
            // complete previous artifact intact.
            File.Replace(temporaryPath, path, null);
            return;
        }
        File.Move(temporaryPath, path);
    }
}

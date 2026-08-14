using System.Globalization;
using System.Text.Json.Serialization;

namespace DevBridge.Coordinator;

internal sealed class TestInputValue
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; }
}

internal sealed class TestInputAssignment
{
    internal string Name { get; set; }
    internal string Value { get; set; }
}

internal sealed class TestInputSet
{
    [JsonPropertyName("values")]
    public List<TestInputValue> Values { get; set; } = new();

    [JsonIgnore]
    internal bool QuicktestEnabled { get; set; }

    [JsonIgnore]
    internal int QuicktestTimeoutSeconds { get; set; }

    [JsonIgnore]
    internal string QuicktestVariant { get; set; }

    internal TestInputSet Clone() => new()
    {
        Values = (Values ?? new List<TestInputValue>()).Select(value => new TestInputValue
        {
            Name = value.Name,
            Value = value.Value
        }).ToList(),
        QuicktestEnabled = QuicktestEnabled,
        QuicktestTimeoutSeconds = QuicktestTimeoutSeconds,
        QuicktestVariant = QuicktestVariant
    };
}

internal static class TestGenerationInputs
{
    internal const string QuicktestName = "quicktest";
    internal const string QuicktestTimeoutName = "quicktestTimeoutSeconds";
    internal const string QuicktestVariantName = "quicktestVariant";
    internal const string BuiltInVariant = "builtin-dev";
    internal const string DisabledVariant = "disabled";
    internal const int DefaultQuicktestTimeoutSeconds = 60;
    internal const int MinQuicktestTimeoutSeconds = 5;
    internal const int MaxQuicktestTimeoutSeconds = 120;
    private const int MaxInputNameLength = 64;
    private const int MaxInputValueLength = 128;

    internal static TestInputSet Normalize(IEnumerable<TestInputAssignment> assignments,
        string profileMode)
    {
        List<TestInputAssignment> inputList = (assignments ?? Enumerable.Empty<TestInputAssignment>()).ToList();
        if (inputList.Count > 0 && string.Equals(profileMode, ModProfile.LegacyMode, StringComparison.OrdinalIgnoreCase))
            throw new ProfileException("TEST_INPUT_NOT_SUPPORTED_FOR_PROFILE",
                "Declared test inputs are supported only by DevBridge baseline and project profiles.");

        Dictionary<string, string> raw = new(StringComparer.OrdinalIgnoreCase);
        foreach (TestInputAssignment assignment in inputList)
        {
            string name = assignment?.Name?.Trim() ?? string.Empty;
            string value = assignment?.Value?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Length > MaxInputNameLength)
                throw new ProfileException("TEST_INPUT_UNKNOWN", "The declared test input name is invalid.");
            if (value.Length == 0 || value.Length > MaxInputValueLength)
                throw new ProfileException("TEST_INPUT_INVALID_TYPE",
                    "Test input " + name + " must have one bounded scalar value.");
            if (!IsKnownName(name))
                throw new ProfileException("TEST_INPUT_UNKNOWN", "Unknown declared test input '" + name + "'.");
            if (!raw.TryAdd(name, value))
                throw new ProfileException("TEST_INPUT_UNKNOWN", "Test input '" + name + "' was declared more than once.");
        }

        bool quicktestEnabled = true;
        if (raw.TryGetValue(QuicktestName, out string enabledValue) &&
            !bool.TryParse(enabledValue, out quicktestEnabled))
            throw new ProfileException("TEST_INPUT_INVALID_TYPE",
                "Test input quicktest must be boolean true or false.");

        int timeoutSeconds = DefaultQuicktestTimeoutSeconds;
        if (raw.TryGetValue(QuicktestTimeoutName, out string timeoutValue) &&
            (!int.TryParse(timeoutValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeoutSeconds)))
            throw new ProfileException("TEST_INPUT_INVALID_TYPE",
                "Test input quicktestTimeoutSeconds must be an invariant integer.");
        if (timeoutSeconds < MinQuicktestTimeoutSeconds || timeoutSeconds > MaxQuicktestTimeoutSeconds)
            throw new ProfileException("TEST_INPUT_OUT_OF_RANGE",
                "Test input quicktestTimeoutSeconds must be between " + MinQuicktestTimeoutSeconds + " and " +
                MaxQuicktestTimeoutSeconds + " seconds.");

        string variant = quicktestEnabled ? BuiltInVariant : DisabledVariant;
        if (raw.TryGetValue(QuicktestVariantName, out string variantValue))
        {
            variant = variantValue.ToLowerInvariant();
            if (!string.Equals(variant, BuiltInVariant, StringComparison.Ordinal) &&
                !string.Equals(variant, DisabledVariant, StringComparison.Ordinal))
                throw new ProfileException("TEST_INPUT_VALUE_NOT_ALLOWED",
                    "Test input quicktestVariant must be builtin-dev or disabled.");
            bool variantRequestsQuicktest = string.Equals(variant, BuiltInVariant, StringComparison.Ordinal);
            if (raw.ContainsKey(QuicktestName) && quicktestEnabled != variantRequestsQuicktest)
                throw new ProfileException("TEST_INPUT_CONFLICT",
                    "quicktest and quicktestVariant request incompatible DevBridge test behavior.");
            quicktestEnabled = variantRequestsQuicktest;
        }

        if (!quicktestEnabled && raw.ContainsKey(QuicktestTimeoutName) &&
            timeoutSeconds != DefaultQuicktestTimeoutSeconds)
            throw new ProfileException("TEST_INPUT_NOT_SUPPORTED_FOR_PROFILE",
                "quicktestTimeoutSeconds applies only to the built-in Dev Quicktest variant.");

        return new TestInputSet
        {
            QuicktestEnabled = quicktestEnabled,
            QuicktestTimeoutSeconds = timeoutSeconds,
            QuicktestVariant = variant,
            Values = new List<TestInputValue>
            {
                new() { Name = QuicktestName, Value = quicktestEnabled ? "true" : "false" },
                new() { Name = QuicktestTimeoutName, Value = timeoutSeconds.ToString(CultureInfo.InvariantCulture) },
                new() { Name = QuicktestVariantName, Value = variant }
            }
        };
    }

    internal static TestInputSet FromValues(IEnumerable<TestInputValue> values, string profileMode)
    {
        return Normalize((values ?? Enumerable.Empty<TestInputValue>()).Select(value => new TestInputAssignment
        {
            Name = value?.Name,
            Value = value?.Value
        }), profileMode);
    }

    internal static TestInputAssignment ParseCommandAssignment(string raw)
    {
        string value = raw?.Trim() ?? string.Empty;
        int separator = value.IndexOf('=');
        if (separator <= 0 || separator == value.Length - 1)
            throw new ProfileException("TEST_INPUT_INVALID_TYPE",
                "Each --input must use the declared name=value form.");
        return new TestInputAssignment
        {
            Name = value.Substring(0, separator).Trim(),
            Value = value.Substring(separator + 1).Trim()
        };
    }

    internal static List<TestInputValue> CloneValues(IEnumerable<TestInputValue> values) =>
        (values ?? Enumerable.Empty<TestInputValue>()).Select(value => new TestInputValue
        {
            Name = value.Name,
            Value = value.Value
        }).ToList();

    internal static IReadOnlyList<string> SemanticFingerprintEntries(IEnumerable<TestInputValue> values)
    {
        TestInputSet normalized = FromValues(values, ModProfile.ProjectsMode);
        List<string> entries = new();
        if (!normalized.QuicktestEnabled)
            entries.Add(QuicktestName + "=false");
        if (normalized.QuicktestTimeoutSeconds != DefaultQuicktestTimeoutSeconds)
            entries.Add(QuicktestTimeoutName + "=" +
                normalized.QuicktestTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        if (!string.Equals(normalized.QuicktestVariant, BuiltInVariant, StringComparison.Ordinal))
            entries.Add(QuicktestVariantName + "=" + normalized.QuicktestVariant);
        return entries;
    }

    internal static bool AreEquivalent(IEnumerable<TestInputValue> left, IEnumerable<TestInputValue> right)
    {
        try
        {
            List<string> leftEntries = SemanticFingerprintEntries(left).ToList();
            List<string> rightEntries = SemanticFingerprintEntries(right).ToList();
            return leftEntries.SequenceEqual(rightEntries, StringComparer.Ordinal);
        }
        catch (ProfileException)
        {
            return false;
        }
    }

    internal static bool IsQuicktestEnabled(IEnumerable<TestInputValue> values)
    {
        return FromValues(values, ModProfile.ProjectsMode).QuicktestEnabled;
    }

    private static bool IsKnownName(string name) =>
        string.Equals(name, QuicktestName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, QuicktestTimeoutName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, QuicktestVariantName, StringComparison.OrdinalIgnoreCase);
}

using System.Globalization;

namespace DevBridge.Coordinator;

internal sealed partial class CoordinatorState
{
    private int History(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        arguments ??= Array.Empty<string>();
        string operation = arguments.Count == 0 ? string.Empty : arguments[0].Trim().ToLowerInvariant();
        int? selectedGeneration = null;
        bool lastGood = false;
        if (operation == "diff")
        {
            if (arguments.Count != 3)
            {
                emit("Usage: DevBridge.cmd history | history show <generation> | history last-good | history diff <from-generation> <to-generation> | history diagnose <generation>");
                return 2;
            }
            return ExecuteHistoryDiff(arguments.Skip(1).ToList(), request, emit);
        }
        if (operation == "diagnose")
        {
            if (arguments.Count != 2)
            {
                emit("Usage: DevBridge.cmd history | history show <generation> | history last-good | history diff <from-generation> <to-generation> | history diagnose <generation>");
                return 2;
            }
            return ExecuteHistoryDiagnose(arguments.Skip(1).ToList(), request, emit);
        }
        if (operation == "show")
        {
            if (arguments.Count != 2 || !int.TryParse(arguments[1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out int generation) || generation <= 0)
            {
                emit("Usage: DevBridge.cmd history | history show <generation> | history last-good | history diff <from-generation> <to-generation> | history diagnose <generation>");
                return 2;
            }
            selectedGeneration = generation;
        }
        else if (operation == "last-good")
        {
            if (arguments.Count != 1)
            {
                emit("Usage: DevBridge.cmd history | history show <generation> | history last-good | history diff <from-generation> <to-generation> | history diagnose <generation>");
                return 2;
            }
            lastGood = true;
        }
        else if (!string.IsNullOrWhiteSpace(operation))
        {
            emit("Usage: DevBridge.cmd history | history show <generation> | history last-good | history diff <from-generation> <to-generation> | history diagnose <generation>");
            return 2;
        }

        GenerationHistoryView view;
        lock (gate)
        {
            SynchronizeLocked();
            view = BuildGenerationHistoryViewLocked(state.Generation, selectedGeneration);
            if (lastGood)
            {
                int? lastKnownGood = view.LastKnownGoodGeneration;
                if (!lastKnownGood.HasValue)
                {
                    request.HistoryResult = view;
                    emit("No accepted READY generation has been recorded yet.");
                    return 1;
                }
                view.Selected = view.LastKnownGood;
            }
            request.HistoryResult = view;
        }

        if (view.Corrupt)
        {
            emit(view.ErrorCode + ": " + view.Error);
            emit("History was not rewritten. Run DevBridge.cmd doctor for the durable artifact diagnosis.");
            return 4;
        }

        if (selectedGeneration.HasValue && view.Selected == null)
        {
            emit("No history record exists for generation " + selectedGeneration.Value + ".");
            return 1;
        }

        if (lastGood)
        {
            emit("Last known good generation: " + view.LastKnownGoodGeneration.Value);
            EmitHistoryEntry(view.Selected, emit);
        }
        else if (selectedGeneration.HasValue)
        {
            emit("Generation: " + selectedGeneration.Value);
            EmitHistoryEntry(view.Selected, emit);
        }
        else
        {
            emit("Current accepted generation: " + view.CurrentGeneration);
            emit("Previous accepted generation: " + (view.PreviousGeneration?.ToString() ?? "none"));
            emit("Last known good generation: " + (view.LastKnownGoodGeneration?.ToString() ?? "none"));
            foreach (GenerationHistoryRecord record in view.Records)
                emit("Generation " + record.Generation + ": " + record.Status +
                    (string.IsNullOrWhiteSpace(record.TerminalFailureCode) ? string.Empty :
                        " (" + record.TerminalFailureCode + ")"));
            if (view.ProfileComparison != null)
                emit("Current profile matches last known good: " + view.ProfileComparison.SameProfile);
        }
        return 0;
    }

    private static void EmitHistoryEntry(GenerationHistoryEntry entry, Action<string> emit)
    {
        if (entry?.Record == null)
        {
            emit("No history record exists for the requested generation.");
            return;
        }
        GenerationHistoryRecord record = entry.Record;
        emit("Status: " + record.Status);
        emit("Observed UTC: " + record.ObservedUtc.ToString("O", CultureInfo.InvariantCulture));
        if (record.AcceptedUtc.HasValue)
            emit("Accepted UTC: " + record.AcceptedUtc.Value.ToString("O", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(record.TerminalFailureCode))
            emit("Terminal failure: " + record.TerminalFailureCode +
                (string.IsNullOrWhiteSpace(record.TerminalFailureDetail) ? string.Empty :
                    " - " + record.TerminalFailureDetail));
        GenerationManifest manifest = entry.Manifest;
        if (manifest == null)
        {
            emit("Accepted manifest: none (this generation did not reach READY).");
            return;
        }
        emit("Manifest: generations/" + manifest.Generation + ".json");
        emit("Profile fingerprint: " + (manifest.Profile.ProfileFingerprint ?? "none"));
        List<TestInputValue> historyInputs = manifest?.Profile?.TestInputs ??
            (entry.Record?.TestInputs ?? new List<TestInputValue>());
        emit("Test inputs: " + (historyInputs.Count == 0
            ? "none"
            : string.Join(", ", historyInputs.Select(value => value.Name + "=" + value.Value))));
        emit("Resolved mod order: " + (manifest.Profile.ResolvedMods.Count == 0
            ? "none" : string.Join(" -> ", manifest.Profile.ResolvedMods)));
        emit("ModsConfig fingerprint: " + (manifest.ModsConfig.Fingerprint ?? "none"));
    }
}

using SerialWorkbench.Domain;

namespace SerialWorkbench.Automation;

public static class ParallelResultAggregator
{
    public static RunResult Aggregate(IEnumerable<RunResult> results)
    {
        var values = results.ToArray();
        if (values.Length == 0)
        {
            return RunResult.Passed;
        }

        if (values.Contains(RunResult.RuntimeError))
        {
            return RunResult.RuntimeError;
        }

        if (values.Contains(RunResult.TimedOut))
        {
            return RunResult.TimedOut;
        }

        if (values.Contains(RunResult.ValidationFailed))
        {
            return RunResult.ValidationFailed;
        }

        return values.Contains(RunResult.Cancelled) ? RunResult.Cancelled : RunResult.Passed;
    }

    public static IReadOnlyDictionary<string, object?> MergeExports(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> branches,
        IReadOnlyDictionary<string, ExportMergeRule> rules)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in branches.SelectMany(static branch => branch.Keys).Distinct(StringComparer.Ordinal))
        {
            if (!rules.TryGetValue(name, out var rule))
            {
                throw new InvalidOperationException($"Export '{name}' has no merge rule.");
            }

            var values = branches.Where(branch => branch.ContainsKey(name)).Select(branch => branch[name]).ToArray();
            result[name] = rule switch
            {
                ExportMergeRule.AllEqual => values.Distinct().Count() == 1 ? values[0] : throw new InvalidOperationException($"Export '{name}' values differ."),
                ExportMergeRule.Collect => values,
                ExportMergeRule.FirstByBranchOrder => values.FirstOrDefault(),
                _ => throw new ArgumentOutOfRangeException(nameof(rules)),
            };
        }

        return result;
    }
}

public enum ExportMergeRule
{
    AllEqual,
    Collect,
    FirstByBranchOrder,
}

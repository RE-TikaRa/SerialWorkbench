namespace SerialWorkbench.Domain;

public enum SendContentFormat
{
    Text,
    Hex,
}

public sealed record SendItem(
    string Name,
    Guid ConnectionId,
    string Content,
    SendContentFormat Format = SendContentFormat.Text,
    string EncodingName = "utf-8",
    string LineEnding = "",
    int Repeat = 1,
    int IntervalMilliseconds = 0,
    bool Enabled = true);

public enum TestStepKind
{
    Send,
    Delay,
    WaitForBytes,
    ValidateBytes,
    Loop,
    Parallel,
    SetVariable,
    ManualConfirmation,
}

public sealed record TestStep(
    string Id,
    string Name,
    TestStepKind Kind,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<TestStep>? Children = null,
    IReadOnlyList<TestStep>? Cleanup = null,
    int RetryCount = 0);

public sealed record StepAttempt(
    string StepId,
    int Attempt,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    RunResult Result,
    string? Error);

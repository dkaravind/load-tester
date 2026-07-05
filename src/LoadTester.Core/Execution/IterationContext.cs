namespace LoadTester.Core.Execution;

/// <summary>Per-iteration state handed to templates: captured variables and the data feed row.</summary>
public sealed class IterationContext
{
    public long IterationId { get; init; }

    /// <summary>Variables captured from responses via step.capture; referenced as {{vars.name}}.
    /// Case-insensitive, matching token-name and data-feed column semantics.</summary>
    public Dictionary<string, string> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Row assigned from the scenario's data feed; referenced as {{data.column}}.</summary>
    public IReadOnlyDictionary<string, string>? DataRow { get; init; }
}

namespace Scrappy.Verify.Updates;

/// <summary>
/// One PASS or FAIL line per assertion, in the same spirit as tools/Scrappy.Verify: no framework,
/// no discovery, no colour. The exit code is the answer.
/// </summary>
public sealed class Report
{
    private readonly List<string> _failures = new();

    public int Count { get; private set; }

    public int FailureCount => _failures.Count;

    public IReadOnlyList<string> Failures => _failures;

    public void Check(string group, string name, bool ok, string? detail = null)
    {
        Count++;
        if (ok)
        {
            Console.WriteLine($"PASS  {group,-9} {name}");
            return;
        }

        string line = detail is null ? name : $"{name}  ({detail})";
        Console.WriteLine($"FAIL  {group,-9} {line}");
        _failures.Add($"{group}: {line}");
    }

    public void Unchanged(string group, string name, object? expected, object? actual)
    {
        bool ok = string.Equals(expected?.ToString(), actual?.ToString(), StringComparison.Ordinal);
        Check(group, name, ok, ok ? null : $"expected '{expected}', got '{actual}'");
    }
}

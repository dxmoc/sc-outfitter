namespace ScOutfitter.Tests;

/// <summary>Tiny test harness: no NuGet, plain checks with a coloured summary.</summary>
public sealed class TestRunner
{
    private readonly List<string> _failures = [];
    private string _section = string.Empty;
    private int _passed;

    public void Section(string title)
    {
        _section = title;
        Console.WriteLine();
        Console.WriteLine($"── {title} ".PadRight(74, '─'));
    }

    public void Check(string label, bool ok, string detail = "")
    {
        string suffix = detail.Length > 0 ? $"  → {detail}" : string.Empty;
        if (ok)
        {
            _passed++;
            Write(ConsoleColor.Green, "  PASS  ");
        }
        else
        {
            _failures.Add($"{_section}: {label}{suffix}");
            Write(ConsoleColor.Red, "  FAIL  ");
        }

        Console.WriteLine($"{label}{suffix}");
    }

    public void Equal<T>(string label, T expected, T actual) =>
        Check(label, EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    public void Close(string label, double expected, double actual, double tolerance = 1e-6) =>
        Check(label, Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    public async Task RunAsync(string title, Func<TestRunner, Task> body)
    {
        Section(title);
        try
        {
            await body(this).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Check($"{title} ran without throwing", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public int Report()
    {
        Console.WriteLine();
        Console.WriteLine(new string('─', 74));
        if (_failures.Count == 0)
        {
            Write(ConsoleColor.Green, $"{_passed} checks passed.");
            Console.WriteLine();
            return 0;
        }

        Write(ConsoleColor.Red, $"{_failures.Count} of {_passed + _failures.Count} checks failed:");
        Console.WriteLine();
        foreach (string f in _failures)
        {
            Console.WriteLine($"  - {f}");
        }

        return 1;
    }

    private static void Write(ConsoleColor color, string text)
    {
        ConsoleColor old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = old;
    }
}

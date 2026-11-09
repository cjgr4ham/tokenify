namespace Tokenify.Tests;

public static partial class Tests
{
    private static int _passed, _failed;
    private static readonly List<string> Failures = new();

    public static int RunAll(string? fixtures)
    {
        foreach (var (name, test) in All(fixtures))
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine($"  ok    {name}");
            }
            catch (SkipException skip)
            {
                Console.WriteLine($"  skip  {name}: {skip.Message}");
            }
            catch (Exception ex)
            {
                _failed++;
                Failures.Add(name);
                Console.WriteLine($"  FAIL  {name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    internal sealed class SkipException(string message) : Exception(message);

    internal static void Eq<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    internal static void True(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    private static partial IEnumerable<(string, Action)> All(string? fixtures);
}

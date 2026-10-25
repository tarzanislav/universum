using System.Diagnostics;
using System.Reflection;

namespace Universum.Tests;

/// <summary>Marks a test method (public, parameterless, returning void or Task).</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class TestAttribute : Attribute;

/// <summary>Thrown to skip a test when its precondition (e.g. installed MKVToolNix) is missing.</summary>
internal sealed class SkipException(string reason) : Exception(reason);

internal sealed class AssertionException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{what ?? "value"}: expected <{expected}>, actual <{actual}>");
        }
    }

    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? what = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
        {
            throw new AssertionException($"{what ?? "sequence"}: expected [{string.Join(", ", e)}], actual [{string.Join(", ", a)}]");
        }
    }

    public static TException Throws<TException>(Action action, string what)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new AssertionException($"{what}: expected {typeof(TException).Name}");
    }

    public static void Skip(string reason) => throw new SkipException(reason);
}

internal static class TestRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var filter = args.FirstOrDefault(a => !a.StartsWith('-'));
        var tests = typeof(TestRunner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => (Method: m, Name: $"{m.DeclaringType!.Name}.{m.Name}"))
            .Where(t => filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        int passed = 0, failed = 0, skipped = 0;
        var total = Stopwatch.StartNew();
        foreach (var (method, name) in tests)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var instance = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
                if (method.Invoke(instance, null) is Task task)
                {
                    await task.ConfigureAwait(false);
                }

                passed++;
                Console.WriteLine($"PASS  {name} ({watch.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex) when (Unwrap(ex) is SkipException skip)
            {
                skipped++;
                Console.WriteLine($"SKIP  {name}: {skip.Message}");
            }
            catch (Exception ex)
            {
                failed++;
                var inner = Unwrap(ex);
                Console.WriteLine($"FAIL  {name}: {inner.Message}");
                if (inner is not AssertionException)
                {
                    Console.WriteLine(inner);
                }

                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                {
                    Console.WriteLine($"::error title=Test {name}::{inner.Message.Replace('\n', ' ')}");
                }
            }
        }

        Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped in {total.Elapsed.TotalSeconds:F1} s");
        return failed == 0 && tests.Count > 0 ? 0 : 1;
    }

    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : ex;
}

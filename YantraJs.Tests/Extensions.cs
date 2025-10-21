namespace YantraJs.Tests;

public static class Extensions
{
    public static IReadOnlySet<T> AsReadOnly<T>(this HashSet<T> set)
    {
        return new Tests13.ReadOnlySet<T>(set);
    }
}

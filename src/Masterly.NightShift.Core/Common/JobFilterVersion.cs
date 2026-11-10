namespace Masterly.NightShift.Common;

/// <summary>
/// Counts the changes made to any <see cref="JobFilterCollection"/> or <see cref="JobFilterProviderCollection"/>,
/// so filter lists cached per job method are thrown away when the filters change.
/// </summary>
internal static class JobFilterVersion
{
    private static int _current;

    public static int Current => Volatile.Read(ref _current);

    public static void Increment() => Interlocked.Increment(ref _current);
}

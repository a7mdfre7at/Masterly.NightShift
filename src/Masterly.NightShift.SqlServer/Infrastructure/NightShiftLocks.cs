namespace Masterly.NightShift.SqlServer;

/// <summary>
/// Enters and leaves a <see cref="NightShiftLock"/> where a <c>lock</c> statement doesn't fit: without waiting, or
/// held across calls. <see cref="Monitor"/> is used only on net8.0, where the lock is a plain object.
/// </summary>
internal static class NightShiftLocks
{
    public static bool TryEnter([NotNull] NightShiftLock syncRoot)
    {
#if NET9_0_OR_GREATER
        return syncRoot.TryEnter();
#else
        return Monitor.TryEnter(syncRoot);
#endif
    }

    public static void Enter([NotNull] NightShiftLock syncRoot)
    {
#if NET9_0_OR_GREATER
        syncRoot.Enter();
#else
        Monitor.Enter(syncRoot);
#endif
    }

    public static void Exit([NotNull] NightShiftLock syncRoot)
    {
#if NET9_0_OR_GREATER
        syncRoot.Exit();
#else
        Monitor.Exit(syncRoot);
#endif
    }
}

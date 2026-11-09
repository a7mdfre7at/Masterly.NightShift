// One lock type for both target frameworks. System.Threading.Lock (.NET 9 and later) is faster than locking an
// object, and the lock statement uses it the same way; on net8.0 the fields stay plain objects.
#if NET9_0_OR_GREATER
global using NightShiftLock = System.Threading.Lock;
#else
global using NightShiftLock = System.Object;
#endif

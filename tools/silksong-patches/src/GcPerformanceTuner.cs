// GcPerformanceTuner — spread managed-heap collection work across frames.
// Do not call GC.Collect() on a timer or every scene: forced full collections
// can create larger hitches and can fight Unity's own collector. Incremental GC
// lets Unity budget collection work over multiple frames instead.

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
using UnityEngine.Scripting;

public static class GcPerformanceTuner
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void Bootstrap()
    {
        try
        {
            // Incremental mode is supported by modern Unity runtimes. Keep a
            // small per-frame time slice to reduce collection spikes without
            // disabling collection or changing the game's memory limits.
            GarbageCollector.isIncremental = true;
            GarbageCollector.incrementalTimeSliceNanoseconds = 1_000_000;
            Debug.Log("[GcPerformanceTuner] incremental GC enabled; slice=1ms");
        }
        catch (System.Exception ex)
        {
            // A runtime/version mismatch must not prevent startup.
            Debug.LogWarning("[GcPerformanceTuner] incremental GC unavailable: " + ex.Message);
        }
    }
}
#endif

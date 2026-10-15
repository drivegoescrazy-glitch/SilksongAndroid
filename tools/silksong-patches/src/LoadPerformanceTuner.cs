// LoadPerformanceTuner — reduce competition between background asset loading
// and gameplay on mobile CPUs. This deliberately changes only Unity's loader
// thread priority; it does not rewrite game loading calls, force synchronous
// loads, unload live assets, or alter memory budgets. Lower priority can make
// loading take slightly longer, but may reduce frame-time spikes while work is
// happening in the background.

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;

public static class LoadPerformanceTuner
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void Bootstrap()
    {
        try
        {
            Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.BelowNormal;
            Debug.Log("[LoadPerformanceTuner] background loading priority set to BelowNormal");
        }
        catch (System.Exception ex)
        {
            // A tuning failure must never prevent the game from starting.
            Debug.LogWarning("[LoadPerformanceTuner] could not adjust loader priority: " + ex.Message);
        }
    }
}
#endif

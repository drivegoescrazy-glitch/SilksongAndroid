// Optional lower-cost quality profile intended to reduce GPU-bound frame-time spikes.
// The setting is exported by the launcher and read once at game startup.
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class PerformanceImprovements : MonoBehaviour
{
    static PerformanceImprovements _instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        if (!SilksongPatches.Settings.GetBool("performance_improvements", false))
            return;
        if (_instance != null) return;

        var go = new GameObject("__SilksongPerformanceImprovements__");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<PerformanceImprovements>();
    }

    void Awake()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        Apply();
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (_instance == this) _instance = null;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Apply();
    }

    static void Apply()
    {
        try
        {
            // Avoid costly soft-shadow filtering and keep shadow-map work bounded.
            QualitySettings.shadows = ShadowQuality.HardOnly;
            QualitySettings.shadowResolution = ShadowResolution.Low;
            QualitySettings.shadowDistance = Mathf.Min(QualitySettings.shadowDistance, 18f);
            QualitySettings.shadowCascades = 1;

            // These effects can add GPU passes or extra lighting work without
            // changing game logic, input, physics, or save behaviour.
            QualitySettings.softParticles = false;
            QualitySettings.realtimeReflectionProbes = false;
            QualitySettings.pixelLightCount = Mathf.Min(QualitySettings.pixelLightCount, 1);
            QualitySettings.antiAliasing = 0;

            Debug.Log("[PerformanceImprovements] lower-cost quality profile enabled");
        }
        catch (System.Exception e)
        {
            // Never prevent the game from starting if a Unity version does not
            // expose one of the optional quality controls.
            Debug.LogWarning("[PerformanceImprovements] partial application: " + e.Message);
        }
    }
}
#endif

// Optional low-cost profile intended to reduce GPU-bound frame-time spikes.
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

    void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Apply();

    static void Apply()
    {
        try
        {
            // Bound expensive shadow-map and filtering work.
            QualitySettings.shadows = ShadowQuality.HardOnly;
            QualitySettings.shadowResolution = ShadowResolution.Low;
            QualitySettings.shadowDistance = Mathf.Min(QualitySettings.shadowDistance, 12f);
            QualitySettings.shadowCascades = 1;

            // Reduce additional lighting, particle, reflection and AA passes.
            QualitySettings.softParticles = false;
            QualitySettings.realtimeReflectionProbes = false;
            QualitySettings.pixelLightCount = 0;
            QualitySettings.antiAliasing = 0;

            // Prefer cheaper geometry LODs and avoid a VSync wait.
            QualitySettings.lodBias = Mathf.Min(QualitySettings.lodBias, 0.8f);
            QualitySettings.maximumLODLevel = Mathf.Max(QualitySettings.maximumLODLevel, 1);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;

            Debug.Log("[PerformanceImprovements] low-cost 60 FPS profile enabled");
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

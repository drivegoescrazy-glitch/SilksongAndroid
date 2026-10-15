// ShaderWarmup — avoids overlapping scene warmups and duplicate work during
// rapid scene transitions. Shader.WarmupAllShaders is synchronous, so queueing
// multiple coroutines can turn a transition into a long CPU spike.
//
// Warmup is still performed after each stable scene load so newly loaded shader
// variants can be prepared. If another scene loads before the deferred warmup
// starts, the stale request is cancelled instead of doing redundant work.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ShaderWarmup : MonoBehaviour
{
    static int s_sceneGeneration;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("__ShaderWarmup__");
        DontDestroyOnLoad(go);
        go.AddComponent<ShaderWarmup>();
        Debug.Log("[ShaderWarmup] registered");
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        int generation = ++s_sceneGeneration;
        StartCoroutine(WarmAfterFrame(scene.name, generation));
    }

    static IEnumerator WarmAfterFrame(string sceneName, int generation)
    {
        // Let scene renderers and addressable bundles initialise first.
        yield return null;

        // A newer scene arrived while we were waiting. Don't run an expensive
        // global warmup for a scene that is no longer active.
        if (generation != s_sceneGeneration)
            yield break;

        var sw = Stopwatch.StartNew();
        Shader.WarmupAllShaders();
        sw.Stop();

        if (sw.ElapsedMilliseconds > 20)
            Debug.Log($"[ShaderWarmup] {sceneName}: WarmupAllShaders {sw.ElapsedMilliseconds}ms");
    }
}
#endif

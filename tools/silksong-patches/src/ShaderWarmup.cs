// ShaderWarmup — one global warmup per app session, and never overlap queued
// warmups during rapid scene transitions. WarmupAllShaders is synchronous and
// can cause a CPU spike; repeating it for every scene adds cost without helping
// variants that have already been prepared.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ShaderWarmup : MonoBehaviour
{
    static int s_sceneGeneration;
    static bool s_warmupCompleted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("__ShaderWarmup__");
        DontDestroyOnLoad(go);
        go.AddComponent<ShaderWarmup>();
        Debug.Log("[ShaderWarmup] registered (single warmup per session)");
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (s_warmupCompleted) return;

        int generation = ++s_sceneGeneration;
        StartCoroutine(WarmAfterFrame(scene.name, generation));
    }

    static IEnumerator WarmAfterFrame(string sceneName, int generation)
    {
        // Give scene activation and initial asset setup a frame to settle.
        yield return null;

        // Skip stale requests and avoid repeating the global CPU-heavy pass.
        if (generation != s_sceneGeneration || s_warmupCompleted)
            yield break;

        // Set before calling the synchronous API so no later scene callback
        // can schedule a second warmup in this session.
        s_warmupCompleted = true;

        var sw = Stopwatch.StartNew();
        Shader.WarmupAllShaders();
        sw.Stop();

        if (sw.ElapsedMilliseconds > 20)
            Debug.Log($"[ShaderWarmup] {sceneName}: WarmupAllShaders {sw.ElapsedMilliseconds}ms");
    }
}
#endif

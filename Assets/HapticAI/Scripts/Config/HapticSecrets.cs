using System;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace HapticAI.Config
{
    /// <summary>
    /// Runtime secrets kept OUT of source control. Loaded from
    /// <c>Assets/StreamingAssets/hapticai.local.json</c> (gitignored). A committed
    /// <c>hapticai.config.template.json</c> documents the shape. Since the prompting move this is
    /// just the backend URL — the Gemini key now lives on that server, not on the headset. An empty
    /// URL runs the scene fully on-device (offline mock synth).
    /// </summary>
    [Serializable]
    public sealed class HapticSecrets
    {
        public string hapticServiceUrl = "";

        public static async Task<HapticSecrets> LoadAsync(string fileName = "hapticai.local.json")
        {
            // StreamingAssets is inside the APK on Android, so it must be read via UnityWebRequest.
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, fileName);
            string uri = path.Contains("://") ? path : "file://" + path;

            using var www = UnityWebRequest.Get(uri);
            await www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[HapticAI] No secrets file at {fileName} ({www.error}). " +
                                 "Running fully on-device (offline synthesis, no backend).");
                return new HapticSecrets();
            }

            try
            {
                return JsonUtility.FromJson<HapticSecrets>(www.downloadHandler.text) ?? new HapticSecrets();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HapticAI] Failed to parse {fileName}: {e.Message}");
                return new HapticSecrets();
            }
        }
    }
}

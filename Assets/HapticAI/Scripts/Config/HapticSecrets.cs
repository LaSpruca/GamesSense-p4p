using System;
using System.Threading.Tasks;
using HapticAI.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace HapticAI.Config
{
    /// <summary>
    /// Runtime secrets kept OUT of source control: since the prompting move this is just the
    /// backend URL — the Gemini key now lives on that server, not on the headset.
    ///
    /// Resolved from the first source that supplies a non-empty URL:
    ///   1. the <c>HAPTIC_SERVICE_URL</c> environment variable (handy in the Editor, and how CI
    ///      would point at a test server without touching the project),
    ///   2. the address saved on this headset from the setup scene (<see cref="StudySettings"/>),
    ///   3. <c>Assets/StreamingAssets/hapticai.local.json</c> (gitignored; a committed
    ///      <c>hapticai.config.template.json</c> documents the shape),
    ///   4. in the Editor only, <see cref="EditorLocalhostUrl"/>.
    ///
    /// Step 2 sits above the shipped file on purpose: it is the only source the person running a
    /// study can change, and it is a deliberate choice made minutes ago on this headset, so it must
    /// beat whatever address happened to be baked into the build. It is also the only source that
    /// can say "no server" out loud - once the setup screen has been used and the server switched
    /// off, resolution stops there and the experience runs on-device, instead of quietly picking up
    /// a stale URL from the file below it.
    ///
    /// The last step exists because the failure it prevents is invisible: with no URL the factory
    /// quietly builds an on-device mock pipeline, so a running local server sits unused and every
    /// clip is a hashed sine blob that feels nothing like generated haptics. It is Editor-only
    /// because on a headset "localhost" is the headset.
    /// </summary>
    [Serializable]
    public sealed class HapticSecrets
    {
        public const string EnvVarName = "HAPTIC_SERVICE_URL";
        public const string EditorLocalhostUrl = "http://localhost:8080";

        public string hapticServiceUrl = "";

        /// <summary>Where the URL in use came from, for logging and the on-headset readout.</summary>
        public string Source { get; private set; } = "none";

        public static async Task<HapticSecrets> LoadAsync(string fileName = "hapticai.local.json",
            bool allowEditorLocalhostFallback = true)
        {
            string fromEnv = SafeGetEnv(EnvVarName);
            if (!string.IsNullOrEmpty(fromEnv))
                return Resolved(fromEnv, $"{EnvVarName} environment variable");

            var saved = StudySettings.Current;
            if (saved.Configured)
            {
                // Switched off is an explicit choice, so it stops resolution here rather than
                // falling through to an address the operator cannot see or change.
                if (!saved.useServer)
                {
                    Debug.Log("[HapticAI] Setup screen has the haptics server switched off - running " +
                              "fully on-device.");
                    return new HapticSecrets { Source = "setup screen (server off)" };
                }

                // Server on but no address set yet is NOT a choice - it is an unfinished one, so it
                // falls through to the sources below rather than stranding the app on-device.
                if (!string.IsNullOrEmpty(saved.ServiceUrl))
                    return Resolved(saved.ServiceUrl, "setup screen (saved on this headset)");
            }

            var shipped = await LoadShippedAsync(fileName);
            if (!string.IsNullOrEmpty(shipped.hapticServiceUrl)) return shipped;

            if (allowEditorLocalhostFallback && Application.isEditor)
            {
                Debug.LogWarning($"[HapticAI] No haptic service URL configured ({EnvVarName} unset and " +
                                 $"no usable StreamingAssets/{fileName}). Falling back to {EditorLocalhostUrl} " +
                                 "because this is the Editor. Create the file, or set the environment " +
                                 "variable, to point somewhere else.");
                return Resolved(EditorLocalhostUrl, "Editor localhost fallback");
            }

            Debug.LogError("[HapticAI] No haptic service URL configured. Set the address on the setup " +
                           $"screen, or set {EnvVarName}, or copy " +
                           $"StreamingAssets/hapticai.config.template.json to {fileName} and fill in " +
                           "hapticServiceUrl. Running fully on-device: every clip will come from the " +
                           "offline mock synth, NOT from HaptiGen.");
            return new HapticSecrets();
        }

        /// <summary>
        /// Where the BUILD points - the environment variable and the StreamingAssets file - with
        /// nothing saved on this headset considered, and no complaint when neither is set.
        ///
        /// The setup screen uses this to prefill its address editor on a headset that has never
        /// been configured, so the first thing an operator sees is the address the app would
        /// otherwise have used, rather than a made-up default.
        /// </summary>
        public static async Task<HapticSecrets> LoadShippedAsync(string fileName = "hapticai.local.json")
        {
            string fromEnv = SafeGetEnv(EnvVarName);
            if (!string.IsNullOrEmpty(fromEnv))
                return Resolved(fromEnv, $"{EnvVarName} environment variable");

            var fromFile = await LoadFileAsync(fileName);
            if (fromFile != null && !string.IsNullOrEmpty(fromFile.hapticServiceUrl))
                return Resolved(fromFile.hapticServiceUrl, $"StreamingAssets/{fileName}");

            return new HapticSecrets();
        }

        static HapticSecrets Resolved(string url, string source)
        {
            Debug.Log($"[HapticAI] Haptic service URL {url} (from {source}).");
            return new HapticSecrets { hapticServiceUrl = url, Source = source };
        }

        static async Task<HapticSecrets> LoadFileAsync(string fileName)
        {
            // StreamingAssets is inside the APK on Android, so it must be read via UnityWebRequest.
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, fileName);
            string uri = path.Contains("://") ? path : "file://" + path;

            using var www = UnityWebRequest.Get(uri);
            await www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success) return null;

            try
            {
                return JsonUtility.FromJson<HapticSecrets>(www.downloadHandler.text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HapticAI] Failed to parse {fileName}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reading environment variables is permitted on Android but has thrown on some IL2CPP
        /// configurations; a missing URL must degrade to the next source, never break startup.
        /// </summary>
        static string SafeGetEnv(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch { return null; }
        }

        /// <summary>
        /// Ask the backend whether it is actually up, so "the server was running" stops being a
        /// guess. Returns a short human-readable status for the briefing panel.
        /// </summary>
        public static async Task<ProbeResult> ProbeAsync(string baseUrl, int timeoutSeconds = 5)
        {
            if (string.IsNullOrEmpty(baseUrl))
                return new ProbeResult { Reachable = false, Message = "offline (no URL)" };

            using var www = UnityWebRequest.Get(baseUrl.TrimEnd('/') + "/health");
            www.timeout = timeoutSeconds < 1 ? 5 : timeoutSeconds;
            await www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"[HapticAI] Backend health OK: {www.downloadHandler.text}");
                return new ProbeResult { Reachable = true, Message = "reachable" };
            }

            Debug.LogWarning($"[HapticAI] Backend at {baseUrl} did not answer /health " +
                             $"({www.error}). Clips will fall back to on-device synthesis.");
            return new ProbeResult { Reachable = false, Message = $"UNREACHABLE ({www.error})" };
        }

        /// <summary>Outcome of <see cref="ProbeAsync"/>: whether the backend answered, and what to show.</summary>
        public struct ProbeResult
        {
            public bool Reachable;
            public string Message;
        }
    }
}

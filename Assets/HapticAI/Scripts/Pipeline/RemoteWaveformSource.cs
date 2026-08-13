using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Core;
using HapticAI.Net;
using HapticAI.Synth;
using UnityEngine;
using UnityEngine.Networking;

namespace HapticAI.Pipeline
{
    /// <summary>
    /// Sends the raw <see cref="HapticEvent"/> to the backend, which does the whole prompt-
    /// generation process (context building + the Gemini chain) plus HaptiGen synthesis and returns
    /// audio/wav. Contract (see server/hapticgen_server.py):
    ///   POST {url}/haptics  body {event,custom_name?,hand,numbers,tags,duration_s,seed} → audio/wav
    /// On any failure it falls back to the injected on-device source so the scene keeps working
    /// offline (the "keep full on-device offline mode" behaviour).
    /// </summary>
    public sealed class RemoteWaveformSource : IHapticWaveformSource
    {
        readonly string _endpoint;
        readonly int _timeoutSeconds;
        readonly IHapticWaveformSource _fallback;

        public RemoteWaveformSource(string baseUrl, IHapticWaveformSource fallback, int timeoutSeconds = 30)
        {
            _endpoint = baseUrl.TrimEnd('/') + "/haptics";
            _fallback = fallback;
            _timeoutSeconds = timeoutSeconds < 1 ? 30 : timeoutSeconds;
        }

        public async Task<WaveformResult> GenerateAsync(HapticEvent evt, HapticSynthOptions options, CancellationToken ct)
        {
            try
            {
                return await RequestAsync(evt, options, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation is not a service failure — let the director handle it.
            }
            catch (Exception e)
            {
                if (_fallback == null) throw;
                Debug.LogWarning($"[HapticAI] Remote haptic service failed ({e.Message}); " +
                                 "falling back to on-device synthesis.");
                var degraded = await _fallback.GenerateAsync(evt, options, ct);
                degraded.IsDegraded = true;   // keep for this session, but never persist it
                return degraded;
            }
        }

        async Task<WaveformResult> RequestAsync(HapticEvent evt, HapticSynthOptions options, CancellationToken ct)
        {
            byte[] body = Encoding.UTF8.GetBytes(BuildPayload(evt, options));

            using var www = new UnityWebRequest(_endpoint, "POST")
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = _timeoutSeconds,
            };
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Accept", "audio/wav");

            using (ct.Register(www.Abort))
                await www.SendWebRequest();

            ct.ThrowIfCancellationRequested();
            if (www.result != UnityWebRequest.Result.Success)
                throw new Exception($"HTTP {www.responseCode}: {www.error}");

            var waveform = WavCodec.Decode(www.downloadHandler.data);
            string prompt = www.GetResponseHeader("X-Haptic-Prompt");
            if (string.IsNullOrEmpty(prompt)) prompt = "(server)";
            return new WaveformResult
            {
                Waveform = waveform,
                Prompt = prompt,
                Trace = new List<string> { $"[server] {prompt}" },
            };
        }

        // --- JSON payload. Hand-built because JsonUtility can't serialise dictionaries. ---

        static string BuildPayload(HapticEvent evt, HapticSynthOptions options)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            AppendString(sb, "event", evt.Type.ToString());
            sb.Append(',');
            if (evt.Type == HapticEventType.Custom && !string.IsNullOrEmpty(evt.CustomName))
            {
                AppendString(sb, "custom_name", evt.CustomName);
                sb.Append(',');
            }
            AppendString(sb, "hand", evt.Hand.ToString());
            sb.Append(',');

            sb.Append("\"numbers\":{");
            bool first = true;
            foreach (var kv in evt.Numbers)
            {
                if (!first) sb.Append(',');
                first = false;
                AppendKey(sb, kv.Key);
                sb.Append(kv.Value.ToString("R", CultureInfo.InvariantCulture));
            }
            sb.Append("},");

            sb.Append("\"tags\":{");
            first = true;
            foreach (var kv in evt.Tags)
            {
                if (!first) sb.Append(',');
                first = false;
                AppendKey(sb, kv.Key);
                AppendJsonString(sb, kv.Value);
            }
            sb.Append("},");

            AppendKey(sb, "duration_s");
            sb.Append(options.DurationSeconds.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(',');
            AppendKey(sb, "seed");
            sb.Append(options.Seed.ToString(CultureInfo.InvariantCulture));

            sb.Append('}');
            return sb.ToString();
        }

        static void AppendString(StringBuilder sb, string key, string value)
        {
            AppendKey(sb, key);
            AppendJsonString(sb, value);
        }

        static void AppendKey(StringBuilder sb, string key)
        {
            AppendJsonString(sb, key);
            sb.Append(':');
        }

        static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }
    }
}

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace HapticAI.Synth
{
    /// <summary>
    /// Calls a hosted HaptiGen inference server (see /server/hapticgen_server.py) that runs the
    /// ~3.7 GB PyTorch model on a GPU — HaptiGen can't run on the headset itself. Contract:
    ///   POST {url}/generate   body: {"prompt","duration_s","seed"}   → audio/wav bytes.
    /// Kept behind <see cref="IHapticSynthProvider"/> so it is a drop-in swap for the mock.
    /// </summary>
    public sealed class HaptiGenHttpSynth : IHapticSynthProvider
    {
        readonly string _endpoint;
        readonly int _timeoutSeconds;

        public HaptiGenHttpSynth(string baseUrl, int timeoutSeconds = 30)
        {
            _endpoint = baseUrl.TrimEnd('/') + "/generate";
            _timeoutSeconds = timeoutSeconds;
        }

        public async Task<Waveform> SynthesizeAsync(string prompt, HapticSynthOptions options, CancellationToken ct)
        {
            var req = new Request { prompt = prompt, duration_s = options.DurationSeconds, seed = options.Seed };
            byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(req));

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
                throw new Exception($"HaptiGen request failed ({www.responseCode}): {www.error}");

            return WavCodec.Decode(www.downloadHandler.data);
        }

        [Serializable]
        class Request
        {
            public string prompt;
            public float duration_s;
            public int seed;
        }
    }
}

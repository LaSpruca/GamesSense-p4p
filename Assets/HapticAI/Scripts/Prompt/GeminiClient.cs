using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace HapticAI.Prompt
{
    /// <summary>
    /// Minimal client for the Gemini Cloud REST API (generateContent). Runs from the headset
    /// over HTTPS because on-device Gemini Nano (Android AICore) is unavailable on Horizon OS.
    /// Swap this out for a different LLM by implementing another <see cref="Pipeline.IPromptStage"/>.
    /// </summary>
    public sealed class GeminiClient
    {
        readonly string _apiKey;
        readonly string _model;
        readonly string _endpointBase;
        readonly float _temperature;
        readonly int _maxOutputTokens;

        public GeminiClient(string apiKey, string model = "gemini-2.5-flash",
            string endpointBase = "https://generativelanguage.googleapis.com/v1beta",
            float temperature = 0.7f, int maxOutputTokens = 256)
        {
            _apiKey = apiKey;
            _model = string.IsNullOrEmpty(model) ? "gemini-2.5-flash" : model;
            _endpointBase = endpointBase.TrimEnd('/');
            _temperature = temperature;
            _maxOutputTokens = maxOutputTokens;
        }

        public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

        public async Task<string> GenerateAsync(string systemInstruction, string userText, CancellationToken ct)
        {
            if (!HasApiKey)
                throw new InvalidOperationException("Gemini API key is not configured.");

            var request = new GeminiRequest
            {
                system_instruction = new GeminiContent { parts = new[] { new GeminiPart { text = systemInstruction } } },
                contents = new[] { new GeminiContent { role = "user", parts = new[] { new GeminiPart { text = userText } } } },
                generationConfig = new GeminiGenConfig { temperature = _temperature, maxOutputTokens = _maxOutputTokens },
            };

            string url = $"{_endpointBase}/models/{_model}:generateContent?key={_apiKey}";
            byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(request));

            using var www = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            www.SetRequestHeader("Content-Type", "application/json");

            using (ct.Register(www.Abort))
                await www.SendWebRequest();

            ct.ThrowIfCancellationRequested();
            if (www.result != UnityWebRequest.Result.Success)
                throw new Exception($"Gemini request failed ({www.responseCode}): {www.error}\n{www.downloadHandler.text}");

            var parsed = JsonUtility.FromJson<GeminiResponse>(www.downloadHandler.text);
            string text = parsed?.FirstText();
            if (string.IsNullOrWhiteSpace(text))
                throw new Exception($"Gemini returned no text. Raw: {www.downloadHandler.text}");
            return text.Trim();
        }

        // --- JsonUtility DTOs (field names must match the REST payload exactly) ---

        [Serializable] class GeminiPart { public string text; }

        [Serializable]
        class GeminiContent
        {
            public string role;
            public GeminiPart[] parts;
        }

        [Serializable]
        class GeminiGenConfig
        {
            public float temperature;
            public int maxOutputTokens;
        }

        [Serializable]
        class GeminiRequest
        {
            public GeminiContent system_instruction;
            public GeminiContent[] contents;
            public GeminiGenConfig generationConfig;
        }

        [Serializable]
        class GeminiResponse
        {
            public GeminiCandidate[] candidates;
            public string FirstText()
            {
                if (candidates == null || candidates.Length == 0) return null;
                var parts = candidates[0]?.content?.parts;
                return parts != null && parts.Length > 0 ? parts[0].text : null;
            }
        }

        [Serializable] class GeminiCandidate { public GeminiContent content; }
    }
}

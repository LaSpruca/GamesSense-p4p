using System.Threading;
using System.Threading.Tasks;
using HapticAI.Pipeline;

namespace HapticAI.Prompt
{
    /// <summary>
    /// A single Gemini-backed link in the prompt chain. Its behaviour is entirely defined by its
    /// <see cref="_systemInstruction"/> and <see cref="_userTemplate"/> (both editable as data on
    /// the config asset), so you can chain several of these — e.g. "summarise the environment",
    /// then "turn that into a haptic design brief", then "write the final HaptiGen prompt" —
    /// without writing new code.
    ///
    /// Template placeholders substituted before the call:
    ///   {input}  → the previous stage's output (or the raw context brief for the first stage)
    ///   {brief}  → the full context brief
    ///   {event}  → the event label (ignite/spread/…)
    /// </summary>
    public sealed class GeminiPromptStage : IPromptStage
    {
        readonly GeminiClient _client;
        readonly string _systemInstruction;
        readonly string _userTemplate;

        public string Name { get; }

        public GeminiPromptStage(string name, GeminiClient client, string systemInstruction, string userTemplate)
        {
            Name = name;
            _client = client;
            _systemInstruction = systemInstruction ?? "";
            _userTemplate = string.IsNullOrEmpty(userTemplate) ? "{input}" : userTemplate;
        }

        public async Task<PromptState> RunAsync(PromptState state, CancellationToken ct)
        {
            string user = _userTemplate
                .Replace("{input}", state.Current ?? "")
                .Replace("{brief}", state.Context.ToBrief())
                .Replace("{event}", state.Context.EventLabel);

            string output = await _client.GenerateAsync(_systemInstruction, user, ct);
            state.Record(Name, output);
            return state;
        }
    }
}

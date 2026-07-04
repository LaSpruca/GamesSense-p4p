using System.Threading;
using System.Threading.Tasks;
using HapticAI.Pipeline;

namespace HapticAI.Prompt
{
    /// <summary>
    /// A deterministic, network-free prompt stage. Used as the whole chain when no Gemini key is
    /// configured (so the scene still runs on-device offline), and as a unit-test double. It just
    /// wraps the context brief in a HaptiGen-friendly instruction.
    /// </summary>
    public sealed class OfflinePromptStage : IPromptStage
    {
        public string Name => "offline";

        public Task<PromptState> RunAsync(PromptState state, CancellationToken ct)
        {
            string brief = state.Context.ToBrief();
            string prompt = $"A short vibrotactile haptic effect representing: {brief}. " +
                            "Texture and sharpness should match the described intensity and materials.";
            state.Record(Name, prompt);
            return Task.FromResult(state);
        }
    }
}

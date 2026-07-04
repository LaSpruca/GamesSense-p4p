using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Core;

namespace HapticAI.Pipeline
{
    /// <summary>
    /// Carried through the prompt chain. <see cref="Current"/> is the working text handed from
    /// one stage to the next (the final stage's <see cref="Current"/> is the HaptiGen prompt).
    /// <see cref="Trace"/> keeps every stage's output for on-device debugging.
    /// </summary>
    public sealed class PromptState
    {
        public readonly HapticContext Context;
        public string Current;
        public readonly List<string> Trace = new List<string>();

        public PromptState(HapticContext context)
        {
            Context = context;
            Current = context.ToBrief();
        }

        public void Record(string stageName, string output)
        {
            Current = output;
            Trace.Add($"[{stageName}] {output}");
        }
    }

    /// <summary>
    /// One link in the LLM chain. Stages are composed in a <see cref="PromptChain"/>; each may
    /// be a Gemini call with its own template, or an offline transform. Reorder / add / retune
    /// stages to vary how prompts are generated without touching the runtime.
    /// </summary>
    public interface IPromptStage
    {
        string Name { get; }
        Task<PromptState> RunAsync(PromptState state, CancellationToken ct);
    }
}

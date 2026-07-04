using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Core;

namespace HapticAI.Pipeline
{
    /// <summary>
    /// Runs a list of <see cref="IPromptStage"/> in order, threading each stage's output into
    /// the next. The whole chain is just this list, so "vary how prompts are generated" means
    /// editing the list — e.g. environment-summary → haptic-design-brief → haptigen-prompt.
    /// </summary>
    public sealed class PromptChain
    {
        readonly IReadOnlyList<IPromptStage> _stages;

        public PromptChain(IReadOnlyList<IPromptStage> stages)
        {
            _stages = stages ?? new List<IPromptStage>();
        }

        /// <summary>Runs the chain and returns the final HaptiGen prompt plus the full trace.</summary>
        public async Task<PromptState> RunAsync(HapticContext context, CancellationToken ct)
        {
            var state = new PromptState(context);
            for (int i = 0; i < _stages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await _stages[i].RunAsync(state, ct);
            }
            return state;
        }
    }
}

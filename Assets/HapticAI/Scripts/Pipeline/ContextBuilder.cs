using System.Collections.Generic;
using HapticAI.Core;

namespace HapticAI.Pipeline
{
    /// <summary>
    /// Runs an ordered list of <see cref="IContextContributor"/> over a <see cref="HapticEvent"/>
    /// to produce a <see cref="HapticContext"/>. The list is the single place that decides what
    /// environmental information reaches the prompt chain.
    /// </summary>
    public sealed class ContextBuilder
    {
        readonly IReadOnlyList<IContextContributor> _contributors;

        public ContextBuilder(IReadOnlyList<IContextContributor> contributors)
        {
            _contributors = contributors ?? new List<IContextContributor>();
        }

        public HapticContext Build(HapticEvent evt)
        {
            var ctx = new HapticContext(evt);
            for (int i = 0; i < _contributors.Count; i++)
                _contributors[i]?.Contribute(evt, ctx);
            return ctx;
        }
    }
}

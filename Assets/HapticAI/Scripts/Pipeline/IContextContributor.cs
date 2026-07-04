using HapticAI.Core;

namespace HapticAI.Pipeline
{
    /// <summary>
    /// One modular unit of "what does the environment look like right now". Each contributor
    /// reads whatever cues it understands off the <see cref="HapticEvent"/> and appends
    /// descriptors/fields to the <see cref="HapticContext"/>. Fold in new information later by
    /// adding a contributor to the list — nothing else changes.
    /// </summary>
    public interface IContextContributor
    {
        void Contribute(HapticEvent evt, HapticContext ctx);
    }
}

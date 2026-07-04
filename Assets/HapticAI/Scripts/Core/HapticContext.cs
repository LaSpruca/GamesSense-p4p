using System.Collections.Generic;
using System.Text;

namespace HapticAI.Core
{
    /// <summary>
    /// The accumulated understanding of a <see cref="HapticEvent"/> after every
    /// <see cref="Pipeline.IContextContributor"/> has run. <see cref="Descriptors"/> holds the
    /// natural-language phrases fed to the prompt chain; <see cref="Fields"/> holds structured
    /// values a stage template can reference directly.
    /// </summary>
    public sealed class HapticContext
    {
        public readonly HapticEvent Event;
        public readonly List<string> Descriptors = new List<string>();
        public readonly Dictionary<string, string> Fields = new Dictionary<string, string>();

        public HapticContext(HapticEvent evt) => Event = evt;

        public string EventLabel => Event.Name;

        public void AddDescriptor(string phrase)
        {
            if (!string.IsNullOrWhiteSpace(phrase)) Descriptors.Add(phrase.Trim());
        }

        public void SetField(string key, string value) => Fields[key] = value;

        /// <summary>Human-readable summary of the cues, joined for the first prompt stage.</summary>
        public string ToBrief()
        {
            var sb = new StringBuilder();
            sb.Append("Event: ").Append(EventLabel);
            foreach (var d in Descriptors)
                sb.Append("; ").Append(d);
            return sb.ToString();
        }
    }
}

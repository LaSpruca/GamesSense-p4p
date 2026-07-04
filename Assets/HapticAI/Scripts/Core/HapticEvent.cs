using System.Collections.Generic;

namespace HapticAI.Core
{
    public enum HapticEventType
    {
        Ignite,
        Spread,
        Extinguish,
        SprayTick,
        Custom,
    }

    public enum HapticHand
    {
        Left,
        Right,
    }

    /// <summary>
    /// A request for a haptic response, carrying whatever environmental cues are available at
    /// the moment it fires. Cues are intentionally open-ended bags (<see cref="Numbers"/> /
    /// <see cref="Tags"/>) rather than fixed fields: adding a new signal later means writing a
    /// new <see cref="Pipeline.IContextContributor"/> that reads a new key — no change to this
    /// type or to the runtime that raises events.
    /// </summary>
    public sealed class HapticEvent
    {
        public HapticEventType Type;
        /// <summary>Name used when <see cref="Type"/> is <see cref="HapticEventType.Custom"/>.</summary>
        public string CustomName;
        public HapticHand Hand = HapticHand.Right;

        /// <summary>Numeric cues, e.g. "intensity", "distance", "triggerDepth", "sprayDuration".</summary>
        public readonly Dictionary<string, float> Numbers = new Dictionary<string, float>();
        /// <summary>Categorical cues, e.g. "surface" = "TABLE".</summary>
        public readonly Dictionary<string, string> Tags = new Dictionary<string, string>();

        public HapticEvent(HapticEventType type) => Type = type;

        public static HapticEvent Custom(string name) =>
            new HapticEvent(HapticEventType.Custom) { CustomName = name };

        public string Name => Type == HapticEventType.Custom ? (CustomName ?? "custom") : Type.ToString();

        public HapticEvent WithNumber(string key, float value) { Numbers[key] = value; return this; }
        public HapticEvent WithTag(string key, string value) { Tags[key] = value; return this; }

        public float Number(string key, float fallback = 0f) =>
            Numbers.TryGetValue(key, out var v) ? v : fallback;
        public string Tag(string key, string fallback = null) =>
            Tags.TryGetValue(key, out var v) ? v : fallback;
    }
}

namespace HapticAI.Config
{
    /// <summary>
    /// Which haptics the experience is running - the A/B condition of the study.
    ///
    /// Lives in the Core assembly rather than next to the session that switches it, because the
    /// setup scene persists it (see <see cref="StudySettings"/>) and the persistence layer cannot
    /// reference Assembly-CSharp. The numeric values are serialised into PlayerPrefs and into the
    /// scene's inspector data, so do not reorder them.
    /// </summary>
    public enum HapticsMode
    {
        /// <summary>Fixed scalar rumble on the trigger, and nothing for fire events. The baseline.</summary>
        Static = 0,
        /// <summary>The AI pipeline: cues and spray both come from generated waveforms.</summary>
        Ai = 1,
    }
}

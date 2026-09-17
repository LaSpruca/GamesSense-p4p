namespace HapticAI.Scene
{
    /// <summary>
    /// The two scenes in the build, by name. They are loaded by name from both directions - setup
    /// starts training, training goes back to setup between participants - so the strings are
    /// written once here rather than in each caller.
    ///
    /// Both must be listed in File > Build Profiles (Scenes In Build), with <see cref="Setup"/>
    /// first: index 0 is what the app opens on the headset.
    /// </summary>
    public static class StudyScenes
    {
        public const string Setup = "Setup";
        public const string Training = "FireTraining";
    }
}

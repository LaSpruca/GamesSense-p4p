namespace HapticAI.Core
{
    /// <summary>
    /// Deterministic string hashing. <see cref="System.String.GetHashCode"/> is randomised per
    /// process in modern .NET, which would make cache keys and the mock synth non-repeatable
    /// across runs; this FNV-1a hash is stable, which the cache and <c>MockHapticSynth</c> rely on.
    /// </summary>
    public static class Hashing
    {
        public static uint Fnv1a(string s)
        {
            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;
                uint hash = offset;
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        hash ^= s[i];
                        hash *= prime;
                    }
                }
                return hash;
            }
        }
    }
}

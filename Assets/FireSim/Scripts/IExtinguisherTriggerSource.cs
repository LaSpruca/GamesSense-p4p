namespace FireSim
{
    /// <summary>
    /// A replacement for the Quest controller's analog index trigger, so <see cref="FoamSprayer"/>
    /// can be driven by something else (e.g. the ESP32 custom controller's force sensor) without
    /// knowing what that something is.
    /// </summary>
    public interface IExtinguisherTriggerSource
    {
        /// <summary>
        /// Returns the current trigger pull, 0-1. False means "no reading yet" (e.g. the source
        /// hasn't connected), in which case the caller should not treat it as fully released.
        /// </summary>
        bool TryGetTrigger(out float trigger);
    }
}

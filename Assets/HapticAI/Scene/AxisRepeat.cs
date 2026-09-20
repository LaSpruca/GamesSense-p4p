using UnityEngine;

namespace HapticAI.Scene
{
    /// <summary>
    /// One axis of the thumbstick turned into discrete steps: one on the first push, then a
    /// repeat that speeds up while held. Without the acceleration, setting an octet to 200 is
    /// two hundred deliberate flicks.
    ///
    /// Shared by the two screens a stick drives a list on rather than a character: the setup
    /// console and the pause menu. It is deliberately time-agnostic - the caller passes the clock
    /// it wants, which is how the pause menu keeps working on unscaled time while the drill
    /// behind it is frozen at timeScale 0.
    /// </summary>
    sealed class AxisRepeat
    {
        const float Deadzone = 0.5f;
        const float ReleaseZone = 0.35f;   // lower than Deadzone: hysteresis, so a wobbling
                                           // stick near the threshold does not stutter.
        const float FirstRepeatDelay = 0.4f;
        const float RepeatDelay = 0.12f;
        const float FastAfterSeconds = 1.2f;
        const float FastRepeatDelay = 0.03f;

        int _direction;
        float _heldSince;
        float _nextRepeatAt;

        public int Step(float value, float now)
        {
            int direction = value > Deadzone ? 1 : value < -Deadzone ? -1 : 0;

            if (direction == 0 && Mathf.Abs(value) < ReleaseZone)
            {
                _direction = 0;
                return 0;
            }
            if (direction == 0) return 0;   // inside the hysteresis band: hold the current state

            if (direction != _direction)
            {
                _direction = direction;
                _heldSince = now;
                _nextRepeatAt = now + FirstRepeatDelay;
                return direction;
            }

            if (now < _nextRepeatAt) return 0;
            _nextRepeatAt = now + (now - _heldSince > FastAfterSeconds ? FastRepeatDelay : RepeatDelay);
            return direction;
        }
    }
}

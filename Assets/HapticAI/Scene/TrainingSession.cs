using System.Collections.Generic;
using FireSim;
using FireSim.UI;
using HapticAI.Config;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HapticAI.Scene
{
    /// <summary>
    /// Runs the experience end to end: briefing, run, result. It exists mostly because the fire
    /// used to start the instant the room finished loading, which meant it started while the user
    /// was still working out what to do - and then simply stopped, with nothing to say they had
    /// finished.
    ///
    /// It also owns the haptics A/B switch. Both conditions live in one scene so a participant can
    /// be flipped between them without a rebuild or a scene load, and so the two never drift apart
    /// the way two hand-maintained scenes do.
    /// </summary>
    public sealed class TrainingSession : MonoBehaviour
    {
        public enum Phase { Loading, Briefing, Running, Complete, Failed }

        [Tooltip("Seconds to wait for a room scan before telling the user something is wrong.")]
        public float roomTimeoutSeconds = 25f;

        SurfaceFireManager _fire;
        FoamSprayer _sprayer;
        FireHapticRelay _relay;
        TrainingPanel _panel;
        HapticPlayer _player;
        StudySettings _settings;

        string _hapticSource = "(resolving)";
        bool _hapticsWarm;
        float _loadStartedAt;
        string _loadingBody;
        float _runStartedAt;
        float _runSeconds;

        public Phase Current { get; private set; } = Phase.Loading;
        public HapticsMode Mode { get; private set; } = HapticsMode.Ai;

        public void Initialise(SurfaceFireManager fire, FoamSprayer sprayer, FireHapticRelay relay,
            HapticPlayer player, Transform head, HapticsMode initialMode, StudySettings settings)
        {
            _fire = fire;
            _sprayer = sprayer;
            _relay = relay;
            _player = player;
            _settings = settings ?? StudySettings.Current;
            _panel = TrainingPanel.Create(head);
            Mode = initialMode;
            _loadStartedAt = Time.time;

            _fire.RoomBecameReady += OnRoomReady;
            _fire.AllExtinguished += OnAllExtinguished;

            ApplyMode();
            EnterLoading();
        }

        void OnDestroy()
        {
            if (_fire == null) return;
            _fire.RoomBecameReady -= OnRoomReady;
            _fire.AllExtinguished -= OnAllExtinguished;
        }

        /// <summary>Called by the bootstrap once the haptics stack is built and the cache warmed.</summary>
        public void SetHapticsReady(string sourceDescription)
        {
            _hapticSource = sourceDescription;
            _hapticsWarm = true;
            if (Current == Phase.Loading) TryEnterBriefing();
        }

        void OnRoomReady()
        {
            if (Current == Phase.Loading) TryEnterBriefing();
        }

        void Update()
        {
            if (_fire == null) return;
            switch (Current)
            {
                case Phase.Loading:
                    if (Time.time - _loadStartedAt > roomTimeoutSeconds && !_fire.RoomReady) EnterRoomFailure();
                    else EnterLoading();   // cheap: only redraws when a progress line actually changes
                    break;

                case Phase.Briefing:
                    if (BackPressed()) { ReturnToSetup(); }
                    else if (ConfirmPressed()) StartRun();
                    else if (TogglePressed() && CanSwitchMode) { CycleMode(); EnterBriefing(); }
                    break;

                case Phase.Running:
                    _runSeconds = Time.time - _runStartedAt;
                    break;

                case Phase.Complete:
                    if (BackPressed()) { ReturnToSetup(); }
                    else if (ConfirmPressed()) StartRun();
                    else if (TogglePressed() && CanSwitchMode) { CycleMode(); EnterComplete(); }
                    break;

                case Phase.Failed:
                    if (BackPressed()) { ReturnToSetup(); }
                    else if (ConfirmPressed() && _fire.RoomReady) StartRun();
                    break;
            }
        }

        // --- Phases -------------------------------------------------------------------------

        void EnterLoading()
        {
            Current = Phase.Loading;
            _sprayer.Armed = false;

            string body = "Getting ready...\n\n" +
                          $"  Room scan          {(_fire.RoomReady ? "ready" : "scanning...")}\n" +
                          $"  Haptics            {(_hapticsWarm ? "ready" : "preparing...")}\n" +
                          $"  Source             {_hapticSource}";
            if (body == _loadingBody) return;   // called every frame; only redraw on a real change.
            _loadingBody = body;

            _panel.Show("Fire Extinguisher Training", body,
                "This takes a few seconds the first time.");
        }

        void TryEnterBriefing()
        {
            if (!_fire.RoomReady || !_hapticsWarm) return;
            EnterBriefing();
        }

        void EnterBriefing()
        {
            Current = Phase.Briefing;
            _sprayer.Armed = false;
            _panel.Show($"Fire Extinguisher Training - {ParticipantId}",
                "A fire will start somewhere in this room, and it will spread.\n" +
                "Your job is to put every bit of it out.\n\n" +
                "   1.  Hold the RIGHT TRIGGER to spray foam.\n" +
                "   2.  Aim at the BASE of the flames. Foam hitting the top\n" +
                "        of a fire barely does anything.\n" +
                "   3.  Stay on each fire until it is out. Leave one half-doused\n" +
                "        and it will build itself back up.\n" +
                "   4.  Fires spread every few seconds. Do not fall behind.\n\n" +
                $"Haptics: {ModeLabel(Mode)}\n" +
                $"Source:  {_hapticSource}{HapticsAvailabilityNote()}",
                Footer("A  start"));
        }

        void StartRun()
        {
            // Before Begin(), not after: igniting raises the first fire event synchronously, and if
            // the relay were still muted from the previous condition that cue would be dropped.
            ApplyMode();

            if (!_fire.Begin())
            {
                EnterSurfaceFailure();
                return;
            }

            Current = Phase.Running;
            _runStartedAt = Time.time;
            _runSeconds = 0f;
            _sprayer.Armed = true;
            _panel.Hide();
        }

        void OnAllExtinguished()
        {
            if (Current != Phase.Running) return;
            _runSeconds = Time.time - _runStartedAt;

            // Logged here rather than in EnterComplete, which is also re-entered when the condition
            // is switched on the result screen - that would write the same run to the log twice.
            StudyLog.AppendRun(_settings, Mode, _runSeconds, _fire, _relay, _hapticSource);
            EnterComplete();
        }

        void EnterComplete()
        {
            Current = Phase.Complete;
            // Deliberately no StopAll here. The relay handles AllExtinguished first: it stops the
            // spray loop and starts the completion cue, and silencing the player now would cut that
            // cue off before it played.
            _sprayer.Armed = false;

            var rows = new List<(string, string)>
            {
                ("Time taken", FormatDuration(_runSeconds)),
                ("Fires extinguished", _fire.TotalExtinguished.ToString()),
                ("Fires at their worst", _fire.PeakBurning.ToString()),
                ("Haptics", ModeLabel(Mode)),
            };
            if (_relay != null && Mode == HapticsMode.Ai)
                rows.Add(("Clips generated / fallback", $"{_relay.GeneratedCount} / {_relay.FallbackCount}"));

            _panel.ShowStats($"Fire Out - Well Done ({ParticipantId})", rows,
                Footer("A  run it again"));
        }

        void EnterRoomFailure()
        {
            Current = Phase.Failed;
            _sprayer.Armed = false;
            _panel.Show("No Room Scan Found",
                "This experience places fire on the real surfaces around you, so it\n" +
                "needs a scan of this room.\n\n" +
                "On the headset: Settings > Physical Space > Space Setup, then\n" +
                "restart this app.",
                "Waiting for a room...     Y  setup");
        }

        void EnterSurfaceFailure()
        {
            Current = Phase.Failed;
            _sprayer.Armed = false;
            _panel.Show("Nothing To Burn",
                "The room scan came through, but none of its surfaces are ones fire\n" +
                "is allowed to start on. Re-run Space Setup and make sure the floor\n" +
                "and at least one table or surface are captured.",
                "A  try again     Y  setup");
        }

        // --- Mode ---------------------------------------------------------------------------

        /// <summary>
        /// Whether the B button does anything. The setup screen locks it for a study run: the
        /// button that switches condition sits next to the one that starts a run, and a participant
        /// who flips condition halfway through has produced data for neither.
        /// </summary>
        bool CanSwitchMode => _settings == null || !_settings.lockCondition;

        void CycleMode()
        {
            Mode = Mode == HapticsMode.Ai ? HapticsMode.Static : HapticsMode.Ai;

            // Keep the saved settings honest: if the condition was switched here, that is the
            // condition this headset is now running, and the setup screen must say so.
            if (_settings != null)
            {
                _settings.condition = Mode;
                _settings.Save();
            }
        }

        /// <summary>Back to the setup screen, between participants.</summary>
        void ReturnToSetup()
        {
            _sprayer.Armed = false;
            _fire.Abort();
            _player?.StopAll();
            SceneManager.LoadScene(StudyScenes.Setup);
        }

        void ApplyMode()
        {
            bool ai = Mode == HapticsMode.Ai;
            // Exactly one system drives the controller at a time: the sprayer's fixed rumble, or
            // the AI relay's generated clips. Both at once would fight over the same actuator.
            _sprayer.useBuiltInHaptics = !ai;
            _relay?.SetMuted(!ai);
            Debug.Log($"[FireSim] Haptics mode: {ModeLabel(Mode)}");
        }

        string ParticipantId => _settings != null ? _settings.ParticipantId : "no participant set";

        /// <summary>
        /// The button line, which is not fixed: B is absent while the condition is locked, because
        /// a screen that offers a button that does nothing is worse than one that offers neither.
        /// </summary>
        string Footer(string primary) =>
            CanSwitchMode
                ? $"{primary}     B  switch haptics mode     Y  setup"
                : $"{primary}     Y  setup";

        static string ModeLabel(HapticsMode mode) =>
            mode == HapticsMode.Ai ? "AI-generated (dynamic)" : "Fixed rumble (static baseline)";

        string HapticsAvailabilityNote() =>
            _player != null && !_player.IsAvailable
                ? "\n         (no haptics-capable controller detected - nothing will be felt)"
                : string.Empty;

        static string FormatDuration(float seconds)
        {
            int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{total / 60}:{total % 60:00}";
        }

        // --- Input --------------------------------------------------------------------------
        // A confirms, B switches condition, Y (left hand) goes back to setup. The keyboard mirrors
        // are for Editor testing without controllers, where OVRInput reports nothing at all.

        static bool ConfirmPressed() =>
            OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) || KeyPressed(KeyRole.Confirm);

        static bool TogglePressed() =>
            OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) || KeyPressed(KeyRole.Toggle);

        /// <summary>
        /// Y on the LEFT controller: far from A and B, so the operator taking the headset back
        /// cannot be confused with the participant starting another run.
        /// </summary>
        static bool BackPressed() =>
            OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch) || KeyPressed(KeyRole.Back);

        enum KeyRole { Confirm, Toggle, Back }

        static bool KeyPressed(KeyRole key)
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard == null) return false;
            switch (key)
            {
                case KeyRole.Confirm: return keyboard.spaceKey.wasPressedThisFrame;
                case KeyRole.Toggle: return keyboard.tabKey.wasPressedThisFrame;
                case KeyRole.Back: return keyboard.escapeKey.wasPressedThisFrame;
            }
#endif
            return false;
        }
    }
}

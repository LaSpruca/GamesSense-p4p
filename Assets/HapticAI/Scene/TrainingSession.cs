using System.Collections.Generic;
using System.Text;
using FireSim;
using FireSim.UI;
using HapticAI.Config;
using HapticAI.Profiles;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HapticAI.Scene
{
    /// <summary>
    /// Runs the drill end to end as a series of stages: briefing → run → "stage complete" → next
    /// briefing, and a final summary after the last one. Each stage adds one demand on top of the
    /// previous (aim, sweep, re-ignition, then everything at once), so the stage that trips a
    /// trainee up names the habit they still need. See <see cref="TrainingStages"/>.
    ///
    /// It also owns the haptics A/B switch. Both conditions live in one scene so a participant can
    /// be flipped between them without a rebuild or a scene load, and so the two never drift apart
    /// the way two hand-maintained scenes do.
    /// </summary>
    public sealed class TrainingSession : MonoBehaviour
    {
        public enum Phase { Loading, HapticsGuide, Briefing, Running, StageComplete, AllComplete, Failed, Paused }

        /// <summary>Rows of the pause menu, in the order they are drawn.</summary>
        enum PauseRow { Resume, RestartStage, StartOver, Setup }

        const int PauseRowCount = (int)PauseRow.Setup + 1;

        [Tooltip("Seconds to wait for a room scan before telling the user something is wrong.")]
        public float roomTimeoutSeconds = 25f;

        SurfaceFireManager _fire;
        FoamSprayer _sprayer;
        FireHapticRelay _relay;
        HapticProfileLibrary _library;
        TrainingPanel _panel;
        HapticPlayer _player;
        StudySettings _settings;

        string _hapticSource = "(resolving)";
        bool _hapticsReady;
        float _loadStartedAt;
        string _lastBody;
        float _runStartedAt;
        float _runSeconds;
        int _stageIndex = 1;
        bool _hapticsGuideShown;
        Phase _pausedFrom;
        int _pauseSelected;
        readonly AxisRepeat _pauseVertical = new AxisRepeat();
        readonly List<StageResult> _results = new List<StageResult>();

        public Phase Current { get; private set; } = Phase.Loading;
        public HapticsMode Mode { get; private set; } = HapticsMode.Ai;
        public TrainingStage Stage => TrainingStages.Get(_stageIndex);

        struct StageResult
        {
            public TrainingStage Stage;
            public float Seconds;
            public int Extinguished;
            public int Reignitions;
            public float BaseHitFraction;
        }

        public void Initialise(SurfaceFireManager fire, FoamSprayer sprayer, FireHapticRelay relay,
            HapticProfileLibrary library, HapticPlayer player, Transform head, HapticsMode initialMode,
            StudySettings settings)
        {
            _fire = fire;
            _sprayer = sprayer;
            _relay = relay;
            _library = library;
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
            // Whatever loads next must not inherit a frozen clock.
            if (Current == Phase.Paused) Time.timeScale = 1f;
            if (_fire == null) return;
            _fire.RoomBecameReady -= OnRoomReady;
            _fire.AllExtinguished -= OnAllExtinguished;
        }

        /// <summary>
        /// Called by the bootstrap once a palette has been adopted (server-planned or on-device).
        /// Clips may still be streaming in; the briefing shows their progress and the relay plays
        /// procedural stand-ins for any that have not landed.
        /// </summary>
        public void SetHapticsReady(string sourceDescription)
        {
            _hapticSource = sourceDescription;
            _hapticsReady = true;
            if (Current == Phase.Loading) TryEnterBriefing();
        }

        /// <summary>The bootstrap updates this when the palette source changes (e.g. socket reconnects).</summary>
        public void UpdateHapticSource(string sourceDescription)
        {
            _hapticSource = sourceDescription;
        }

        void OnRoomReady()
        {
            if (Current == Phase.Loading) TryEnterBriefing();
        }

        void Update()
        {
            if (_fire == null) return;

            if (Current == Phase.Paused) { UpdatePause(); return; }
            if (CanPause && PausePressed()) { EnterPause(); return; }

            switch (Current)
            {
                case Phase.Loading:
                    if (Time.time - _loadStartedAt > roomTimeoutSeconds && !_fire.RoomReady) EnterRoomFailure();
                    else EnterLoading();   // cheap: only redraws when a progress line actually changes
                    break;

                case Phase.HapticsGuide:
                    if (BackPressed()) ReturnToSetup();
                    else if (ConfirmPressed()) { _hapticsGuideShown = true; EnterBriefing(); }
                    break;

                case Phase.Briefing:
                    if (BackPressed()) ReturnToSetup();
                    else if (ConfirmPressed()) StartRun();
                    else if (TogglePressed() && CanSwitchMode) { CycleMode(); EnterBriefing(); }
                    else EnterBriefing();  // redraws only when the palette progress line changes
                    break;

                case Phase.Running:
                    _runSeconds = Time.time - _runStartedAt;
                    break;

                case Phase.StageComplete:
                    if (BackPressed()) ReturnToSetup();
                    else if (ConfirmPressed()) { _stageIndex++; EnterBriefing(); }
                    else if (TogglePressed() && CanSwitchMode) { CycleMode(); EnterStageComplete(); }
                    break;

                case Phase.AllComplete:
                    if (BackPressed()) ReturnToSetup();
                    else if (ConfirmPressed()) { _stageIndex = 1; _results.Clear(); EnterBriefing(); }
                    else if (TogglePressed() && CanSwitchMode) { CycleMode(); EnterAllComplete(); }
                    break;

                case Phase.Failed:
                    if (BackPressed()) ReturnToSetup();
                    else if (ConfirmPressed() && _fire.RoomReady) StartRun();
                    break;
            }
        }

        // --- Pause --------------------------------------------------------------------------

        /// <summary>
        /// Phases the menu button opens from. Loading is excluded because there is nothing yet to
        /// pause or restart, and Failed because its screens are already a recovery prompt.
        /// </summary>
        bool CanPause =>
            Current == Phase.Briefing || Current == Phase.Running ||
            Current == Phase.StageComplete || Current == Phase.AllComplete;

        /// <summary>
        /// Freeze the drill and put the menu up. timeScale is what does the freezing: the fire
        /// spreads on coroutines and the spots burn down on scaled deltas, so zeroing it stops all
        /// of them at once rather than each needing to learn a paused flag of its own. The run
        /// timer is scaled time too, so paused seconds never reach the log.
        /// </summary>
        void EnterPause()
        {
            _pausedFrom = Current;
            Current = Phase.Paused;
            _pauseSelected = 0;

            _sprayer.Armed = false;   // a trigger held through the menu must not keep spraying
            _player?.StopAll();       // and nothing is left buzzing in the hand
            // The relay is not on the frozen clock: its smoulder warning reschedules itself on
            // unscaled time, so without muting it the controller keeps buzzing while paused.
            _relay?.SetMuted(true);
            Time.timeScale = 0f;

            RenderPause();
        }

        void UpdatePause()
        {
            // Unscaled: Time.time does not move while paused, so a scaled clock would freeze the
            // stick repeat along with the fire.
            int vertical = _pauseVertical.Step(ReadStickY(), Time.unscaledTime);

            if (PausePressed()) { Resume(); return; }          // the button that opened it closes it
            if (ConfirmPressed()) { Activate((PauseRow)_pauseSelected); return; }

            if (vertical == 0) return;
            _pauseSelected = (_pauseSelected - vertical + PauseRowCount) % PauseRowCount;
            RenderPause();
        }

        void Activate(PauseRow row)
        {
            switch (row)
            {
                case PauseRow.Resume: Resume(); break;
                case PauseRow.RestartStage: RestartFrom(_stageIndex); break;
                case PauseRow.StartOver: RestartFrom(1); break;
                case PauseRow.Setup:
                    Unfreeze();
                    ReturnToSetup();
                    break;
            }
        }

        /// <summary>Back to whichever screen the menu was opened over.</summary>
        void Resume()
        {
            Unfreeze();
            ApplyMode();   // unmutes the relay that EnterPause silenced
            switch (_pausedFrom)
            {
                case Phase.Running:
                    Current = Phase.Running;
                    _sprayer.Armed = true;
                    _panel.Hide();
                    break;
                case Phase.StageComplete: EnterStageComplete(); break;
                case Phase.AllComplete: EnterAllComplete(); break;
                default: EnterBriefing(); break;
            }
        }

        /// <summary>
        /// Abandon the run in progress and go back to a briefing - the same path for "restart this
        /// stage" and "start over", which differ only in where they land and what they keep.
        /// </summary>
        void RestartFrom(int stageIndex)
        {
            Unfreeze();
            ApplyMode();
            _fire.Abort();
            _player?.StopAll();
            _sprayer.Armed = false;
            _runSeconds = 0f;

            _stageIndex = Mathf.Max(1, stageIndex);
            // Drop the results this restart is about to redo. Without it, restarting a stage from
            // its own "stage complete" screen - which is exactly when someone notices a run went
            // wrong - leaves the finished attempt in the list and the summary reports that stage
            // twice. The run log is append-only and still keeps both, which is what it is for.
            _results.RemoveAll(r => r.Stage.Index >= _stageIndex);

            EnterBriefing();
        }

        void Unfreeze() => Time.timeScale = 1f;

        void RenderPause()
        {
            var stage = Stage;
            var rows = new List<MenuRow>(PauseRowCount)
            {
                PauseLine(PauseRow.Resume, "Resume",
                    _pausedFrom == Phase.Running ? "back to the drill" : "back to the screen"),
                PauseLine(PauseRow.RestartStage, "Restart this stage", $"stage {stage.Index}, {stage.Name}"),
                PauseLine(PauseRow.StartOver, "Start over", "from stage 1"),
                PauseLine(PauseRow.Setup, "Back to setup", "between participants"),
            };

            _panel.ShowMenu($"Paused - {ParticipantId}", rows, PauseNote(),
                "Stick  move      A  select      Menu  resume");
        }

        MenuRow PauseLine(PauseRow row, string label, string value) =>
            new MenuRow(label, value, _pauseSelected == (int)row);

        string PauseNote()
        {
            switch ((PauseRow)_pauseSelected)
            {
                case PauseRow.RestartStage:
                    return "Puts this stage back to its briefing. Stages already finished keep " +
                           "their results, and the abandoned attempt is not logged.";
                case PauseRow.StartOver:
                    return "Back to stage 1 and clears the results collected so far. This is the " +
                           "one to use when a run has to be thrown away.";
                case PauseRow.Setup:
                    return "Leaves the experience for the setup screen, to change participant or " +
                           "condition.";
                default:
                    return _pausedFrom == Phase.Running
                        ? "The fire is frozen where it was and the timer is stopped; nothing you " +
                          "do here is recorded against the run."
                        : "Nothing is running - this menu was opened from a briefing or a result " +
                          "screen.";
            }
        }

        // --- Phases -------------------------------------------------------------------------

        void EnterLoading()
        {
            Current = Phase.Loading;
            _sprayer.Armed = false;

            string body = "Getting ready...\n\n" +
                          $"  Room scan          {(_fire.RoomReady ? "ready" : "scanning...")}\n" +
                          $"  Haptics            {(_hapticsReady ? "ready" : "planning...")}";
            if (ShowDiagnostics) body += $"\n  Source             {_hapticSource}";
            if (body == _lastBody) return;   // called every frame; only redraw on a real change.
            _lastBody = body;

            _panel.Show("Fire Extinguisher Training", body,
                "This takes a few seconds the first time.");
        }

        void TryEnterBriefing()
        {
            if (!_fire.RoomReady || !_hapticsReady) return;
            EnterBriefing();
        }

        void EnterBriefing()
        {
            // The dynamic haptics are a vocabulary the trainee has to know before it can help them,
            // so it is explained once, ahead of the first briefing in that condition - including
            // when the condition is switched to it partway through.
            if (Mode == HapticsMode.Ai && !_hapticsGuideShown)
            {
                EnterHapticsGuide();
                return;
            }

            var stage = Stage;
            var sb = new StringBuilder();
            sb.Append(stage.Objective).Append("\n\n");
            foreach (var note in stage.TechniqueNotes)
                sb.Append("   -  ").Append(note).Append('\n');
            sb.Append('\n');
            sb.Append("Haptics: ").Append(ModeLabel(Mode));
            if (ShowDiagnostics)
            {
                sb.Append('\n');
                sb.Append("Source:  ").Append(_hapticSource).Append(HapticsAvailabilityNote()).Append('\n');
                sb.Append("Palette: ").Append(PaletteProgress());
            }

            string body = sb.ToString();
            bool redraw = Current != Phase.Briefing || body != _lastBody;
            Current = Phase.Briefing;
            _sprayer.Armed = false;
            if (!redraw) return;
            _lastBody = body;

            _panel.Show($"Stage {stage.Index} of {TrainingStages.Count} - {stage.Name} ({ParticipantId})", body,
                Footer("A  start"));
        }

        /// <summary>
        /// What each dynamic-haptics pattern means. Must stay in step with what the AI condition
        /// actually plays: <see cref="HapticAI.Profiles.HapticProfileSelector.TechniquePulseHz"/> for
        /// the spray, and the taps in <see cref="FireHapticRelay"/>.
        /// </summary>
        void EnterHapticsGuide()
        {
            if (Current == Phase.HapticsGuide) return;
            Current = Phase.HapticsGuide;
            _sprayer.Armed = false;
            _lastBody = null;

            var rows = new List<MenuRow>
            {
                new MenuRow("Pin pulled", "one short tap", false),
                new MenuRow("Spraying the base of the fire", "steady, strong buzz", false),
                new MenuRow("Slightly off target", "slow pulsing", false),
                new MenuRow("Way off target", "fast pulsing", false),
                new MenuRow("Fire knocked down", "double tap", false),
            };
            _panel.ShowMenu($"What You'll Feel - the extinguisher tells you how you're doing ({ParticipantId})",
                rows,
                "A steady buzz means keep going. Pulsing means re-aim at the base of the flames - " +
                "the faster it pulses, the further off you are.",
                "A  got it     Y  setup");
        }

        void StartRun()
        {
            // Before Begin(), not after: igniting raises the first fire event synchronously, and if
            // the relay were still muted from the previous condition that cue would be dropped.
            ApplyMode();

            if (!_fire.Begin(Stage))
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

            // Logged here rather than in EnterStageComplete, which is also re-entered when the
            // condition is switched on the result screen - that would write the same run twice.
            StudyLog.AppendStage(_settings, Mode, Stage, _runSeconds, _fire, _relay, _hapticSource);
            _results.Add(new StageResult
            {
                Stage = Stage,
                Seconds = _runSeconds,
                Extinguished = _fire.TotalExtinguished,
                Reignitions = _fire.TotalReignited,
                BaseHitFraction = _fire.BaseHitFraction,
            });

            if (_stageIndex >= TrainingStages.Count) EnterAllComplete();
            else EnterStageComplete();
        }

        /// <summary>
        /// The "you're done with this stage" notice. Deliberately no StopAll here: the relay handles
        /// AllExtinguished first and starts the stage-complete cue, and silencing the player now
        /// would cut that cue off before it played.
        /// </summary>
        void EnterStageComplete()
        {
            Current = Phase.StageComplete;
            _sprayer.Armed = false;

            var stage = Stage;
            var next = TrainingStages.Get(_stageIndex + 1);
            var rows = StageRows(stage);
            rows.Add(("Haptics", ModeLabel(Mode)));
            if (ShowDiagnostics && _relay != null && Mode == HapticsMode.Ai)
                rows.Add(("Clips generated / stand-in", $"{_relay.GeneratedCount} / {_relay.FallbackCount}"));

            _panel.ShowStats($"Stage {stage.Index} Complete - {stage.Name}", rows,
                Footer($"A  next: stage {next.Index}, {next.Name}"));
        }

        void EnterAllComplete()
        {
            Current = Phase.AllComplete;
            _sprayer.Armed = false;

            var rows = new List<(string, string)>();
            float total = 0f;
            foreach (var r in _results)
            {
                total += r.Seconds;
                rows.Add(($"Stage {r.Stage.Index}  {r.Stage.Name}",
                    $"{FormatDuration(r.Seconds)}   aim {Mathf.RoundToInt(r.BaseHitFraction * 100f)}%" +
                    (r.Stage.ReigniteEnabled ? $"   re-lit {r.Reignitions}" : string.Empty)));
            }
            rows.Add(("Total time", FormatDuration(total)));
            rows.Add(("Haptics", ModeLabel(Mode)));

            _panel.ShowStats($"All Stages Complete - Well Done ({ParticipantId})", rows,
                Footer("A  start again from stage 1"));
        }

        List<(string, string)> StageRows(TrainingStage stage)
        {
            var rows = new List<(string, string)>
            {
                ("Time taken", FormatDuration(_runSeconds)),
                ("Fires put out", _fire.TotalExtinguished.ToString()),
                ("Fires at their worst", _fire.PeakBurning.ToString()),
                ("Foam on the base", $"{Mathf.RoundToInt(_fire.BaseHitFraction * 100f)}%"),
            };
            if (stage.ReigniteEnabled) rows.Add(("Flared back up", _fire.TotalReignited.ToString()));
            return rows;
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

        /// <summary>
        /// Whether the technical lines are drawn. Off for a participant run - see
        /// <see cref="StudySettings.showDiagnostics"/>. Null settings show them, which is the
        /// developer case.
        /// </summary>
        bool ShowDiagnostics => _settings == null || _settings.showDiagnostics;

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
            // the AI relay's profile clips. Both at once would fight over the same actuator.
            _sprayer.useBuiltInHaptics = !ai;
            _relay?.SetMuted(!ai);
            Debug.Log($"[FireSim] Haptics mode: {ModeLabel(Mode)}");
        }

        string ParticipantId => _settings != null ? _settings.ParticipantId : "no participant set";

        string PaletteProgress()
        {
            if (_library == null || !_library.HasManifest) return "planning...";
            int n = _library.ClipCount, total = _library.ProfileCount;
            string state = n >= total ? "all clips ready" : $"{n} of {total} clips ready, stand-ins for the rest";
            return $"{state} ({_library.Source})";
        }

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

        /// <summary>
        /// The Menu button, which only the left controller has - the right one's equivalent is the
        /// Oculus button and belongs to the system, not to us.
        /// </summary>
        static bool PausePressed() =>
            OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch) || KeyPressed(KeyRole.Pause);

        /// <summary>Either stick's vertical axis, plus the arrow keys for the Editor.</summary>
        static float ReadStickY()
        {
            float stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick).y +
                          OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).y;
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard != null)
                stick += (keyboard.upArrowKey.isPressed ? 1f : 0f) -
                         (keyboard.downArrowKey.isPressed ? 1f : 0f);
#endif
            return stick;
        }

        enum KeyRole { Confirm, Toggle, Back, Pause }

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
                case KeyRole.Pause: return keyboard.pKey.wasPressedThisFrame;
            }
#endif
            return false;
        }
    }
}

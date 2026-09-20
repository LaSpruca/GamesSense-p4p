using FireSim;
using HapticAI.Core;
using HapticAI.Net;
using HapticAI.Profiles;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HapticAI.Scene
{
    /// <summary>
    /// Bridges the fire simulation to the profile-based haptics. Two jobs:
    ///
    ///   * <b>Cues.</b> Fire events (ignite / spread / extinguish / re-ignite / stage complete)
    ///     each map to a one-shot role in the palette and play in full.
    ///
    ///   * <b>The spray.</b> Every frame the trigger is held, the fire sim's telemetry (is the foam
    ///     landing, where on the flames, is the nozzle sweeping, is it soaking embers) is turned into
    ///     a <see cref="SprayReading"/>. The rule-based selector picks a looping role from it on the
    ///     spot, so a change from missing to hitting the base is felt on the frame it happens. The
    ///     same reading is streamed to the server, and when Gemini's choice comes back it overrides
    ///     the rules - but only while the coarse situation it was made for still holds, so a stale
    ///     answer can never pin the wrong feel to a new situation.
    ///
    /// This is the seam where "what the trainee is doing" becomes "what the hand feels", and it
    /// is deliberately the only place that knows both the fire sim and the palette.
    /// </summary>
    public sealed class FireHapticRelay : MonoBehaviour
    {
        /// <summary>Seconds between ember pulses while a smouldering patch is left un-soaked.</summary>
        const float SmoulderWarningInterval = 2.5f;

        // Individual labels probed to name the surface a fire sits on (order = priority).
        static readonly MRUKAnchor.SceneLabels[] CandidateLabels =
        {
            MRUKAnchor.SceneLabels.TABLE, MRUKAnchor.SceneLabels.STORAGE, MRUKAnchor.SceneLabels.COUCH,
            MRUKAnchor.SceneLabels.BED, MRUKAnchor.SceneLabels.SCREEN, MRUKAnchor.SceneLabels.WALL_ART,
            MRUKAnchor.SceneLabels.WALL_FACE, MRUKAnchor.SceneLabels.FLOOR, MRUKAnchor.SceneLabels.CEILING,
            MRUKAnchor.SceneLabels.OTHER,
        };

        HapticProfileLibrary _library;
        HapticSessionClient _client;
        HapticPlayer _player;
        Transform _head;
        SurfaceFireManager _fire;
        FoamSprayer _sprayer;
        float _stateInterval = 1f / 8f;
        bool _muted;

        ProfileSelection _override;
        bool _hasOverride;
        string _loopRole;
        float _loopGain = -1f;
        HapticClipData _loopClip;
        float _nextStateSendAt;
        bool _sentIdle = true;
        float _nextSmoulderWarningAt;

        /// <summary>Human-readable description of the last clip played, for the debug readout.</summary>
        public string LastClipDescription { get; private set; } = "(nothing played yet)";
        /// <summary>The looping role and where its choice came from, e.g. "spray_base (gemini)".</summary>
        public string CurrentSelection { get; private set; } = "(silent)";
        /// <summary>Plays that used a generated profile clip.</summary>
        public int GeneratedCount { get; private set; }
        /// <summary>Plays that used the procedural stand-in because the clip had not arrived.</summary>
        public int FallbackCount { get; private set; }
        /// <summary>Loop switches decided by Gemini rather than the on-device rules.</summary>
        public int GeminiSelections { get; private set; }
        public int RuleSelections { get; private set; }

        public void Initialise(HapticProfileLibrary library, HapticSessionClient client, HapticPlayer player,
            Transform head, SurfaceFireManager fire, FoamSprayer sprayer, float stateSendHz)
        {
            _library = library;
            _client = client;
            _player = player;
            _head = head;
            _fire = fire;
            _sprayer = sprayer;
            _stateInterval = stateSendHz > 0.5f ? 1f / stateSendHz : 1f / 8f;

            _fire.SpotIgnited += OnIgnited;
            _fire.SpotSpread += OnSpread;
            _fire.SpotExtinguished += OnExtinguished;
            _fire.SpotReignited += OnReignited;
            _fire.AllExtinguished += OnAllExtinguished;
            _sprayer.SprayStopped += OnSprayStopped;
            _library.ClipChanged += OnClipChanged;
            if (_client != null) _client.SelectionReceived += OnServerSelection;
        }

        void OnDestroy()
        {
            if (_fire != null)
            {
                _fire.SpotIgnited -= OnIgnited;
                _fire.SpotSpread -= OnSpread;
                _fire.SpotExtinguished -= OnExtinguished;
                _fire.SpotReignited -= OnReignited;
                _fire.AllExtinguished -= OnAllExtinguished;
            }
            if (_sprayer != null) _sprayer.SprayStopped -= OnSprayStopped;
            if (_library != null) _library.ClipChanged -= OnClipChanged;
            if (_client != null) _client.SelectionReceived -= OnServerSelection;
            _player?.StopAll();
        }

        /// <summary>
        /// Silence the AI path without tearing it down, so the session can flip between the static
        /// and AI conditions instantly. Readings still stream and clips still arrive while muted -
        /// only playback stops - so switching back is immediate.
        /// </summary>
        public void SetMuted(bool muted)
        {
            if (_muted == muted) return;
            _muted = muted;
            if (!muted) return;
            _player?.StopAll();
            ClearLoop();
        }

        // --- Per frame ----------------------------------------------------------------------

        void Update()
        {
            if (_fire == null || _library == null) return;
            if (!_muted) _player?.Tick();

            var reading = ToReading(_fire.Telemetry);
            DriveLoop(reading);
            StreamState(reading);
            WarnAboutSmouldering(reading);
        }

        static SprayReading ToReading(in SprayTelemetry t) => new SprayReading
        {
            Spraying = t.Spraying,
            Trigger = t.Trigger,
            HitRate = t.HitRate,
            HitQuality = t.HitQuality,
            Soaking = t.Soaking,
            Sweeping = t.Sweeping,
            Coverage = t.Coverage,
            NearestFireMetres = t.NearestFireMetres,
            AimErrorDegrees = t.AimErrorDegrees,
            FiresBurning = t.FiresBurning,
            FiresSmouldering = t.FiresSmouldering,
            Stage = t.StageIndex,
        };

        /// <summary>Rules now; Gemini's override while its situation lasts.</summary>
        void DriveLoop(in SprayReading reading)
        {
            var rules = HapticProfileSelector.Rules(reading);
            if (_hasOverride && _override.StateKey != rules.StateKey) _hasOverride = false;
            var chosen = _hasOverride ? _override : rules;

            if (chosen.IsSilent || _muted || !_sprayer.IsSpraying)
            {
                if (_loopRole != null) ClearLoop();
                return;
            }

            var clip = _library.ClipFor(chosen.Role, out float profileGain, out bool isFallback);
            float gain = chosen.Gain * profileGain;
            bool changed = !ReferenceEquals(clip, _loopClip) || chosen.Role != _loopRole || Mathf.Abs(gain - _loopGain) > 0.02f;
            if (!changed) return;

            bool roleChanged = chosen.Role != _loopRole;
            _player.SetSustained(clip, gain);
            _loopClip = clip;
            _loopRole = chosen.Role;
            _loopGain = gain;
            CurrentSelection = $"{chosen.Role} ({chosen.Source})";
            if (roleChanged)
            {
                if (chosen.Source == "gemini") GeminiSelections++; else RuleSelections++;
                Record(clip, "spray", isFallback, chosen.Source);
            }
        }

        void ClearLoop()
        {
            _player?.StopSustained();
            _loopRole = null;
            _loopClip = null;
            _loopGain = -1f;
            _hasOverride = false;
            CurrentSelection = "(silent)";
        }

        /// <summary>
        /// Stream the reading while spraying, then exactly one idle reading after release so the
        /// server knows the loop stopped (and stops asking Gemini about it).
        /// </summary>
        void StreamState(in SprayReading reading)
        {
            if (_client == null) return;
            if (reading.Spraying)
            {
                if (Time.unscaledTime < _nextStateSendAt) return;
                _nextStateSendAt = Time.unscaledTime + _stateInterval;
                _sentIdle = false;
                _client.Send(HapticWire.State.From(reading));
            }
            else if (!_sentIdle)
            {
                _sentIdle = true;
                _client.Send(HapticWire.State.From(reading));
            }
        }

        void WarnAboutSmouldering(in SprayReading reading)
        {
            if (reading.FiresSmouldering == 0 || reading.Soaking || _muted) return;
            if (Time.unscaledTime < _nextSmoulderWarningAt) return;
            _nextSmoulderWarningAt = Time.unscaledTime + SmoulderWarningInterval;
            PlayRoleCue(HapticRoles.SmoulderWarning, null);
        }

        // --- Server selections ------------------------------------------------------------

        void OnServerSelection(HapticWire.Select select)
        {
            if (select == null || select.source != "gemini") return;   // rules we already have locally
            if (string.IsNullOrEmpty(select.profile))
            {
                _hasOverride = false;
                return;
            }
            var profile = _library.ProfileFor(select.profile);
            string role = profile != null ? profile.Role : select.profile;
            if (!HapticRoles.IsLoop(role)) return;

            _override = new ProfileSelection
            {
                Role = role,
                Gain = Mathf.Clamp(select.gain, 0.2f, 1f),
                Source = "gemini",
                StateKey = select.state_key ?? string.Empty,
                Reason = select.reason,
            };
            _hasOverride = true;
            Debug.Log($"[HapticAI] Gemini: {role} gain {select.gain:0.00} for {select.state_key} - {select.reason}");
        }

        /// <summary>A profile's clip landed: if that role is playing, swap it in mid-loop (no click).</summary>
        void OnClipChanged(HapticProfile profile)
        {
            if (profile == null || _loopRole != profile.Role) return;
            _loopClip = null;   // DriveLoop re-applies on the next frame with the new clip
        }

        // --- Discrete fire cues -------------------------------------------------------------

        void OnIgnited(FireSpot spot) => PlayRoleCue(HapticRoles.Ignite, spot);
        void OnSpread(FireSpot spot) => PlayRoleCue(HapticRoles.Spread, spot);
        void OnExtinguished(FireSpot spot) => PlayRoleCue(HapticRoles.Extinguish, spot);
        void OnReignited(FireSpot spot) => PlayRoleCue(HapticRoles.Reignite, spot);

        void OnAllExtinguished()
        {
            // The spray loop must stop before the completion cue, or the loop would immediately
            // preempt it on the next Tick.
            ClearLoop();
            PlayRoleCue(HapticRoles.StageComplete, null);
        }

        void OnSprayStopped() => ClearLoop();

        void PlayRoleCue(string role, FireSpot spot)
        {
            TellServer(role, spot);
            if (_muted) return;
            var clip = _library.ClipFor(role, out float profileGain, out bool isFallback);
            _player.PlayCue(clip, profileGain);
            Record(clip, "cue", isFallback, role);
        }

        void TellServer(string role, FireSpot spot)
        {
            if (_client == null) return;
            var evt = new HapticWire.Event { name = role, stage = _fire.Stage?.Index ?? 0 };
            if (spot != null)
            {
                evt.surface = SurfaceLabel(spot.Anchor) ?? string.Empty;
                if (_head != null) evt.distance_m = Mathf.Round(Vector3.Distance(_head.position, spot.Position) * 100f) / 100f;
            }
            _client.Send(evt);
        }

        // --- Diagnostics ---------------------------------------------------------------------

        void Record(HapticClipData clip, string kind, bool isFallback, string detail)
        {
            if (clip == null) return;
            if (isFallback) FallbackCount++; else GeneratedCount++;
            string source = isFallback ? "procedural stand-in" : "generated";
            LastClipDescription = $"{kind}: {clip.Key} [{source}] {clip.Amplitudes.Length} samples";
            Debug.Log($"[HapticAI] play {LastClipDescription} ({detail})");
        }

        static string SurfaceLabel(MRUKAnchor anchor)
        {
            if (anchor == null) return null;
            foreach (var label in CandidateLabels)
                if (anchor.HasAnyLabel(label)) return label.ToString();
            return null;
        }
    }
}

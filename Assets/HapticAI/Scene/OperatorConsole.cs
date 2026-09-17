using System.Collections.Generic;
using System.Threading.Tasks;
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
    /// The setup screen, and the app's entry point: the scene the headset opens on. It is for the
    /// person running the study, not the participant - they set who is in the headset, which
    /// haptics condition that person gets, and where the laptop running the haptics server is, then
    /// press A to hand the headset over.
    ///
    /// It exists because all three of those are things that change during a study and cannot be
    /// baked into a build: the laptop's address changes with the network, the condition changes per
    /// participant, and the participant changes every twenty minutes. Without this screen, the only
    /// way to change any of them is a rebuild and a reinstall between participants.
    ///
    /// Everything is saved to the headset as it is changed (<see cref="StudySettings"/>), so a
    /// headset that has been set up once opens on the same settings tomorrow.
    ///
    /// Input is the thumbstick plus A/B, not a pointer and not a keyboard. There is no on-headset
    /// keyboard to rely on, and a laser pointer needs an event system, raycasters and a visible ray
    /// - all of which can fail silently and leave an operator stuck at a screen they cannot use.
    /// A stick and two buttons cannot. That is also why the server address is edited as four
    /// numbers rather than typed.
    /// </summary>
    public sealed class OperatorConsole : MonoBehaviour
    {
        /// <summary>Address offered the first time, when the device has nothing saved and no file to read.</summary>
        static readonly int[] DefaultOctets = { 192, 168, 1, 100 };

        enum Row
        {
            Participant,
            Condition,
            ConditionSwitch,
            Server,
            Address,
            TestConnection,
            Start,
        }

        const int RowCount = (int)Row.Start + 1;

        StudySettings _settings;
        TrainingPanel _panel;

        int _selected;
        bool _editingAddress;
        int _addressField;              // 0-3 = octets, 4 = port
        int[] _octets;
        int _port;

        string _status = "";
        bool _probing;
        float _resetArmedUntil;

        readonly AxisRepeat _vertical = new AxisRepeat();
        readonly AxisRepeat _horizontal = new AxisRepeat();

        async void Start()
        {
            var rig = XrRig.EnsureCameraRig();
            XrRig.ConfigurePassthrough(rig);

            _settings = StudySettings.Current.Copy();
            _panel = TrainingPanel.Create(rig != null ? rig.centerEyeAnchor : transform);
            LoadAddressFields();
            Render();

            // A headset that has never been set up still has an address somewhere if the build
            // shipped one (StreamingAssets) or the Editor has the environment variable set. Showing
            // that instead of a made-up default means the first thing the operator sees is the
            // address the app would actually have used.
            if (!StudySettings.Current.Configured)
            {
                await PrefillFromShippedConfigAsync();
                Render();
            }
        }

        async Task PrefillFromShippedConfigAsync()
        {
            HapticSecrets secrets;
            try
            {
                secrets = await HapticSecrets.LoadShippedAsync();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HapticAI] Could not read a shipped haptic service URL: {e.Message}");
                return;
            }

            if (this == null || _settings == null) return;   // scene changed while we waited
            if (string.IsNullOrEmpty(secrets.hapticServiceUrl)) return;
            if (!TrySplitUrl(secrets.hapticServiceUrl, out string host, out int port)) return;

            _settings.serverHost = host;
            _settings.serverPort = port;
            LoadAddressFields();
            _status = $"address from {secrets.Source}";
        }

        // --- Input --------------------------------------------------------------------------

        void Update()
        {
            float now = Time.unscaledTime;
            Vector2 stick = ReadStick();
            int vertical = _vertical.Step(stick.y, now);
            int horizontal = _horizontal.Step(stick.x, now);

            bool changed = false;

            if (_editingAddress)
            {
                if (horizontal != 0) { MoveAddressField(horizontal); changed = true; }
                if (vertical != 0) { NudgeAddressField(vertical); changed = true; }

                if (ConfirmPressed()) { CommitAddress(); changed = true; }
                else if (CancelPressed()) { CancelAddress(); changed = true; }
            }
            else
            {
                // Up on the stick moves up the list, which is the opposite sign to the axis.
                if (vertical != 0)
                {
                    _selected = (_selected - vertical + RowCount) % RowCount;
                    changed = true;
                }
                if (horizontal != 0) { Adjust((Row)_selected, horizontal); changed = true; }
                if (ConfirmPressed()) { Activate((Row)_selected); changed = true; }
                if (ResetPressed()) { HandleResetRequest(); changed = true; }
            }

            if (changed) Render();
        }

        static Vector2 ReadStick()
        {
            Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick) +
                            OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
            return stick + KeyboardStick();
        }

        /// <summary>
        /// Keyboard mirrors, for driving this screen in the Editor where OVRInput reports nothing.
        /// A/Enter and B/Escape match the same idea as the briefing screen's Space/Tab.
        /// </summary>
        static Vector2 KeyboardStick()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard == null) return Vector2.zero;
            float x = (keyboard.rightArrowKey.isPressed ? 1f : 0f) - (keyboard.leftArrowKey.isPressed ? 1f : 0f);
            float y = (keyboard.upArrowKey.isPressed ? 1f : 0f) - (keyboard.downArrowKey.isPressed ? 1f : 0f);
            return new Vector2(x, y);
#else
            return Vector2.zero;
#endif
        }

        static bool ConfirmPressed() =>
            OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) || KeyDown(KeyRole.Confirm);

        static bool CancelPressed() =>
            OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) || KeyDown(KeyRole.Cancel);

        /// <summary>Y on the left controller - deliberately the hand that is not doing anything else.</summary>
        static bool ResetPressed() =>
            OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch) || KeyDown(KeyRole.Reset);

        enum KeyRole { Confirm, Cancel, Reset }

        static bool KeyDown(KeyRole role)
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard == null) return false;
            switch (role)
            {
                case KeyRole.Confirm: return keyboard.enterKey.wasPressedThisFrame ||
                                             keyboard.spaceKey.wasPressedThisFrame;
                case KeyRole.Cancel: return keyboard.escapeKey.wasPressedThisFrame;
                case KeyRole.Reset: return keyboard.f5Key.wasPressedThisFrame;
            }
#endif
            return false;
        }

        // --- Row behaviour ------------------------------------------------------------------

        /// <summary>Left/right on a row: change its value in place.</summary>
        void Adjust(Row row, int direction)
        {
            switch (row)
            {
                case Row.Participant:
                    _settings.participantNumber = Mathf.Clamp(_settings.participantNumber + direction,
                        StudySettings.MinParticipant, StudySettings.MaxParticipant);
                    break;
                case Row.Condition:
                    _settings.condition = _settings.condition == HapticsMode.Ai
                        ? HapticsMode.Static
                        : HapticsMode.Ai;
                    break;
                case Row.ConditionSwitch:
                    _settings.lockCondition = !_settings.lockCondition;
                    break;
                case Row.Server:
                    _settings.useServer = !_settings.useServer;
                    break;
                case Row.Address:
                    BeginAddressEdit();
                    return;   // nothing to save yet; the edit commits it
                default:
                    return;
            }
            SaveNow();
        }

        /// <summary>A on a row: the rows that do something rather than hold a value.</summary>
        void Activate(Row row)
        {
            switch (row)
            {
                case Row.Address:
                    BeginAddressEdit();
                    break;
                case Row.TestConnection:
                    if (!_probing) _ = TestConnectionAsync();
                    break;
                case Row.Start:
                    StartExperience();
                    break;
                default:
                    Adjust(row, 1);
                    break;
            }
        }

        void SaveNow()
        {
            _settings.Save();
            _status = "saved to this headset";
        }

        // --- Address editing ----------------------------------------------------------------

        void LoadAddressFields()
        {
            _octets = StudySettings.TryParseIPv4(_settings.serverHost, out var parsed)
                ? parsed
                : (int[])DefaultOctets.Clone();
            _port = _settings.serverPort;
        }

        void BeginAddressEdit()
        {
            if (!_settings.useServer)
            {
                _status = "switch the haptics server on first";
                return;
            }
            LoadAddressFields();
            _editingAddress = true;
            _addressField = 0;
            _selected = (int)Row.Address;
        }

        void MoveAddressField(int direction) =>
            _addressField = Mathf.Clamp(_addressField + direction, 0, 4);

        void NudgeAddressField(int direction)
        {
            if (_addressField < 4)
                _octets[_addressField] = Mathf.Clamp(_octets[_addressField] + direction, 0, 255);
            else
                _port = Mathf.Clamp(_port + direction, 1, 65535);
        }

        void CommitAddress()
        {
            _settings.serverHost = StudySettings.FormatIPv4(_octets);
            _settings.serverPort = _port;
            _editingAddress = false;
            SaveNow();
        }

        void CancelAddress()
        {
            _editingAddress = false;
            LoadAddressFields();   // back to what is saved
            _status = "address unchanged";
        }

        // --- Actions ------------------------------------------------------------------------

        async Task TestConnectionAsync()
        {
            string url = _settings.ServiceUrl;
            if (string.IsNullOrEmpty(url))
            {
                _status = _settings.useServer
                    ? "set an address first - there is nothing to test yet"
                    : "nothing to test - the haptics server is switched off";
                Render();
                return;
            }

            _probing = true;
            _status = $"checking {url} ...";
            Render();

            // Any failure here has to clear _probing, or the row says "checking ..." for the rest
            // of the session and the operator can never test again.
            try
            {
                var probe = await HapticSecrets.ProbeAsync(url);
                if (this == null) return;   // scene changed while the request was in flight
                _status = probe.Reachable
                    ? $"server answered at {url}"
                    : $"no answer from {url} - {probe.Message}";
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HapticAI] Connection test failed: {e}");
                _status = $"could not reach {url} - {e.Message}";
            }
            finally
            {
                _probing = false;
            }

            if (this == null) return;
            Render();
        }

        void StartExperience()
        {
            if (_settings.useServer && string.IsNullOrEmpty(_settings.serverHost))
            {
                _status = "set an address, or switch the haptics server off";
                return;
            }

            _settings.Save();
            Debug.Log($"[HapticAI] Starting {_settings.ParticipantId} in " +
                      $"{ConditionLabel(_settings.condition)} " +
                      $"(server: {(string.IsNullOrEmpty(_settings.ServiceUrl) ? "off" : _settings.ServiceUrl)})");
            SceneManager.LoadScene(StudyScenes.Training);
        }

        /// <summary>
        /// Two presses, because one press of a button the operator is holding anyway should not be
        /// able to wipe the address mid-study.
        /// </summary>
        void HandleResetRequest()
        {
            if (Time.unscaledTime <= _resetArmedUntil)
            {
                _settings = StudySettings.ResetToDefaults().Copy();
                LoadAddressFields();
                _resetArmedUntil = 0f;
                _status = "reset to defaults";
                return;
            }

            _resetArmedUntil = Time.unscaledTime + 3f;
            _status = "press Y again to reset everything to defaults";
        }

        // --- Drawing ------------------------------------------------------------------------

        void Render()
        {
            var rows = new List<MenuRow>(RowCount);
            bool addressUsable = _settings.useServer;

            rows.Add(Line(Row.Participant, "Participant", _settings.ParticipantId));
            rows.Add(Line(Row.Condition, "Haptics condition", ConditionLabel(_settings.condition)));
            rows.Add(Line(Row.ConditionSwitch, "Participant can switch condition",
                _settings.lockCondition ? "no (locked)" : "yes (B button)"));
            rows.Add(Line(Row.Server, "Haptics server",
                _settings.useServer ? "on (laptop)" : "off (on-device only)"));
            rows.Add(Line(Row.Address, "Server address", AddressValue(), dimmed: !addressUsable));
            rows.Add(Line(Row.TestConnection, "Test connection", TestValue()));
            rows.Add(Line(Row.Start, "Start the experience", "press A"));

            _panel.ShowMenu($"Session Setup - {_settings.ParticipantId}", rows, Note(), Footer());
        }

        MenuRow Line(Row row, string label, string value, bool dimmed = false) =>
            new MenuRow(label, value, _selected == (int)row, dimmed);

        string AddressValue()
        {
            if (_editingAddress)
            {
                // The field being changed is bracketed, so it is obvious which number the stick is
                // moving without needing a caret or any colour the operator has to learn.
                string[] parts =
                {
                    _octets[0].ToString(), _octets[1].ToString(),
                    _octets[2].ToString(), _octets[3].ToString(),
                };
                string host = string.Join(".", Bracket(parts, _addressField));
                string port = _addressField == 4 ? $"[{_port}]" : _port.ToString();
                return $"{host} : {port}";
            }

            if (!_settings.useServer) return "-";
            if (string.IsNullOrEmpty(_settings.serverHost)) return "not set";
            return $"{_settings.serverHost} : {_settings.serverPort}";
        }

        static string[] Bracket(string[] parts, int index)
        {
            if (index >= 0 && index < parts.Length) parts[index] = $"[{parts[index]}]";
            return parts;
        }

        string TestValue()
        {
            if (_probing) return "checking ...";
            if (!_settings.useServer) return "-";
            return "press A";
        }

        string Note()
        {
            string help;
            if (_editingAddress)
            {
                help = "Stick left/right picks a number, up/down changes it. Hold to move faster. " +
                       "This is the laptop's address on the same Wi-Fi as the headset.";
            }
            else
            {
                switch ((Row)_selected)
                {
                    case Row.Participant:
                        help = "Who is in the headset. Recorded against each run in the log.";
                        break;
                    case Row.Condition:
                        help = "Which haptics this participant gets: generated clips, or the fixed " +
                               "rumble baseline.";
                        break;
                    case Row.ConditionSwitch:
                        help = "Locked stops the participant changing condition mid-study with the " +
                               "B button. Leave it locked while collecting data.";
                        break;
                    case Row.Server:
                        help = "On routes generation through the laptop. Off runs everything " +
                               "on-device, which always works but is the mock synth, not HaptiGen.";
                        break;
                    case Row.Address:
                        help = "Press A to edit. The laptop's IP address and the port the server " +
                               "is listening on (default " + StudySettings.DefaultPort + ").";
                        break;
                    case Row.TestConnection:
                        help = "Asks the server for /health. Do this before every session - it is " +
                               "the only way to know the laptop is really reachable.";
                        break;
                    default:
                        help = "Hands over to the training experience. Come back here with Y at any " +
                               "briefing or result screen.\nEach finished run is appended to " +
                               StudyLog.FileName + " on this headset.";
                        break;
                }
            }

            return string.IsNullOrEmpty(_status) ? help : help + "\n" + _status;
        }

        string Footer() =>
            _editingAddress
                ? "Stick  choose / change      A  done      B  cancel"
                : "Stick  move / change      A  select      Y  reset to defaults";

        static string ConditionLabel(HapticsMode mode) =>
            mode == HapticsMode.Ai ? "AI-generated (dynamic)" : "Fixed rumble (baseline)";

        /// <summary>Split "http://host:port" back into its parts, for prefilling the editor.</summary>
        static bool TrySplitUrl(string url, out string host, out int port)
        {
            host = string.Empty;
            port = StudySettings.DefaultPort;
            if (string.IsNullOrEmpty(url)) return false;

            try
            {
                var uri = new System.Uri(url);
                if (!StudySettings.TryParseIPv4(uri.Host, out _)) return false;   // hostnames stay in the file
                host = uri.Host;
                port = uri.Port > 0 ? uri.Port : StudySettings.DefaultPort;
                return true;
            }
            catch (System.UriFormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// One axis of the thumbstick turned into discrete steps: one on the first push, then a
        /// repeat that speeds up while held. Without the acceleration, setting an octet to 200 is
        /// two hundred deliberate flicks.
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
}

using System;
using UnityEngine;

namespace HapticAI.Config
{
    /// <summary>
    /// Everything the person running a session chooses on the headset: which participant is in the
    /// chair, which haptics condition they get, and where the haptics server is.
    ///
    /// These are stored client-side in <see cref="PlayerPrefs"/> (on Android: the app's
    /// SharedPreferences, in internal storage) rather than in a file the build ships with, because
    /// the values that matter are exactly the ones that cannot be baked in at build time - the
    /// laptop's address changes with every network the study is run on, and the participant changes
    /// every twenty minutes. Configuring a headset once and having it stay configured across app
    /// restarts, sleeps and battery swaps is the whole point of this class.
    ///
    /// <see cref="Current"/> is the single instance the experience reads. It is loaded lazily on
    /// first use and kept for the life of the process, so the setup scene and the training scene
    /// see the same values without either having to pass them along.
    ///
    /// Nothing here is a secret: the URL is a LAN address, and the Gemini key lives on the server.
    /// </summary>
    [Serializable]
    public sealed class StudySettings
    {
        public const int DefaultPort = 8080;
        public const int MinParticipant = 1;
        public const int MaxParticipant = 99;

        // Versioned so a later change of shape can be spotted rather than silently half-reading an
        // older layout.
        const string Prefix = "hapticai.study.v1.";
        const string KeyConfigured = Prefix + "configured";
        const string KeyParticipant = Prefix + "participant";
        const string KeyCondition = Prefix + "condition";
        const string KeyLockCondition = Prefix + "lockCondition";
        const string KeyUseServer = Prefix + "useServer";
        const string KeyServerHost = Prefix + "serverHost";
        const string KeyServerPort = Prefix + "serverPort";

        /// <summary>Participant currently in the headset. Rendered as "P07" and used in the run log.</summary>
        public int participantNumber = MinParticipant;

        /// <summary>The A/B condition this participant is being run in.</summary>
        public HapticsMode condition = HapticsMode.Ai;

        /// <summary>
        /// Stop the participant switching condition mid-study with the B button. On by default: a
        /// participant who flips condition halfway through a run has produced unusable data, and
        /// that button is right next to the one that starts the run.
        /// </summary>
        public bool lockCondition = true;

        /// <summary>Route generation through the laptop, rather than running fully on-device.</summary>
        public bool useServer = true;

        /// <summary>Host of the haptics server - normally the laptop's LAN address, e.g. "192.168.1.42".</summary>
        public string serverHost = "";

        public int serverPort = DefaultPort;

        /// <summary>
        /// False until somebody has actually been through the setup screen and saved. Until then
        /// URL resolution in <see cref="HapticSecrets"/> keeps its old behaviour (environment
        /// variable, then StreamingAssets file, then the Editor localhost fallback), so a fresh
        /// install or a dev machine is not forced through the headset UI to get a URL.
        /// </summary>
        public bool Configured { get; private set; }

        /// <summary>
        /// The backend base URL these settings ask for, or empty when the operator has chosen to
        /// run on-device. Empty is a real answer, not "unset" - see <see cref="Configured"/>.
        /// </summary>
        public string ServiceUrl
        {
            get
            {
                if (!useServer) return string.Empty;
                string host = (serverHost ?? string.Empty).Trim();
                if (host.Length == 0) return string.Empty;
                return $"http://{host}:{Mathf.Clamp(serverPort, 1, 65535)}";
            }
        }

        public string ParticipantId =>
            "P" + Mathf.Clamp(participantNumber, MinParticipant, MaxParticipant).ToString("00");

        // --- The shared instance ------------------------------------------------------------

        static StudySettings _current;

        /// <summary>The settings the running app is using. Loaded from the device on first access.</summary>
        public static StudySettings Current
        {
            get
            {
                if (_current == null) _current = Load();
                return _current;
            }
        }

        public static StudySettings Load()
        {
            var s = new StudySettings
            {
                Configured = PlayerPrefs.GetInt(KeyConfigured, 0) == 1,
                participantNumber = PlayerPrefs.GetInt(KeyParticipant, MinParticipant),
                condition = PlayerPrefs.GetInt(KeyCondition, (int)HapticsMode.Ai) == (int)HapticsMode.Static
                    ? HapticsMode.Static
                    : HapticsMode.Ai,
                lockCondition = PlayerPrefs.GetInt(KeyLockCondition, 1) == 1,
                useServer = PlayerPrefs.GetInt(KeyUseServer, 1) == 1,
                serverHost = PlayerPrefs.GetString(KeyServerHost, ""),
                serverPort = PlayerPrefs.GetInt(KeyServerPort, DefaultPort),
            };
            s.Clamp();
            return s;
        }

        /// <summary>
        /// Write these settings to the device and make them the ones the app is using.
        ///
        /// <see cref="PlayerPrefs.Save"/> is called every time rather than left to Unity's own
        /// flush on quit, because the realistic way this app ends on a headset is the operator
        /// pulling it off and the battery running out - not a clean exit.
        /// </summary>
        public void Save()
        {
            Clamp();
            Configured = true;
            _current = this;

            PlayerPrefs.SetInt(KeyConfigured, 1);
            PlayerPrefs.SetInt(KeyParticipant, participantNumber);
            PlayerPrefs.SetInt(KeyCondition, (int)condition);
            PlayerPrefs.SetInt(KeyLockCondition, lockCondition ? 1 : 0);
            PlayerPrefs.SetInt(KeyUseServer, useServer ? 1 : 0);
            PlayerPrefs.SetString(KeyServerHost, serverHost ?? string.Empty);
            PlayerPrefs.SetInt(KeyServerPort, serverPort);
            PlayerPrefs.Save();
        }

        /// <summary>Forget everything stored on this headset and return the defaults.</summary>
        public static StudySettings ResetToDefaults()
        {
            string[] keys =
            {
                KeyConfigured, KeyParticipant, KeyCondition, KeyLockCondition,
                KeyUseServer, KeyServerHost, KeyServerPort,
            };
            foreach (var key in keys) PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();

            _current = new StudySettings();
            return _current;
        }

        /// <summary>A detached copy, so a setup screen can be cancelled without side effects.</summary>
        public StudySettings Copy() => (StudySettings)MemberwiseClone();

        void Clamp()
        {
            participantNumber = Mathf.Clamp(participantNumber, MinParticipant, MaxParticipant);
            serverPort = Mathf.Clamp(serverPort, 1, 65535);
            serverHost = (serverHost ?? string.Empty).Trim();
        }

        // --- Address helpers ----------------------------------------------------------------
        // The setup screen edits the host as four numbers, because there is no keyboard in the
        // headset. A host that is not a dotted quad (a hostname from the StreamingAssets file, say)
        // is still stored and used verbatim; it just cannot be edited in-headset.

        /// <summary>Parse a dotted-quad address. Returns false for anything else, hostnames included.</summary>
        public static bool TryParseIPv4(string host, out int[] octets)
        {
            octets = new[] { 0, 0, 0, 0 };
            if (string.IsNullOrWhiteSpace(host)) return false;

            var parts = host.Trim().Split('.');
            if (parts.Length != 4) return false;

            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(parts[i], out int value) || value < 0 || value > 255) return false;
                // Reject "01" and "+1": int.TryParse accepts both, and an address that renders back
                // differently from what was stored is a debugging trap.
                if (parts[i] != value.ToString()) return false;
                octets[i] = value;
            }
            return true;
        }

        public static string FormatIPv4(int[] octets)
        {
            if (octets == null || octets.Length != 4) return string.Empty;
            return $"{Clamp255(octets[0])}.{Clamp255(octets[1])}.{Clamp255(octets[2])}.{Clamp255(octets[3])}";
        }

        static int Clamp255(int v) => Mathf.Clamp(v, 0, 255);
    }
}

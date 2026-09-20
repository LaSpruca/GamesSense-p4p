using System;
using UnityEngine;

namespace HapticAI.Profiles
{
    /// <summary>
    /// The JSON messages exchanged with the server over the session socket, shaped for
    /// <see cref="JsonUtility"/> (public fields, no dictionaries, snake_case to match the wire).
    /// The protocol itself is documented in <c>server/ws_session.py</c>.
    /// </summary>
    public static class HapticWire
    {
        public const int ProtocolVersion = 1;

        // --- Server -> headset ----------------------------------------------------------------

        [Serializable] public sealed class Envelope { public string type; }

        [Serializable]
        public sealed class Welcome
        {
            public string type;
            public bool gemini;
            public string transport;
            public bool model;
        }

        [Serializable]
        public sealed class ProfileSpec
        {
            public string id;
            public string role;
            public string prompt;
            public float duration_s;
            public bool loop;
            public float gain;
        }

        [Serializable]
        public sealed class Profiles
        {
            public string type;
            public string set_id;
            public string scene_key;
            public string source;
            public string notes;
            public ProfileSpec[] profiles;
        }

        [Serializable]
        public sealed class ClipHeader
        {
            public string type;
            public string set_id;
            public string profile_id;
            public string source;
        }

        [Serializable]
        public sealed class Select
        {
            public string type;
            public string profile;
            public float gain;
            public string source;
            public string reason;
            public string state_key;
        }

        [Serializable] public sealed class Error { public string type; public string message; }
        [Serializable] public sealed class Pong { public string type; public long t; }

        // --- Headset -> server ----------------------------------------------------------------

        [Serializable]
        public sealed class Hello
        {
            public string type = "hello";
            public string client = "quest";
            public int version = ProtocolVersion;
        }

        [Serializable]
        public sealed class RoomSnapshot
        {
            public string[] surfaces;
            public float[] size_m;
            public int anchors;
        }

        [Serializable]
        public sealed class ExtinguisherSnapshot
        {
            public string type = "foam";
            public string hand = "right";
        }

        [Serializable]
        public sealed class StageSnapshot
        {
            public int index;
            public string name;
            public string aim;
            public bool spread;
            public int sweep_sectors;
            public bool reignite;
        }

        /// <summary>
        /// Everything Gemini gets to know about the scene, sent once per session. Deliberately
        /// small and stable: the server keys its plan cache on it, so two sessions in the same room
        /// must produce byte-identical snapshots to share a palette.
        /// </summary>
        [Serializable]
        public sealed class SceneSnapshot
        {
            public RoomSnapshot room;
            public ExtinguisherSnapshot extinguisher;
            public StageSnapshot[] stages;
        }

        [Serializable]
        public sealed class Scene
        {
            public string type = "scene";
            public SceneSnapshot scene;
            public bool refresh;
            public bool send_clips;
        }

        [Serializable]
        public sealed class ClipsNeeded
        {
            public string type = "clips_needed";
            public string set_id;
            public string[] profiles;
        }

        /// <summary>The spray reading, streamed while the trigger is held. Mirrors <see cref="SprayReading"/>.</summary>
        [Serializable]
        public sealed class State
        {
            public string type = "state";
            public bool spraying;
            public float trigger;
            public float hit_rate;
            public float hit_quality;
            public bool soaking;
            public bool sweeping;
            public float coverage;
            public float nearest_fire_m;
            public float aim_error_deg;
            public int fires_burning;
            public int fires_smouldering;
            public int stage;

            public static State From(in SprayReading r) => new State
            {
                spraying = r.Spraying,
                trigger = Round(r.Trigger),
                hit_rate = Round(r.HitRate),
                hit_quality = Round(r.HitQuality),
                soaking = r.Soaking,
                sweeping = r.Sweeping,
                coverage = Round(r.Coverage),
                nearest_fire_m = Round(r.NearestFireMetres),
                aim_error_deg = Round(r.AimErrorDegrees),
                fires_burning = r.FiresBurning,
                fires_smouldering = r.FiresSmouldering,
                stage = r.Stage,
            };

            static float Round(float v) => (float)Math.Round(v, 2);
        }

        [Serializable]
        public sealed class Event
        {
            public string type = "event";
            public string name;
            public string surface;
            public float distance_m;
            public int stage;
        }

        [Serializable] public sealed class Ping { public string type = "ping"; public long t; }

        public static string TypeOf(string json)
        {
            try { return JsonUtility.FromJson<Envelope>(json)?.type; }
            catch (Exception) { return null; }
        }
    }
}

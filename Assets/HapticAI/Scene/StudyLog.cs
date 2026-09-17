using System;
using System.Globalization;
using System.IO;
using System.Text;
using FireSim;
using HapticAI.Config;
using UnityEngine;

namespace HapticAI.Scene
{
    /// <summary>
    /// One line of CSV per completed run, written to the headset.
    ///
    /// The result panel already shows a participant everything this records, but it shows it for
    /// about thirty seconds to a person wearing a headset, which is not somewhere data can be read
    /// back from. This is the same numbers, kept, so that a session which went well does not depend
    /// on the operator having copied them down in time.
    ///
    /// It is deliberately append-only and deliberately CSV: a file that can be pulled off the
    /// headset with one adb command and opened without any of this project.
    ///
    ///   adb shell run-as &lt;package&gt; cat files/study-runs.csv
    ///   adb pull /sdcard/Android/data/&lt;package&gt;/files/study-runs.csv
    ///
    /// Failing to write must never interrupt a session, so every failure here is a warning in the
    /// log and nothing more.
    /// </summary>
    public static class StudyLog
    {
        public const string FileName = "study-runs.csv";

        const string Header = "timestamp,participant,condition,duration_s,fires_extinguished," +
                              "peak_burning,clips_generated,clips_fallback,haptic_source";

        public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        public static void AppendRun(StudySettings settings, HapticsMode mode, float runSeconds,
            SurfaceFireManager fire, FireHapticRelay relay, string hapticSource)
        {
            var line = new StringBuilder();
            Append(line, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Append(line, settings != null ? settings.ParticipantId : "unknown");
            Append(line, mode == HapticsMode.Ai ? "ai" : "static");
            Append(line, runSeconds.ToString("0.0", CultureInfo.InvariantCulture));
            Append(line, fire != null ? fire.TotalExtinguished.ToString() : "");
            Append(line, fire != null ? fire.PeakBurning.ToString() : "");
            Append(line, relay != null ? relay.GeneratedCount.ToString() : "");
            Append(line, relay != null ? relay.FallbackCount.ToString() : "");
            Append(line, hapticSource, last: true);

            Write(line.ToString());
        }

        static void Write(string line)
        {
            try
            {
                string path = FilePath;
                bool needsHeader = !File.Exists(path);
                using (var writer = new StreamWriter(path, append: true, Encoding.UTF8))
                {
                    if (needsHeader) writer.WriteLine(Header);
                    writer.WriteLine(line);
                }
                Debug.Log($"[FireSim] Run logged to {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FireSim] Could not write the run log: {e.Message}");
            }
        }

        static void Append(StringBuilder sb, string value, bool last = false)
        {
            sb.Append(Escape(value));
            if (!last) sb.Append(',');
        }

        /// <summary>
        /// Quote anything with a comma or a quote in it. The haptic source description is free text
        /// and does contain commas, which would otherwise silently shift every later column.
        /// </summary>
        static string Escape(string value)
        {
            value ??= string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return '"' + value.Replace("\"", "\"\"") + '"';
        }
    }
}

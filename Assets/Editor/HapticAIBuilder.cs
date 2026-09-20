using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HapticAI.EditorTools
{
    /// <summary>
    /// Headless build entry point, invoked by the Unity CLI via --execute-method.
    /// Output path comes from -buildOutput, which `unity build --output-path` supplies.
    /// </summary>
    public static class HapticAIBuilder
    {
        const string DefaultOutput = "Builds/Android/HapticAI.apk";

        public static void BuildAndroid()
        {
            var output = ArgValue("-buildOutput") ?? DefaultOutput;
            if (!Path.IsPathRooted(output))
                output = Path.GetFullPath(output);

            Directory.CreateDirectory(Path.GetDirectoryName(output));

            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
                Fail("No enabled scenes in Build Settings.");

            // APK, not AAB — this build gets installed straight onto a headset.
            EditorUserBuildSettings.buildAppBundle = false;

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };

            Debug.Log($"[HapticAIBuilder] Building {scenes.Length} scene(s) -> {output}");
            foreach (var s in scenes)
                Debug.Log($"[HapticAIBuilder]   scene: {s}");

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
                Fail($"Build {summary.result}: {summary.totalErrors} error(s).");

            // summary.totalSize counts the uncompressed staging output, not the APK — report the file.
            var apkMb = new FileInfo(summary.outputPath).Length / (1024f * 1024f);
            Debug.Log(
                $"[HapticAIBuilder] Succeeded: {summary.outputPath} " +
                $"({apkMb:F1} MB APK in {summary.totalTime.TotalSeconds:F0}s)");
        }

        static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i < args.Length - 1 ? args[i + 1] : null;
        }

        static void Fail(string message)
        {
            Debug.LogError($"[HapticAIBuilder] {message}");
            EditorApplication.Exit(1);
        }
    }
}

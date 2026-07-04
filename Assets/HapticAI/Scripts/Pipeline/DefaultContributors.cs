using HapticAI.Core;
using UnityEngine;

namespace HapticAI.Pipeline
{
    /// <summary>Base descriptor for the kind of event (ignite/spread/extinguish/spray).</summary>
    public sealed class EventTypeContributor : IContextContributor
    {
        public void Contribute(HapticEvent evt, HapticContext ctx)
        {
            switch (evt.Type)
            {
                case HapticEventType.Ignite:
                    ctx.AddDescriptor("a fire suddenly ignites nearby"); break;
                case HapticEventType.Spread:
                    ctx.AddDescriptor("flames spread to a new surface"); break;
                case HapticEventType.Extinguish:
                    ctx.AddDescriptor("a burning patch is snuffed out into a hiss of steam"); break;
                case HapticEventType.SprayTick:
                    ctx.AddDescriptor("extinguisher foam is being sprayed under pressure"); break;
                default:
                    ctx.AddDescriptor(evt.Name); break;
            }
        }
    }

    /// <summary>Turns an MRUK surface label (e.g. TABLE, FLOOR) into a material phrase.</summary>
    public sealed class SurfaceMaterialContributor : IContextContributor
    {
        public void Contribute(HapticEvent evt, HapticContext ctx)
        {
            var surface = evt.Tag("surface");
            if (string.IsNullOrEmpty(surface)) return;
            ctx.SetField("surface", surface);
            ctx.AddDescriptor($"on a {surface.ToLowerInvariant().Replace('_', ' ')} surface");
        }
    }

    /// <summary>Maps a 0..1 fire intensity onto a qualitative descriptor.</summary>
    public sealed class FireIntensityContributor : IContextContributor
    {
        public void Contribute(HapticEvent evt, HapticContext ctx)
        {
            if (!evt.Numbers.ContainsKey("intensity")) return;
            float i = Mathf.Clamp01(evt.Number("intensity"));
            ctx.SetField("intensity", i.ToString("0.00"));
            string word = i < 0.33f ? "faint" : i < 0.66f ? "moderate" : "intense";
            ctx.AddDescriptor($"{word} in intensity");
        }
    }

    /// <summary>Closer events should feel stronger; maps distance (metres) to a phrase.</summary>
    public sealed class DistanceContributor : IContextContributor
    {
        public void Contribute(HapticEvent evt, HapticContext ctx)
        {
            if (!evt.Numbers.ContainsKey("distance")) return;
            float d = Mathf.Max(0f, evt.Number("distance"));
            ctx.SetField("distance_m", d.ToString("0.00"));
            string word = d < 0.5f ? "very close to the hand" : d < 1.5f ? "an arm's length away" : "across the room";
            ctx.AddDescriptor(word);
        }
    }

    /// <summary>How hard the trigger is pulled drives spray force.</summary>
    public sealed class TriggerDepthContributor : IContextContributor
    {
        public void Contribute(HapticEvent evt, HapticContext ctx)
        {
            if (!evt.Numbers.ContainsKey("triggerDepth")) return;
            float t = Mathf.Clamp01(evt.Number("triggerDepth"));
            ctx.SetField("trigger", t.ToString("0.00"));
            string word = t < 0.4f ? "a gentle trickle" : t < 0.8f ? "a steady stream" : "a full-force blast";
            ctx.AddDescriptor($"delivered as {word}");
        }
    }
}

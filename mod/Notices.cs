using System;
using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace ProbablyNotHuman
{
    // The game's pop-up notifications (scavenging results, wounds, reputation changes, refusals...) all pass through
    // ReputationUIManager.Notify/NotifyReputation and vanish after a few seconds; record them so results and observations can carry them.
    internal static class Notices
    {
        private const string Owner = "probably_not_human.notices";
        private static Harmony harmony;
        private static readonly List<(long seq, string text)> recent = new List<(long, string)>();
        internal static long Sequence { get; private set; }

        internal static void Start(ModLog log)
        {
            if (harmony != null) return;
            harmony = new Harmony(Owner);
            var postfix = new HarmonyMethod(((Action<ReputationUIManager>)Shown).Method);
            foreach (var (method, signature) in new[] { ("Notify", new[] { typeof(string), typeof(string) }), ("NotifyReputation", new[] { typeof(string), typeof(int) }) })
            {
                try { harmony.Patch(ModReflect.Target(typeof(ReputationUIManager), method, signature), postfix: postfix); }
                catch (Exception error) { log?.Error("Notice patch failure: " + method + ": " + error.Message); }
            }
        }
        internal static void Stop() { harmony?.UnpatchAll(Owner); harmony = null; }

        // Postfix: the notifier text is set by then, for both kinds.
        private static void Shown(ReputationUIManager __instance)
        {
            string text = __instance?.reputationNotifierTMP?.text;
            if (string.IsNullOrWhiteSpace(text)) return;
            recent.Add((++Sequence, text));
            if (recent.Count > 30) recent.RemoveAt(0);
        }

        internal static JArray Since(long seq)
        {
            var result = new JArray();
            foreach (var notice in recent) if (notice.seq > seq) result.Add(notice.text);
            return result;
        }
    }
}

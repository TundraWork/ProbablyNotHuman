using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProbablyNotHuman
{
    public sealed class BridgeException : Exception
    {
        public string Code { get; }
        public BridgeException(string code, string message) : base(message) { Code = code; }
    }

    // All game calls happen in the caller's thread. Files go through ModHelper's mod-data helpers, so the mod
    // needs no files capability. A claim marker written before dispatch prevents replay after a crash.
    // ModHelper writes are not atomic: clients must retry a response or heartbeat that does not parse yet.
    // A result answered once the game has played out the request's immediate consequences (Ready), or at its deadline.
    public sealed class Deferred
    {
        public Func<int, bool> Ready; // Rendered frames since dispatch -> settled.
        public Func<JObject> Finish;
        public long TimeoutMs;
    }

    public sealed class FileMailbox
    {
        public const int Protocol = 1;
        public const string ModId = "probably_not_human";
        private readonly string session;
        private readonly Func<string, JObject, object> dispatch;
        private readonly Dictionary<string, long> responses = new Dictionary<string, long>();
        private readonly Func<JObject> readFlow;
        private sealed class Wait { public string Id; public JObject Until; public long Deadline; public int StartFrame; }
        private readonly List<Wait> waits = new List<Wait>();
        private sealed class Settling { public string Id; public Deferred Result; public long Deadline; public int StartFrame; }
        private readonly List<Settling> settling = new List<Settling>();
        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public static string Folder => ModHelper.GetModDataFolder(ModId);
        public FileMailbox(string session, Func<string, JObject, object> dispatch, Func<JObject> readFlow)
        {
            this.session = session; this.dispatch = dispatch; this.readFlow = readFlow;
            foreach (string path in ModHelper.ListModDataFiles(ModId))
            {
                // Responses from an earlier run belong to a dead session; clients already report those as unknown.
                if (path.StartsWith("outbox/", StringComparison.Ordinal)) ModHelper.DeleteModDataFile(ModId, path);
            }
            foreach (string path in ModHelper.ListModDataFiles(ModId))
            {
                if (!path.StartsWith("claims/", StringComparison.Ordinal)) continue;
                string id = Path.GetFileName(path);
                if (Guid.TryParse(id, out _))
                    Respond(id, Error(id, "outcome_unknown", "The game stopped after claiming this request. The action was not replayed; observe before deciding what to do."));
                ModHelper.DeleteModDataFile(ModId, path);
            }
        }
        public void Heartbeat(string gameVersion)
        {
            Write("heartbeat.json", new JObject { ["protocol"] = Protocol, ["session_id"] = session, ["game_version"] = gameVersion, ["updated_at"] = Now });
        }
        public void Stop() { ModHelper.DeleteModDataFile(ModId, "heartbeat.json"); }
        public int Pump(int maximum = 4)
        {
            int processed = 0;
            foreach (string path in ModHelper.ListModDataFiles(ModId))
            {
                if (processed >= maximum) break;
                if (!path.StartsWith("inbox/", StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal)) continue;
                string id = Path.GetFileNameWithoutExtension(path);
                if (!Guid.TryParse(id, out _) || (id.Length != 32 && id.Length != 36)) continue;
                // Clients publish requests by atomic rename, so a listed request is complete.
                string text = ModHelper.ReadModDataFileText(ModId, path);
                if (text == null) continue;
                string claim = "claims/" + id;
                ModHelper.WriteModDataFileText(ModId, claim, "");
                ModHelper.DeleteModDataFile(ModId, path);
                processed++;
                if (ModHelper.ModDataFileExists(ModId, "outbox/" + id + ".json")) { ModHelper.DeleteModDataFile(ModId, claim); continue; }
                JObject response, request = null;
                try
                {
                    using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                        request = JObject.Load(reader);
                    if (request["id"]?.Type != JTokenType.String || (string)request["id"] != id || request["protocol"]?.Type != JTokenType.Integer || (int)request["protocol"] != Protocol)
                        throw new BridgeException("invalid_request", "Request ID must match its filename and protocol must be 1.");
                    if ((string)request["session_id"] != session) throw new BridgeException("stale_session", "Game session changed; observe the new session.");
                    if (request["expires_at"]?.Type != JTokenType.Integer || (long)request["expires_at"] <= Now)
                        throw new BridgeException("expired", "Request deadline passed; no operation was performed.");
                    if (request["method"]?.Type != JTokenType.String || !(request["params"] is JObject parameters)) throw new BridgeException("invalid_request", "method string and params object are required.");
                    if ((string)request["method"] == "wait" && StartWait(id, parameters, Math.Min((long)request["expires_at"], Now + 600000))) continue; // Answered later by PumpWaits; the claim stays until then.
                    object result = (string)request["method"] == "wait" ? WaitResult(parameters["until"] as JObject, readFlow(), 0, true) : dispatch((string)request["method"], parameters);
                    if (result is Deferred deferred)
                    {
                        // Answered later by PumpWaits; the claim stays until then.
                        settling.Add(new Settling { Id = id, Result = deferred, Deadline = Math.Min(Now + deferred.TimeoutMs, (long)request["expires_at"] - 1000), StartFrame = UnityEngine.Time.frameCount });
                        continue;
                    }
                    response = new JObject { ["id"] = id, ["protocol"] = Protocol, ["ok"] = true, ["result"] = (JObject)result };
                }
                catch (BridgeException exception)
                {
                    response = Error(id, exception.Code, exception.Message);
                    if (request?["method"]?.Type == JTokenType.String && (string)request["method"] == "act")
                        try { response["error"]["flow"] = readFlow(); } catch (Exception) { } // The state the refusal was judged in.
                }
                catch (JsonException exception) { response = Error(id, "invalid_request", exception.Message); }
                catch (Exception exception) { response = Error(id, "game_error", exception.GetType().Name + ": " + exception.Message + ". If an action was requested, its outcome may be partial; observe before continuing."); }
                Respond(id, response); // On write failure the claim stays, so a restart reports unknown, never replays.
                ModHelper.DeleteModDataFile(ModId, claim);
            }
            return processed;
        }
        // Returns false when the conditions already hold, so the request is answered immediately.
        private bool StartWait(string id, JObject parameters, long expires)
        {
            if (!(parameters["until"] is JObject until)) throw new BridgeException("invalid_arguments", "until must be an object of flow conditions (see game.flow).");
            if (Flow.Satisfied(until, readFlow(), 0) && until["frames"] == null) return false;
            long timeout = parameters["timeout_ms"]?.Type == JTokenType.Integer ? (long)parameters["timeout_ms"] : 10000;
            waits.Add(new Wait { Id = id, Until = until, Deadline = Math.Min(Now + timeout, expires), StartFrame = UnityEngine.Time.frameCount });
            return true;
        }
        private static JObject WaitResult(JObject until, JObject flow, int frames, bool satisfied) =>
            new JObject { ["satisfied"] = satisfied, ["frames"] = frames, ["flow"] = flow, ["until"] = until };
        // Called every frame: answers waits whose conditions now hold or whose time ran out.
        public void PumpWaits()
        {
            for (int i = settling.Count - 1; i >= 0; i--)
            {
                var pending = settling[i];
                int elapsed = UnityEngine.Time.frameCount - pending.StartFrame;
                JObject response;
                try
                {
                    if (!pending.Result.Ready(elapsed) && Now < pending.Deadline) continue;
                    response = new JObject { ["id"] = pending.Id, ["protocol"] = Protocol, ["ok"] = true, ["result"] = pending.Result.Finish() };
                }
                catch (BridgeException exception) { response = Error(pending.Id, exception.Code, exception.Message); }
                catch (Exception exception) { response = Error(pending.Id, "game_error", exception.GetType().Name + ": " + exception.Message + ". The action ran; observe before continuing."); }
                settling.RemoveAt(i);
                Respond(pending.Id, response);
                ModHelper.DeleteModDataFile(ModId, "claims/" + pending.Id);
            }
            if (waits.Count == 0) return;
            JObject flow = readFlow();
            for (int i = waits.Count - 1; i >= 0; i--)
            {
                var wait = waits[i];
                int frames = UnityEngine.Time.frameCount - wait.StartFrame;
                bool satisfied;
                try { satisfied = Flow.Satisfied(wait.Until, flow, frames); }
                catch (BridgeException exception) { Finish(wait, Error(wait.Id, exception.Code, exception.Message)); continue; }
                if (satisfied || Now >= wait.Deadline)
                    Finish(wait, new JObject { ["id"] = wait.Id, ["protocol"] = Protocol, ["ok"] = true, ["result"] = WaitResult(wait.Until, flow, frames, satisfied) });
            }
        }
        private void Finish(Wait wait, JObject response)
        {
            waits.Remove(wait);
            Respond(wait.Id, response);
            ModHelper.DeleteModDataFile(ModId, "claims/" + wait.Id);
        }
        public void PruneResponses()
        {
            var expired = new List<string>();
            foreach (var pair in responses) if (pair.Value < Now - 3600000) expired.Add(pair.Key);
            foreach (string path in expired) { ModHelper.DeleteModDataFile(ModId, path); responses.Remove(path); }
        }
        private static JObject Error(string id, string code, string message) => new JObject { ["id"] = id, ["protocol"] = Protocol, ["ok"] = false, ["error"] = new JObject { ["code"] = code, ["message"] = message } };
        private void Respond(string id, JObject response)
        {
            string path = "outbox/" + id + ".json";
            Write(path, response);
            responses[path] = Now;
        }
        private static void Write(string path, JObject value) => ModHelper.WriteModDataFileText(ModId, path, value.ToString(Formatting.None));
    }
}

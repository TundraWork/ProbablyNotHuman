using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProbablyNotHuman
{
    public sealed class ModEntry : IMod
    {
        private GameObject host;
        private ModLog log;
        public void Init(ModManifest manifest) { log = new ModLog(manifest); }
        public void OnEnable()
        {
            if (host != null) return;
            host = new GameObject("Probably Not Human");
            UnityEngine.Object.DontDestroyOnLoad(host);
            var input = host.AddComponent<VirtualInput>();
            input.Initialize(log);
            Notices.Start(log);
            host.AddComponent<BridgeRuntime>().StartBridge(log, input, host.AddComponent<ScreenCapturer>());
        }
        public void OnDisable()
        {
            if (host == null) return;
            host.GetComponent<BridgeRuntime>().StopBridge();
            Notices.Stop();
            UnityEngine.Object.Destroy(host);
            host = null;
        }
    }

    public sealed class BridgeRuntime : MonoBehaviour
    {
        private FileMailbox mailbox;
        private GameplayAdapter gameplay;
        private VirtualInput input;
        private SurfaceAdapter surfaces;
        private TradeSurface trades;
        private readonly NightSurface nights = new NightSurface();
        private readonly SaveSurface saves = new SaveSurface();
        private readonly ServiceSurface services = new ServiceSurface();
        private readonly UIAdapter ui = new UIAdapter();
        private readonly string session = Guid.NewGuid().ToString("N");
        private ModLog log;
        private JArray actions;
        private long observation;
        private long noticesObserved;
        private Dictionary<string, int> reputationObserved;
        private float nextPump;
        private float nextHeartbeat;
        private float nextPrune;
        private ScreenCapturer capturer;
        public void StartBridge(ModLog logger, VirtualInput virtualInput, ScreenCapturer screenCapturer)
        {
            capturer = screenCapturer;
            log = logger;
            gameplay = new GameplayAdapter();
            input = virtualInput;
            surfaces = new SurfaceAdapter(gameplay.ResolveItem);
            trades = new TradeSurface(gameplay);
            actions = gameplay.DescribeActions();
            foreach (var action in ui.DescribeActions()) actions.Add(action);
            foreach (var action in surfaces.DescribeActions()) actions.Add(action);
            foreach (var action in trades.DescribeActions()) actions.Add(action);
            foreach (var action in input.DescribeActions()) actions.Add(action);
            foreach (var action in nights.DescribeActions()) actions.Add(action);
            foreach (var action in saves.DescribeActions()) actions.Add(action);
            foreach (var action in services.DescribeActions()) actions.Add(action);
            foreach (var action in capturer.DescribeActions()) actions.Add(action);
            foreach (var action in Modal.DescribeActions()) actions.Add(action);
            mailbox = new FileMailbox(session, Dispatch, () => Flow.Read(input));
            mailbox.Heartbeat(Application.version);
            log.Log("Structured bridge ready at " + FileMailbox.Folder + "; session " + session);
        }
        public void StopBridge()
        {
            try { input?.Stop(); }
            finally { ui.CancelHold(); mailbox?.Stop(); mailbox = null; }
        }
        private void OnDestroy() { StopBridge(); }
        private void OnApplicationQuit() { StopBridge(); }
        private void Update()
        {
            if (mailbox == null) return;
            float now = Time.realtimeSinceStartup;
            try
            {
                if (ui.PumpHold()) observation++;
                Flow.Track();
                GameplayAdapter.PumpDialogue();
                mailbox.PumpWaits();
                if (now >= nextHeartbeat) { mailbox.Heartbeat(Application.version); nextHeartbeat = now + 1; }
                if (now >= nextPump) { mailbox.Pump(); nextPump = now + 0.05f; }
                if (now >= nextPrune) { mailbox.PruneResponses(); nextPrune = now + 60; }
            }
            catch (Exception exception) { nextPump = now + 1; log.Error("Bridge IO failure: " + exception.Message); }
        }
        // include limits the result to the named sections, or to keys of one ("game.customer", "surfaces.calendar"); compact drops geometry, grid cells, UI paths, UI visuals and unreachable controls.
        // Without include: the store sections in a run, saves and ui at the menu; ui and input are asked for by name.
        // A full-screen panel replaces the store sections with its own texts and controls until it closes.
        private JObject Observe(JObject parameters)
        {
            var include = (parameters["include"] as JArray)?.Values<string>().ToList()
                ?? (GameplayAdapter.Active ? new List<string> { "game", "trade", "surfaces", "night" } : new List<string> { "saves", "ui" });
            bool Wanted(string section) => include.Exists(entry => entry == section || entry.StartsWith(section + ".", StringComparison.Ordinal));
            bool compact = parameters["compact"]?.Type == JTokenType.Boolean && (bool)parameters["compact"];
            var result = new JObject { ["session_id"] = session, ["observation_id"] = ++observation, ["scene"] = SceneManager.GetActiveScene().name, ["flow"] = Flow.Read(input) };
            // Pop-up notifications since the previous observation; they fade from the screen within seconds.
            var notices = Notices.Since(noticesObserved);
            noticesObserved = Notices.Sequence;
            if (notices.Count > 0) result["notices"] = notices;
            // Standing changes since the previous observation, including those settled after an action (an arrest in dialogue).
            if (GameplayAdapter.Active)
            {
                var standing = GameplayAdapter.Reputations();
                if (reputationObserved != null && GameplayAdapter.ReputationChange(reputationObserved, standing) is JObject changed) result["reputation_changes"] = changed;
                reputationObserved = standing;
            }
            var modal = Modal.Current;
            if (modal != null)
            {
                var panel = modal.Content != null ? modal.Content() : ui.ObservePanel(modal.Root.transform);
                panel["id"] = modal.Id;
                // Paper panels page and click through surface.paper_page / surface.paper_click, which take these ids.
                var papers = new JArray(SurfaceObservation.Papers().Where(paper => modal.Root.GetComponentsInChildren<PaperUIPanel>().Any(view => view.owner != null && view.owner.Id == (string)paper["id"])));
                if (papers.Count > 0) panel["papers"] = papers;
                var offered = new JArray(modal.Actions);
                if (modal.Close != null) offered.Add("modal.close");
                if (modal.Content == null) offered.Add("ui.click on controls");
                panel["actions"] = offered;
                result["modal"] = panel;
                if (Wanted("input")) result["input"] = input.Observe();
                if (Wanted("saves")) result["saves"] = saves.Observe();
                return result;
            }
            ItemObservation.Compact = compact;
            try
            {
                var only = (parameters["inventories"] as JArray)?.Values<string>().ToList();
                if (Wanted("game")) result["game"] = gameplay.Observe(only == null ? null : new HashSet<string>(only));
                if (Wanted("ui")) result["ui"] = ui.Observe(compact);
                if (Wanted("surfaces")) result["surfaces"] = surfaces.Observe();
                if (Wanted("trade")) result["trade"] = trades.Observe();
                if (Wanted("input")) result["input"] = input.Observe();
                if (Wanted("night")) result["night"] = nights.Observe();
                if (Wanted("saves")) result["saves"] = saves.Observe();
            }
            finally { ItemObservation.Compact = false; }
            // Dotted entries keep only the named keys of a section that was not also asked for whole.
            foreach (var property in result.Properties().ToList())
            {
                if (!(property.Value is JObject section) || include.Contains(property.Name)) continue;
                var keys = include.Where(entry => entry.StartsWith(property.Name + ".", StringComparison.Ordinal)).Select(entry => entry.Substring(property.Name.Length + 1)).ToList();
                if (keys.Count == 0) continue;
                foreach (var key in section.Properties().Select(p => p.Name).ToList()) if (!keys.Contains(key)) section.Remove(key);
            }
            return result;
        }
        private void RefreshInputActions()
        {
            input.RefreshBindings();
            for (int i = actions.Count - 1; i >= 0; i--)
                if (((string)actions[i]["name"]).StartsWith("input.", StringComparison.Ordinal)) actions.RemoveAt(i);
            foreach (var action in input.DescribeActions()) actions.Add(action);
        }
        private object Dispatch(string method, JObject parameters)
        {
            if (method == "describe") RefreshInputActions();
            if (method == "observe") return Observe(parameters);
            if (method == "describe") return new JObject {
                ["protocol"] = 1, ["game_version"] = Application.version, ["build_id"] = PlayerStore.VERSION, ["actions"] = actions.DeepClone(), ["inventory_roles"] = GameplayAdapter.RoleGuide(), ["flow_conditions"] = Flow.Guide(),
                ["transport"] = new JObject { ["kind"] = "local_file_mailbox", ["main_thread"] = true, ["poll_ms"] = 50, ["session_id"] = session },
                ["wait"] = "Method wait {until: {flow condition: value, ...}, timeout_ms} answers once every condition in until matches observation.flow (exact, or {ge: n}/{le: n} for numbers), or at timeout with satisfied=false. \"frames\": n waits n rendered frames."
            };
            if (method != "act") throw new BridgeException("unknown_method", "Supported methods: observe, describe, act.");
            if (parameters["expected_observation"] != null && (parameters["expected_observation"].Type != JTokenType.Integer || (long)parameters["expected_observation"] != observation))
                throw new BridgeException("stale_observation", "Another observation/action superseded the expected observation. Observe again.");
            if (parameters["action"]?.Type != JTokenType.String || !(parameters["arguments"] is JObject args)) throw new BridgeException("invalid_arguments", "action string and arguments object are required.");
            string name = (string)parameters["action"];
            if (name == "input.gesture") RefreshInputActions();
            JObject definition = null;
            foreach (JObject candidate in actions) if ((string)candidate["name"] == name) { definition = candidate; break; }
            if (definition == null)
            {
                if (name == "observe" || name == "describe" || name == "wait" || name == "health")
                    throw new BridgeException("unknown_action", name + " is its own request (tool), not an action: call " + name + " directly.");
                // Suggest actions sharing the verb or the area ("night.sleep" -> day.sleep, night.open...).
                int dot = name.IndexOf('.');
                string area = dot > 0 ? name.Substring(0, dot + 1) : name, verb = dot > 0 ? name.Substring(dot) : name;
                var near = actions.Select(candidate => (string)candidate["name"]).Where(known => known.EndsWith(verb, StringComparison.Ordinal) || known.StartsWith(area, StringComparison.Ordinal)).Take(8).ToList();
                throw new BridgeException("unknown_action", name + (near.Count > 0 ? " is not an action; did you mean " + string.Join(", ", near) + "?" : " is not an action (describe lists them)."));
            }
            ActionSchema.Validate(args, (JObject)definition["arguments"]);
            var modal = Modal.Current;
            if (modal != null && !Modal.Allows(modal, name) && !(name.StartsWith("ui.", StringComparison.Ordinal) && ui.Within((string)args["target"], modal.Root.transform)))
                throw Modal.Blocked(modal);
            var before = GameplayAdapter.Active && name != "item.inspect" && name != "ui.capture" ? gameplay.Snapshot() : null;
            var heard = GameplayAdapter.History();
            long noticesBefore = Notices.Sequence;
            string modalBefore = modal?.Id;
            var panelsBefore = Modal.OpenPanels();
            JObject result;
            try { result = Route(name, args); }
            finally { observation++; ui.Invalidate(); gameplay.InvalidateItems(); } // Native callbacks may rebuild surfaces or move items.
            if (name == "game.quit") return result;
            // Answer once what the action set off has played out (a line typed, a walk-in, a fade, a scene load), at most a few seconds later,
            // with what changed meanwhile: effects, new dialogue lines, a panel that opened, pop-ups and the flow after.
            return new Deferred { TimeoutMs = 7000, Ready = frames => Settled(name, frames), Finish = () =>
            {
                ui.Invalidate(); gameplay.InvalidateItems();
                if (before != null && GameplayAdapter.Active && gameplay.Effects(before, name.StartsWith("night.", StringComparison.Ordinal) || name.StartsWith("day.", StringComparison.Ordinal)) is JObject effects) result["effects"] = effects;
                if (GameplayAdapter.Talk(heard) is JObject talk) result["talk"] = talk;
                if (result["reading"] != null) result["reading"] = SurfaceObservation.Reading(); // Localized by now.
                if (name.StartsWith("phone.", StringComparison.Ordinal) && Modal.Current == null) result["phone"] = ContentObservation.Phone();
                if (result["at_counter"] != null) result["at_counter"] = GameplayAdapter.AtCounter(); // Who stands there now.
                Canvas.ForceUpdateCanvases();
                var covering = Modal.Current;
                if (covering != null && covering.Id != modalBefore) result["modal_opened"] = covering.Id;
                // Item panels that opened without covering the store; they stay open until closed (modal.close {panel}).
                var opened = Modal.OpenPanels().Where(panel => !panelsBefore.Contains(panel) && panel != covering?.Root).Select(Modal.PanelId).ToList();
                if (opened.Count > 0) result["panel_opened"] = new JArray(opened);
                var raised = Notices.Since(noticesBefore);
                if (raised.Count > 0) result["notices"] = raised;
                result["flow"] = Flow.Read(input);
                return result;
            } };
        }
        // Nothing the action started is still in motion: no line typing or auto-advancing, no customer walking in or out, no fade, no virtual input, no scene load.
        private bool Settled(string name, int frames)
        {
            if (frames < 2) return false;
            if (name == "ui.capture") return ScreenCapturer.Pending == 0; // The file is written by the time the result arrives.
            if (name == "customer.shoot" && StoreUIManager.Instance != null && StoreUIManager.Instance.isShootingMode) return false; // The shot and its scene play out first.
            if (name == "save.new" || name == "save.load") return GameplayAdapter.Active && !Flow.Transition;
            if (name == "save.main_menu") return Flow.Read(input)["main_menu"].Value<bool>();
            var store = PlayerStore.IsInstanceExist() ? PlayerStore.Instance : null;
            if (name.StartsWith("phone.", StringComparison.Ordinal))
            {
                var call = DialoguePhoneUIManager.Instance;
                if (call != null && call.currentDialog != null && !call.isWaitingForManualAdvance && !(call.currentDialog.choices?.Count > 0)) return false; // A line is playing.
            }
            bool customerMoving = store != null && (store.currentClientInstance != null ? !Flow.CustomerReady : store.lockNextBellFlag != 0);
            return !GameplayAdapter.Advancing && Flow.DialogueState != "playing" && Flow.PendingDialogueActions == 0 && !Flow.Transition && !input.IsBusy && !customerMoving;
        }
        private JObject Route(string name, JObject args)
        {
            if (name == "modal.close") return Modal.Close(args);
            if (name == "ui.capture") return capturer.Act(args);
            if (name.StartsWith("ui.", StringComparison.Ordinal)) return ui.Act(name, args);
            if (name.StartsWith("input.", StringComparison.Ordinal)) return input.Act(name, args);
            if (name.StartsWith("surface.", StringComparison.Ordinal)) return surfaces.Act(name, args);
            if (name.StartsWith("night.", StringComparison.Ordinal)) return nights.Act(name, args);
            if (name.StartsWith("save.", StringComparison.Ordinal) || name == "game.quit") return saves.Act(name, args);
            if (name.StartsWith("phone.", StringComparison.Ordinal) || name.StartsWith("network.", StringComparison.Ordinal) || name.StartsWith("marketplace.", StringComparison.Ordinal)) return services.Act(name, args);
            if (name == "trade.select" || name == "trade.sell_all" || name == "trade.buy_all" || name == "trade.wholesale" || name == "trade.accept" || name == "trade.confirm" || name == "trade.quote") return trades.Act(name, args);
            return gameplay.Act(name, args);
        }
    }
}

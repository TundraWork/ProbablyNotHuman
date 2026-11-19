using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProbablyNotHuman
{
    // Game-state conditions a client can wait on, each read from the flags the game itself uses.
    // Observations report them as game.flow; the "wait" request answers once all requested values hold.
    internal static class Flow
    {
        private static PlayerStore Store => PlayerStore.IsInstanceExist() ? PlayerStore.Instance : null;

        // StoreBell.RingBell: open store, bell not locked by a customer walking in or out, PlayerStore.CanNextClient.
        internal static bool CustomerCallable
        {
            get
            {
                var store = Store;
                return store != null && store.storeState == PlayerStore.StoreState.OPEN && store.lockNextBellFlag == 0 && store.CanNextClient();
            }
        }
        // StoreClientMono.OnArrived sets isClientArrived once the walk-in ends.
        internal static bool CustomerArrived => Store != null && Store.currentClientInstance != null && Store.isClientArrived;
        // StoreUIManager.OnNextClientArrived starts the main conversation 0.25 s after arrival.
        internal static bool CustomerReady => CustomerArrived && StoreUIManager.Instance != null && !StoreUIManager.Instance.isAboutToStartMainDialog;
        internal static bool NoCustomer => Store != null && Store.currentClientInstance == null && Store.lockNextBellFlag == 0;
        // Nothing more will happen in the counter conversation without player input.
        internal static bool DialogueIdle
        {
            get
            {
                var dialog = DialogUIManager.Instance;
                return dialog == null || dialog.currentDialog == null || dialog.IsChoice() || dialog.isWaitingForManualAdvance;
            }
        }
        // none: no conversation; choice: choices shown; waiting: line finished, needs dialogue.advance; playing: typing or auto-advancing.
        internal static string DialogueState
        {
            get
            {
                var dialog = DialogUIManager.Instance;
                if (dialog == null || dialog.currentDialog == null) return "none";
                return dialog.IsChoice() ? "choice" : dialog.isWaitingForManualAdvance ? "waiting" : "playing";
            }
        }
        // DialogUIManager.OnCurrentTextDisplayed: a line's consequence (goods laid out, a payment, the customer leaving) runs about a second after the line.
        internal static int PendingDialogueActions => DialogUIManager.Instance != null && ModReflect.Get(DialogUIManager.Instance, "scheduledEndActionLines") is System.Collections.ICollection pending ? pending.Count : 0;
        internal static int CounterGoods
        {
            get
            {
                var scene = EmporiumEntry.Instance;
                if (Store == null || scene == null) return 0;
                int count = 0;
                foreach (var item in scene.frontInvinvElement.childItems) if (!GeneralHelper.IsItemOwned(item)) count++;
                foreach (var item in scene.backInvinvElement.childItems) if (!GeneralHelper.IsItemOwned(item)) count++;
                return count;
            }
        }
        // StoreUIManager.BlackScreenSequence: the fade between night and morning, scene changes and shootouts; the morning report opens as it lifts.
        internal static bool Transition => StoreUIManager.Instance != null && StoreUIManager.Instance.blackscreen != null && StoreUIManager.Instance.blackscreen.gameObject.activeSelf;
        internal static string AfterhoursPhase
        {
            get
            {
                var map = MapUIManager.Instance;
                if (map == null) return null;
                return map.IsInTransit ? "travelling" : map.IsOutside ? "at_location" : TransitUI.IsOpen ? "choose_destination" : "in_store";
            }
        }
        internal static bool ResponseChoice
        {
            get
            {
                var scene = EmporiumEntry.Instance;
                return scene != null && ((scene.responseWindow != null && scene.responseWindow.IsVisible()) || (scene.responseWindowClosable != null && scene.responseWindowClosable.IsVisible()));
            }
        }

        // The morning panel clears PlayerStore.nightLogs as it renders them; keep the last complete set per day.
        internal static int ReportDay = -1;
        internal static List<string> ReportEntries = new List<string>();
        private static StoreStation trackedStation; // Rebuilt with each run's scene; PlayerStore is not.
        internal static void Track()
        {
            if (StoreStation.Instance != trackedStation) { trackedStation = StoreStation.Instance; ReportDay = -1; ReportEntries = new List<string>(); }
            var store = Store;
            if (store?.nightLogs == null || store.nightLogs.Count == 0 || StoreStation.Instance == null) return;
            if (ReportDay != StoreStation.Instance.dayCounter || ReportEntries.Count != store.nightLogs.Count)
            {
                ReportDay = StoreStation.Instance.dayCounter;
                ReportEntries = new List<string>(store.nightLogs);
            }
        }

        internal static JObject Read(VirtualInput input) => new JObject {
            ["scene"] = SceneManager.GetActiveScene().name,
            ["active_save"] = GameplayAdapter.Active,
            ["main_menu"] = MainMenuUIController.Instance != null && SceneManager.GetActiveScene().name == "EmporiumMenu",
            ["phase"] = Store?.storeState.ToString(),
            ["customer"] = GameplayAdapter.Active && Store.currentClientInstance != null ? (JToken)Store.currentClientInstance.GetClientBlueprint().displayName : JValue.CreateNull(),
            ["advancing"] = GameplayAdapter.Advancing, ["holding"] = UIAdapter.Current != null && UIAdapter.Current.IsHolding, ["standing"] = Standing(), ["cash"] = GameplayAdapter.Active ? (JToken)Store.playerCash : JValue.CreateNull(), ["day"] = StoreStation.Instance != null ? (JToken)StoreStation.Instance.dayCounter : null,
            ["customer_callable"] = CustomerCallable, ["customer_arrived"] = CustomerArrived, ["customer_ready"] = CustomerReady, ["no_customer"] = NoCustomer,
            ["dialogue_idle"] = DialogueIdle, ["dialogue"] = DialogueState, ["dialogue_actions_pending"] = PendingDialogueActions, ["queued_customers"] = Store?.storeClientManager?.clientStack?.Count ?? 0, ["counter_goods"] = CounterGoods, ["trade_open"] = NegociationUIManager.Instance != null && NegociationUIManager.Instance.IsOpen,
            ["afterhours"] = AfterhoursPhase, ["response_choice"] = ResponseChoice, ["night_menu_open"] = EndOfDayUIManager.Instance != null && EndOfDayUIManager.Instance.IsOpen,
            ["input_idle"] = input == null || !input.IsBusy, ["night_report_day"] = ReportDay, ["captures_pending"] = ScreenCapturer.Pending,
            ["shooting_mode"] = StoreUIManager.Instance != null && StoreUIManager.Instance.isShootingMode, ["transition"] = Transition, ["modal"] = Modal.Current is Modal.Panel modal ? (JToken)modal.Id : JValue.CreateNull() };

        // Every key of until must equal the current value, or satisfy {"ge": n} / {"le": n} for numbers
        // ({"counter_goods": {"ge": 1}} waits for goods); "frames" waits that many rendered frames.
        // Faction reputation by faction name, on every response (perks and thresholds: game.reputations).
        private static JToken Standing()
        {
            if (!GameplayAdapter.Active || Store.storeReputations == null) return JValue.CreateNull();
            var standing = new JObject();
            foreach (var rep in Store.storeReputations) standing[rep.factionDisplay] = rep.GetReputation();
            return standing;
        }
        // What each condition means, for describe.
        internal static JObject Guide() => new JObject {
            ["scene"] = "Active scene name.", ["active_save"] = "A run is loaded and playable.", ["main_menu"] = "The main menu is up.",
            ["phase"] = "Store phase: " + string.Join("/", Enum.GetNames(typeof(PlayerStore.StoreState))) + ".", ["cash"] = "Credits (null outside a run).", ["day"] = "Current day.",
            ["customer"] = "Name of the customer at the counter (arriving, talking or leaving), or null.", ["advancing"] = "dialogue.advance all=true is still advancing.", ["holding"] = "A ui.hold is still being held (its result is in ui.hold).", ["standing"] = "Reputation with each faction; what each level does is in game.reputations (perks with thresholds).",
            ["customer_callable"] = "customer.next would call the next customer now (false when nobody is left in the queue).", ["customer_arrived"] = "A customer stands at the counter.",
            ["customer_ready"] = "The customer at the counter has started their lines.", ["no_customer"] = "Nobody at the counter and nobody walking in or out.",
            ["dialogue_idle"] = "The counter conversation needs input or there is none.", ["dialogue"] = "Counter conversation: none, choice, waiting (line finished) or playing (typing).", ["dialogue_actions_pending"] = "Line consequences scheduled but not yet run (goods laid out, payments, departures); they run about a second after their line.",
            ["queued_customers"] = "Customers still to come today.", ["counter_goods"] = "Items on the front and weighing counters that are not yours (a seller's goods laid out).",
            ["trade_open"] = "The negotiation tablet is open.", ["afterhours"] = "Night trip state: in_store, choose_destination, travelling or at_location.",
            ["response_choice"] = "An item picker window is open (game.response_choice).", ["night_menu_open"] = "The nightly services tablet is open.",
            ["input_idle"] = "No input.gesture is running.", ["night_report_day"] = "Day of the latest nightly report (-1 before the first in this run).",
            ["captures_pending"] = "Screen captures not yet written.", ["shooting_mode"] = "The shotgun is raised.", ["transition"] = "A fade to black is running (sleeping, loading, a shootout); the morning report opens as it lifts.", ["modal"] = "Panel covering the store, or null." };
        // Values a string condition can take; a wait on anything else could never be satisfied.
        private static readonly Dictionary<string, string[]> Choices = new Dictionary<string, string[]> {
            ["phase"] = Enum.GetNames(typeof(PlayerStore.StoreState)),
            ["dialogue"] = new[] { "none", "choice", "waiting", "playing" },
            ["afterhours"] = new[] { "in_store", "choose_destination", "travelling", "at_location" } };
        private static void Check(JProperty condition, JToken current)
        {
            var wanted = condition.Value;
            if (Choices.TryGetValue(condition.Name, out var choices))
            {
                if (wanted.Type != JTokenType.Null && (wanted.Type != JTokenType.String || Array.IndexOf(choices, (string)wanted) < 0))
                    throw new BridgeException("invalid_arguments", condition.Name + " is null or one of: " + string.Join(", ", choices) + ".");
            }
            else if (current.Type == JTokenType.Boolean && wanted.Type != JTokenType.Boolean)
                throw new BridgeException("invalid_arguments", condition.Name + " is true or false.");
            else if (current.Type == JTokenType.Integer && wanted.Type != JTokenType.Integer && !(wanted is JObject))
                throw new BridgeException("invalid_arguments", condition.Name + " is a number; use n, {\"ge\": n} or {\"le\": n}.");
        }
        internal static bool Satisfied(JObject until, JObject now, int framesElapsed)
        {
            // Conditions are flow keys; accept them written as paths too ("flow.day", "game.flow.day").
            foreach (var property in until.Properties().ToList())
            {
                string bare = property.Name.StartsWith("game.flow.", StringComparison.Ordinal) ? property.Name.Substring(10) : property.Name.StartsWith("flow.", StringComparison.Ordinal) ? property.Name.Substring(5) : null;
                if (bare != null && until[bare] == null) property.Replace(new JProperty(bare, property.Value));
            }
            foreach (var property in until.Properties())
            {
                if (property.Name == "frames") continue;
                var current = now[property.Name];
                if (current == null) throw new BridgeException("invalid_arguments", "Unknown wait condition: " + property.Name);
                Check(property, current);
            }
            foreach (var property in until.Properties())
            {
                if (property.Name == "frames") { if (framesElapsed < (int)property.Value) return false; continue; }
                var current = now[property.Name];
                if (current == null) throw new BridgeException("invalid_arguments", "Unknown wait condition: " + property.Name);
                if (property.Value is JObject comparison)
                {
                    if (current.Type != JTokenType.Integer) return false;
                    if (comparison["ge"] != null && (long)current < (long)comparison["ge"]) return false;
                    if (comparison["le"] != null && (long)current > (long)comparison["le"]) return false;
                }
                else if (!JToken.DeepEquals(current, property.Value)) return false;
            }
            return true;
        }
    }
}

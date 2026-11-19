using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProbablyNotHuman
{
    // Full-screen panels that cover the store. While one is up a player can only use that panel,
    // so observations show just the panel and actions outside it are refused until it closes.
    internal static class Modal
    {
        internal sealed class Panel
        {
            public string Id;
            public GameObject Root;
            public Action Close; // null: the panel has no way back to the store (game over).
            public Func<JObject> Content; // A dedicated view instead of the panel's texts and controls.
            public string[] Actions = new string[0]; // Actions the panel itself offers.
        }

        internal static Panel Current
        {
            get
            {
                if (!SurfaceAdapter.ActiveSave) return null;
                var gameOver = GameOverUIManager.Instance;
                if (gameOver != null && SurfaceAdapter.Shown(gameOver.uiPanel))
                    return new Panel { Id = "game_over", Root = gameOver.uiPanel, Close = gameOver.continuePlayButton != null && gameOver.continuePlayButton.activeInHierarchy ? gameOver.ContinuePlayClick : (Action)null };
                // The run summary follows the game-over panel; its buttons return to the main menu or keep playing.
                var summary = ModReflect.GetStatic<GameObject>(typeof(RunSummaryUI), "root");
                if (RunSummaryUI.IsOpen && summary != null) return new Panel { Id = "run_summary", Root = summary };
                var escape = StoreUIManager.Instance?.escapeUIManager;
                if (escape != null && SurfaceAdapter.Shown(escape.menuPanel)) return new Panel { Id = "escape_menu", Root = escape.menuPanel, Close = escape.CloseEscapeMenu };
                var network = ModReflect.GetStatic<CustomUIWindow>(typeof(WildNetworkUI), "window");
                if (WildNetworkUI.IsOpen && network?.root != null)
                    return new Panel { Id = "wilde_network", Root = network.root, Close = WildNetworkUI.Close, Content = CommerceObservation.Network, Actions = new[] { "network.buy" } };
                // The morning report opens the calendar on its nightly report tab; the calendar button opens the same panel.
                var calendar = AdvCalendarUIManager.Instance;
                if (calendar != null && SurfaceAdapter.Shown(calendar.UIPanel))
                    return new Panel { Id = calendar.nightlyReportSection != null && calendar.nightlyReportSection.activeSelf ? "daily_report" : "calendar", Root = calendar.UIPanel, Close = calendar.CloseUI };
                return Covering();
            }
        }

        // Panels opened from items and fixtures (newspaper, notes, guides, scanners, ledgers...) live on the game's panel canvas.
        // One that covers the weighing counter takes the click a player aims at the counter, so it locks the store like the panels above.
        // The trade tablet and the phone are used alongside the counter and keep their own actions.
        private static readonly List<RaycastResult> hits = new List<RaycastResult>();
        private static readonly Vector3[] corners = new Vector3[4];
        private static Panel Covering()
        {
            var canvas = StoreUIManager.Instance?.paperUICanvasObject;
            if (canvas == null || EventSystem.current == null || EmporiumEntry.Instance == null) return null;
            if (EndOfDayUIManager.Instance != null && EndOfDayUIManager.Instance.IsOpen) return null; // The night menu is its own flow (night.*, day.sleep).
            var tablet = NegociationUIManager.Instance?.uiObject;
            var phone = PhoneUIManager.Instance?.phonePanel;
            // Where a player clicks to reach the counter: its view's centre, or the screen centre while it is hidden.
            Vector2 point = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            var counter = (EmporiumEntry.Instance.backInvinvElement as PixelElement)?.handler;
            if (counter != null && counter.gameObject.activeInHierarchy && counter.transform is RectTransform rect && counter.GetComponentInParent<Canvas>() is Canvas counterCanvas)
            {
                rect.GetWorldCorners(corners);
                point = RectTransformUtility.WorldToScreenPoint(ViewGeometry.CameraFor(counterCanvas), (corners[0] + corners[2]) * 0.5f);
            }
            hits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            // The reputation pop-up fades by itself within seconds; what lies under it is what the click reaches.
            var top = hits.Find(hit => !Toast(hit.gameObject));
            if (top.gameObject == null) return null;
            foreach (Transform child in canvas.transform)
            {
                if (!child.gameObject.activeSelf || child.gameObject == tablet || child.gameObject == phone || !top.gameObject.transform.IsChildOf(child)) continue;
                var panel = child.gameObject;
                // As EmporiumEntry's panel sweep: night-trip panels and the darkness and blackscreen overlays are not panels; nor is a backdrop with nothing to read.
                var ui = StoreUIManager.Instance;
                if ((MapUIManager.Instance != null && MapUIManager.Instance.IsAfterhourPanel(panel)) || panel == ui.darknessPanel || (ui.blackscreen != null && panel == ui.blackscreen.gameObject)) return null;
                bool showsText = false;
                foreach (var text in panel.GetComponentsInChildren<TMPro.TMP_Text>()) if (text.isActiveAndEnabled && !string.IsNullOrWhiteSpace(text.text) && !Toast(text.gameObject)) { showsText = true; break; }
                string id = PanelId(panel);
                if (!showsText && id.StartsWith("panel:", StringComparison.Ordinal)) return null; // Known readers count even when they are all pictures.
                return new Panel { Id = id, Root = panel, Close = () => panel.SetActive(false), // StoreUIManager.CloseTopmostEscapeTarget closes these the same way.
                    Content = id == "marketplace" ? CommerceObservation.Marketplace : (Func<JObject>)null,
                    Actions = id == "paper" || id == "book" || id == "multi_page" ? new[] { "surface.paper_page", "surface.paper_click", "surface.book_flip" }
                    : id == "marketplace" ? new[] { "marketplace.fulfill", "marketplace.close" } : new string[0] };
            }
            return null;
        }

        // The reputation pop-up and whatever frames it on the panel canvas, while the reputation panel itself is closed.
        private static bool Toast(GameObject target)
        {
            var reputation = ReputationUIManager.Instance;
            var notifier = reputation?.reputationNotifier;
            if (notifier == null || target == null || SurfaceAdapter.Shown(reputation.uiPanel)) return false;
            var root = notifier.transform;
            var canvas = StoreUIManager.Instance?.paperUICanvasObject;
            while (canvas != null && root.parent != null && root.parent != canvas.transform) root = root.parent;
            if (canvas == null || root.parent != canvas.transform) root = notifier.transform; // Not on the panel canvas: just the pop-up.
            return target.transform == root || target.transform.IsChildOf(root);
        }

        internal static string PanelId(GameObject panel)
        {
            bool Has(GameObject known) => known != null && (known == panel || known.transform.IsChildOf(panel.transform));
            if (Has(PCUIManager.Instance?.panel)) return "marketplace";
            if (Has(PaperUIManager.Instance?.augIdGuide)) return "aug_id_guide";
            if (Has(NewsUIManager.Instance?.uiGameObject)) return "newspaper";
            if (Has(HandnoteUIManager.Instance?.panel)) return "handnote";
            if (Has(MultiPageUIManager.Instance?.panel)) return "multi_page";
            if (Has(PaperUIManager.Instance?.bookPanel)) return "book";
            if (Has(PaperUIManager.Instance?.panel) || panel.GetComponentInChildren<PaperUIPanel>() != null) return "paper";
            // Other panels by their object name: _CigaretteGuide -> panel:cigarette_guide.
            var name = new System.Text.StringBuilder();
            foreach (char c in panel.name.Trim('_', ' '))
            {
                if (char.IsUpper(c) && name.Length > 0 && name[name.Length - 1] != '_') name.Append('_');
                name.Append(c == ' ' ? '_' : char.ToLowerInvariant(c));
            }
            return "panel:" + name;
        }

        // Saves, raw input and screen captures act like a player would regardless of the panel; ui.* is checked against the panel by the caller.
        internal static bool Allows(Panel panel, string action) => Array.IndexOf(panel.Actions, action) >= 0 || action == "modal.close" || action == "ui.capture" || action == "game.quit" ||
            action.StartsWith("save.", StringComparison.Ordinal) || action.StartsWith("input.", StringComparison.Ordinal);

        internal static BridgeException Blocked(Panel panel) =>
            new BridgeException("modal_open", "The " + panel.Id.Replace('_', ' ') + " covers the store; " + (panel.Close != null ? "close it with modal.close" : "use its own controls") + " first.");

        public static JArray DescribeActions() => new JArray(new JObject { ["name"] = "modal.close",
            ["description"] = "Close the panel covering the store (flow.modal: daily_report, calendar, wilde_network, escape_menu, game_over with continue (run_summary has only its own buttons), or an item's panel over the counter such as newspaper, paper, book, handnote, multi_page, panel:<name>). panel closes an open item panel that does not cover the store, by its kind in surfaces.reading (e.g. panel:scratch).",
            ["arguments"] = new JObject { ["type"] = "object", ["properties"] = new JObject { ["panel"] = new JObject { ["type"] = "string" } }, ["additionalProperties"] = false } });

        // Item panels open on the panel canvas, whether or not they cover the counter (the trade tablet, the phone and overlays are not panels).
        internal static List<GameObject> OpenPanels()
        {
            var open = new List<GameObject>();
            var ui = StoreUIManager.Instance;
            var canvas = ui?.paperUICanvasObject;
            if (canvas == null) return open;
            foreach (Transform child in canvas.transform)
            {
                var panel = child.gameObject;
                if (!panel.activeSelf || panel == NegociationUIManager.Instance?.uiObject || panel == PhoneUIManager.Instance?.phonePanel || panel == ui.darknessPanel
                    || (ui.blackscreen != null && panel == ui.blackscreen.gameObject) || (MapUIManager.Instance != null && MapUIManager.Instance.IsAfterhourPanel(panel))) continue;
                if (!PanelId(panel).StartsWith("panel:", StringComparison.Ordinal) || panel.GetComponentsInChildren<TMPro.TMP_Text>().Any(text => text.isActiveAndEnabled && !string.IsNullOrWhiteSpace(text.text) && !Toast(text.gameObject))) open.Add(panel);
            }
            return open;
        }
        private static string OpenList()
        {
            var open = OpenPanels().Select(PanelId).ToList();
            return open.Count == 0 ? "" : " Open panels that leave the store usable: " + string.Join(", ", open) + " (modal.close {panel}).";
        }

        public static JObject Close(JObject args)
        {
            if (args["panel"]?.Type == JTokenType.String)
            {
                string wanted = (string)args["panel"];
                var found = OpenPanels().FirstOrDefault(open => PanelId(open) == wanted);
                if (found == null) throw new BridgeException("precondition_failed", "No open panel " + wanted + "." + OpenList());
                found.SetActive(false); // As StoreUIManager.CloseTopmostEscapeTarget.
                return new JObject { ["dispatched"] = true, ["action"] = "modal.close", ["closed"] = wanted };
            }
            var panel = Current;
            if (panel == null) throw new BridgeException("precondition_failed", "No panel covers the store." + OpenList());
            if (panel.Close == null) throw new BridgeException("precondition_failed", "The " + panel.Id.Replace('_', ' ') + " cannot be closed; use its own controls.");
            panel.Close();
            return new JObject { ["dispatched"] = true, ["action"] = "modal.close", ["closed"] = panel.Id };
        }
    }
}

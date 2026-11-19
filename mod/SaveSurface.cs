using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine.SceneManagement;

namespace ProbablyNotHuman
{
    // Save slots, loading and new runs through the same calls as the main menu's Load Game and New Game screens.
    internal sealed class SaveSurface
    {
        // NewGameData.isInMainMenu stays false after the first PlayerStore initialisation, even in the menu scene.
        private static bool InMainMenu => MainMenuUIController.Instance != null && NewGameData.Instance != null && SceneManager.GetActiveScene().name == "EmporiumMenu";
        private static JObject Define(string name, string description, JObject properties = null, params string[] required) => new JObject {
            ["name"] = name, ["description"] = description,
            ["arguments"] = new JObject { ["type"] = "object", ["properties"] = properties ?? new JObject(), ["required"] = new JArray(required), ["additionalProperties"] = false } };
        public JArray DescribeActions() => new JArray(
            // Earlier days and slot copies use save-file functions that the game restricts to itself; the Load Game
            // screen's own Load and Duplicate buttons (ui.click) remain available for those.
            Define("save.list", "List save slots, start scenarios and difficulties."),
            Define("save.load", "From the main menu, load a slot's latest day.", new JObject { ["slot_id"] = new JObject { ["type"] = "integer" } }, "slot_id"),
            Define("save.new", "From the main menu, start a new run. IDs come from save.list.", new JObject {
                ["scenario_id"] = new JObject { ["type"] = "string" }, ["difficulty_id"] = new JObject { ["type"] = "string" },
                ["skip_intro"] = new JObject { ["type"] = "boolean" }, ["endless"] = new JObject { ["type"] = "boolean" } }, "scenario_id"),
            Define("game.quit", "Exit the game through its own exit path (main menu Exit, or the escape menu's Exit during a run). Progress since the last sleep is not saved."),
            Define("save.main_menu", "Leave the running game for the main menu, as the escape menu's Main Menu button does. Progress since the last sleep is not saved."));

        private static JObject Slot(int id)
        {
            var preview = SaveUIManager.GetPreview(id);
            if (preview == null) return new JObject { ["slot_id"] = id };
            return new JObject { ["slot_id"] = id, ["name"] = preview.GetDisplayName(), ["day"] = preview.dayCount, ["credits"] = preview.credit,
                ["saved_at"] = preview.lastSaveDateTime.ToString("s", CultureInfo.InvariantCulture), ["version"] = preview.version,
                ["start_type"] = preview.startType.ToString(), ["scenario_id"] = preview.startScenarioId };
        }
        public JObject Observe()
        {
            var result = new JObject { ["in_main_menu"] = InMainMenu, ["load_menu_open"] = LoadGameUI.IsOpen,
                ["current_slot"] = PlayerStore.IsInstanceExist() ? PlayerStore.Instance.saveSlotId : -1 };
            var slots = new JArray();
            foreach (int id in SaveUIManager.GetOrderedSlotIds()) slots.Add(Slot(id));
            result["slots"] = slots;
            return result;
        }
        public JObject Act(string action, JObject args)
        {
            switch (action)
            {
                case "save.list":
                {
                    var slots = new JArray();
                    foreach (int id in SaveUIManager.GetOrderedSlotIds()) slots.Add(Slot(id));
                    var scenarios = new JArray();
                    foreach (var scenario in StartingScenario.GetVisible())
                        scenarios.Add(new JObject { ["id"] = scenario.id, ["title"] = scenario.GetTitle(), ["start_type"] = scenario.startType.ToString(),
                            ["description"] = scenario.GetDescription(), ["can_start"] = scenario.CanStart(), ["forces_skip_intro"] = scenario.skipIntro });
                    var difficulties = new JArray();
                    foreach (var difficulty in Difficulty.GetAll())
                        difficulties.Add(new JObject { ["id"] = difficulty.id, ["title"] = difficulty.GetTitle(), ["subtitle"] = difficulty.GetSubtitle(), ["effects"] = new JArray(Difficulty.GetEffectLines(difficulty)) });
                    return new JObject { ["slots"] = slots, ["scenarios"] = scenarios, ["difficulties"] = difficulties };
                }
                case "save.load":
                {
                    if (!InMainMenu) throw new BridgeException("precondition_failed", "Use save.main_menu first.");
                    int id = (int)args["slot_id"];
                    if (SaveUIManager.GetPreview(id) == null) throw new BridgeException("unknown_slot", "No save in slot " + id + ".");
                    if (LoadGameUI.IsOpen) LoadGameUI.Close();
                    NewGameData.Instance.saveSlot = id;
                    NewGameData.Instance.saveFile = null;
                    MainMenuUIController.Instance.StartNewGameClick();
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["slot_id"] = id };
                }
                case "save.new":
                {
                    if (!InMainMenu) throw new BridgeException("precondition_failed", "Use save.main_menu first.");
                    var scenario = StartingScenario.Get((string)args["scenario_id"]);
                    if (scenario == null) throw new BridgeException("unknown_scenario", "Unknown scenario; see save.list.");
                    var data = NewGameData.Instance;
                    data.startType = scenario.startType;
                    data.startScenarioId = scenario.id;
                    if (args["difficulty_id"] != null)
                    {
                        var difficulty = Difficulty.Get((string)args["difficulty_id"]);
                        if (difficulty == null) throw new BridgeException("unknown_difficulty", "Unknown difficulty; see save.list.");
                        data.difficultyId = difficulty.id;
                        data.hardMode = difficulty.IsAtLeast("hard");
                    }
                    data.skipIntro = scenario.skipIntro || (bool?)args["skip_intro"] == true;
                    if (args["endless"] != null) data.endlessMode = (bool)args["endless"];
                    data.saveSlot = -1;
                    data.saveFile = null;
                    data.startingPerks.Clear();
                    if (LoadGameUI.IsOpen) LoadGameUI.Close();
                    MainMenuUIController.Instance.StartNewGameClick();
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["scenario_id"] = scenario.id, ["difficulty_id"] = data.difficultyId };
                }
                case "game.quit":
                    if (InMainMenu) MainMenuUIController.Instance.OnExitClick();
                    else if (StoreUIManager.Instance?.escapeUIManager != null) StoreUIManager.Instance.escapeUIManager.OnExit();
                    else throw new BridgeException("precondition_failed", "Neither the main menu nor a running game is loaded.");
                    return new JObject { ["dispatched"] = true, ["action"] = action };
                case "save.main_menu":
                {
                    if (InMainMenu) return new JObject { ["dispatched"] = false, ["action"] = action, ["already_at_main_menu"] = true };
                    var escape = StoreUIManager.Instance?.escapeUIManager;
                    if (escape == null) throw new BridgeException("precondition_failed", "Not in a running game.");
                    escape.OnMainMenu();
                    return new JObject { ["dispatched"] = true, ["action"] = action };
                }
                default: throw new BridgeException("unknown_action", action);
            }
        }
    }
}

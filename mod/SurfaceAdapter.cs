using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyNotHuman
{
    public sealed class SurfaceAdapter
    {
        private readonly Func<int, GameItem> resolveItem;
        public SurfaceAdapter(Func<int, GameItem> resolveItem) { this.resolveItem = resolveItem; }
        // PlayerStore outlives runs and NewGameData.isInMainMenu is only cleared by a newly built PlayerStore (loading a save),
        // so a run started from the menu after an earlier run would read as no run; the scene decides instead.
        internal static bool ActiveSave => PlayerStore.IsInstanceExist() && NewGameData.Instance != null && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "EmporiumMenu";
        internal static bool Shown(GameObject go) => go != null && go.scene.IsValid() && go.scene.isLoaded && go.activeInHierarchy;
        internal static bool Shown(Component component) => component != null && Shown(component.gameObject) && (!(component is Behaviour b) || b.enabled);
        private static void Require(bool condition, string message) { if (!condition) throw new BridgeException("precondition_failed", message); }
        // Private game state goes through ModReflect, the game's sanctioned accessor.
        internal static T Field<T>(object instance, string name)
        {
            try { return ModReflect.Get<T>(instance, name); }
            catch (ModReflectAccessException error) { throw new BridgeException("unsupported_surface", error.Message); }
        }
        private static void Continue(ItemSelectHandler handler)
        {
            try { ModReflect.Call(handler, "ContinueEquipAfterUse"); }
            catch (ModReflectAccessException error) { throw new BridgeException("unsupported_surface", error.Message); }
        }
        private static JObject Schema(params string[] names)
        {
            var properties = new JObject(); var required = new JArray();
            foreach (string name in names)
            {
                bool integer = name.EndsWith("_id") && name != "paper_id";
                properties[name] = integer ? new JObject { ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = int.MaxValue } : new JObject { ["type"] = "string" };
                required.Add(name);
            }
            return new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
        }
        private static JObject Action(string name, string description, JObject schema) => new JObject { ["name"] = "surface." + name, ["description"] = description, ["arguments"] = schema };
        public JArray DescribeActions() => new JArray {
            Action("equip", "Equip an effectively-owned item through TryEquipItem.", Schema("item_id")),
            Action("select", "Select an item through TrySelectItem; requires no current selection.", Schema("item_id")),
            Action("cancel_item", "Cancel the current selected/equipped item.", Schema()),
            Action("use_selected", "Use the currently selected item on a target (target_id, or item_id) with the normal activation/target validators and equip continuation.", new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject { ["target_id"] = new JObject { ["type"] = "integer" }, ["item_id"] = new JObject { ["type"] = "integer" } } }),
            Action("select_tool", "Select an active negotiation tool or notebook via its normal selection callback. IDs come from surface.tools.", Schema("tool_id")),
            Action("confront_feature", "Click an active feature using the selected negotiation tool/notebook, preserving normal mismatch feedback and lock gates.", Schema("feature_id")),
            Action("select_recipe", "Pass an item from the current picker to the active recipe picker callback.", Schema("item_id")),
            Action("paper_page", "Navigate an active PaperUI to a different zero-based page.", new JObject { ["type"]="object", ["properties"]=new JObject { ["paper_id"]=new JObject { ["type"]="string" }, ["page"]=new JObject { ["type"]="integer", ["minimum"]=0 } }, ["required"]=new JArray("paper_id","page"), ["additionalProperties"]=false }),
            Action("paper_click", "Invoke the normal enabled Button callback of an active paper element.", Schema("paper_id", "element_id")),
            Action("book_flip", "Turn the page of the open multi-page guide (no item_id needed), or of the open book (its item_id), left or right; returns the new page's text.", new JObject { ["type"]="object", ["properties"]=new JObject { ["item_id"]=new JObject { ["type"]="integer" }, ["direction"]=new JObject { ["type"]="string", ["enum"]=new JArray("left","right") } }, ["required"]=new JArray("direction"), ["additionalProperties"]=false })
        };
        public JObject Observe()
        {
            var result = new JObject { ["active_save"] = ActiveSave };
            if (!ActiveSave) return result;
            var handler = ItemSelectHandler.current;
            GameItem selected = handler == null ? null : Field<GameItem>(handler, "currentItem");
            result["item_selection"] = new JObject { ["available"] = handler != null && Shown(handler.gameObject), ["polled_input_enabled"] = handler != null && handler.enabled, ["equipped"] = handler != null && handler.IsEquipped, ["item"] = Reference(selected) };
            result["recipe_picker_active"] = Shown(RecipeSelectHandler.current) && RecipeSelectHandler.current.callback != null;
            result["storage_coordinator_busy"] = MachineStorageCoordinator.IsBusy;
            var tools = new JArray();
            foreach (var component in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (!Shown(component) || (!(component is NegociationElement) && !(component is NotebookElement))) continue;
                if (component is NegociationElement tool && !tool.IsNegociationTool()) continue;
                tools.Add(new JObject { ["id"] = component.GetInstanceID(), ["path"] = UIAdapter.PathOf(component.transform), ["kind"] = component is NotebookElement ? "notebook" : "negotiation_tool", ["tool"] = component is NegociationElement n ? n.GetNegociationToolID() : null, ["selected"] = RTSController.selectedObjects.Contains(component.gameObject), ["previously_selected"] = RTSController.previousSelectedObjects.Contains(component.gameObject) });
            }
            result["tools"] = tools;
            var features = new JArray();
            foreach (var feature in UnityEngine.Object.FindObjectsOfType<ItemFeatureUI>())
                if (Shown(feature) && feature.itemFeature != null) features.Add(new JObject { ["id"] = feature.GetInstanceID(), ["text"] = SurfaceObservation.Text(feature.TMP), ["locked"] = InspectionUIManager.IsConfrontLocked() });
            result["features"] = features;
            result["papers"] = SurfaceObservation.Papers();
            result["scanners"] = SurfaceObservation.Scanners(Reference);
            result["inspection"] = SurfaceObservation.Inspection(Reference);
            result["legacy_paper"] = SurfaceObservation.LegacyPaper(Reference);
            result["reading"] = SurfaceObservation.Reading();
            result["afterhours"] = ContentObservation.Afterhours();
            result["phone"] = ContentObservation.Phone();
            result["multi_page"] = ContentObservation.MultiPage();
            result["scratch"] = ContentObservation.Scratch();
            result["calendar"] = CalendarObservation.Read();
            result["security_case"] = SurfaceObservation.SecurityCase();
            result["marketplace"] = CommerceObservation.Marketplace();
            result["phonebook"] = CommerceObservation.Phonebook();
            result["commissary"] = CommerceObservation.Commissary();
            return result;
        }
        // GameItem's public identity is the same integer used by GameplayAdapter.
        private static JToken Reference(GameItem item) => item == null ? JValue.CreateNull() : new JObject { ["id"] = item.GetUniqueID(), ["name"] = item.GetDisplayName(preferCustomName: true) };
        private static T Find<T>(int id) where T : Component
        {
            foreach (var component in UnityEngine.Object.FindObjectsOfType<T>()) if (component.GetInstanceID() == id) return component;
            throw new BridgeException("unknown_target", "No surface component with this ID.");
        }
        private static PaperUI Paper(string id)
        {
            foreach (var panel in UnityEngine.Object.FindObjectsOfType<PaperUIPanel>())
                if (Shown(panel) && panel.owner != null && panel.owner.Id == id && panel.owner.IsVisible) return panel.owner;
            throw new BridgeException("not_visible", "Paper is not active.");
        }
        public JObject Act(string action, JObject args)
        {
            Require(ActiveSave, "No active game save.");
            if (action == "surface.equip" || action == "surface.select" || action == "surface.use_selected" || action == "surface.select_recipe")
                Require(PlayerStore.Instance.lockAllFlag == 0 && PlayerStore.Instance.lockAllInventoryFlag == 0 && PlayerStore.Instance.lockStorePhysicalFlag == 0 && !PlayerStore.Instance.cinematicMode, "Item interaction is locked by the game.");
            var handler = ItemSelectHandler.current;
            switch (action)
            {
                case "surface.equip":
                case "surface.select":
                {
                    Require(handler != null && Shown(handler.gameObject), "Item selection handler is unavailable.");
                    var item = resolveItem(args.Value<int>("item_id"));
                    Require(item.MaySelectSlotItem() && item.CanSelectSlotItem(), "Item cannot be selected" + (GameplayAdapter.Current?.Interactions(item) ?? "."));
                    if (action == "surface.equip") { Require(GeneralHelper.IsItemEffectivelyOwned(item), "Item is not effectively owned."); handler.TryEquipItem(item); Require(handler.GetEquippedItem() == item, "Equipment was not established."); }
                    else { Require(Field<GameItem>(handler, "currentItem") == null, "Cancel current selection first."); handler.TrySelectItem(item); Require(Field<GameItem>(handler, "currentItem") == item, "Selection was not established."); }
                    break;
                }
                case "surface.cancel_item":
                    Require(handler != null && Shown(handler.gameObject) && Field<GameItem>(handler, "currentItem") != null, "No selected item."); handler.EndSelect(); break;
                case "surface.use_selected":
                {
                    Require(handler != null && Shown(handler.gameObject), "Item selection handler is unavailable.");
                    var source = Field<GameItem>(handler, "currentItem");
                    Require(source != null, "No selected item.");
                    Require(args["target_id"] != null || args["item_id"] != null, "target_id (the item to use the selection on) is required.");
                    var target = resolveItem(args.Value<int>(args["target_id"] != null ? "target_id" : "item_id"));
                    if (source == target) { Require(source.MayActivateSlotItem() && source.CanActivateSlotItem(), "Selected item cannot activate."); source.ActivateSlotItem(); }
                    else { Require(source.MayTarget(target) && source.CanTarget(target), "Selected item cannot target this item."); Require(ItemBehaviourManager.current != null, "Item behaviour manager unavailable."); ItemBehaviourManager.current.Target(source,target); source.Target(target); }
                    if (handler.IsEquipped) Continue(handler); else handler.EndSelect();
                    break;
                }
                case "surface.select_tool":
                {
                    var component = Find<MonoBehaviour>(args.Value<int>("tool_id"));
                    Require(component is NotebookElement || (component is NegociationElement n && n.IsNegociationTool()), "Not a negotiation tool or notebook.");
                    Require(!RTSController.selectedObjects.Contains(component.gameObject), "Tool is already selected.");
                    var world = component.GetComponent<Interactable>(); var ui = component.GetComponent<InteractableUI>();
                    Require(world != null || ui != null, "Tool has no normal selection callback.");
                    if (world != null) Require(!Interactable.isPaused && world.isControlable && world.isAvailable, "Tool is not selectable.");
                    Interactable.DeselectCurrents();
                    if (world != null) world.HandleClick(); else ui.OnSelected();
                    Require(RTSController.selectedObjects.Contains(component.gameObject), "Tool selection did not succeed.");
                    break;
                }
                case "surface.confront_feature":
                {
                    var feature = Find<ItemFeatureUI>(args.Value<int>("feature_id"));
                    Require(feature.itemFeature != null && !InspectionUIManager.IsConfrontLocked(), "Feature confrontation is locked/unavailable.");
                    if (RTSController.selectedObjects.Count > 0) Interactable.DeselectCurrents();
                    Require(RTSController.previousSelectedObjects.Count > 0 && Shown(RTSController.previousSelectedObjects[0]), "No active selected confrontation tool.");
                    var go = RTSController.previousSelectedObjects[0]; var tool = go.GetComponent<NegociationElement>();
                    Require((tool != null && tool.IsNegociationTool()) || go.GetComponent<NotebookElement>() != null, "Selected object cannot confront features.");
                    feature.OnFeatureUIClick(); break;
                }
                case "surface.select_recipe":
                {
                    Require(Shown(RecipeSelectHandler.current) && RecipeSelectHandler.current.callback != null, "No active recipe picker.");
                    var overlay = OverlayHandler.current;
                    var item = resolveItem(args.Value<int>("item_id"));
                    Require(overlay != null && overlay.itemChooseWindow != null && overlay.itemChooseWindow.IsVisible() && overlay.itemChooseInventory != null && overlay.itemChooseInventory.childItems.Contains(item), "Target is not in the current recipe/item picker.");
                    RecipeSelectHandler.current.callback(item);
                    break;
                }
                case "surface.paper_page":
                {
                    var paper = Paper(args.Value<string>("paper_id")); int page = args.Value<int>("page");
                    Require(!paper.EditorMode && page >= 0 && page < paper.PageCount && page != paper.CurrentPage, "Page is unavailable, already current, or editor mode is active."); paper.FlipTo(page); break;
                }
                case "surface.paper_click":
                {
                    var paper = Paper(args.Value<string>("paper_id")); int id = args.Value<int>("element_id"); Button button = null;
                    foreach (var binding in paper.VisibleBindings()) if (binding.go != null && binding.go.GetInstanceID() == id && Shown(binding.go)) button = binding.button;
                    Require(button != null && button.isActiveAndEnabled && button.IsInteractable(), "Paper element is not an enabled button."); button.onClick.Invoke(); break;
                }
                case "surface.book_flip":
                {
                    string direction = args.Value<string>("direction");
                    // Multi-page guides (MultiPageUIManager) turn with their own arrows and need no item.
                    var guide = MultiPageUIManager.Instance;
                    if (guide != null && Shown(guide.panel))
                    {
                        if (direction == "left") { Require(Shown(guide.buttonLeft), "Already on the first page."); guide.FlipLeftClicked(); }
                        else { Require(direction == "right" && Shown(guide.buttonRight), "Already on the last page."); guide.FlipRightClicked(); }
                        return new JObject { ["dispatched"] = true, ["action"] = action, ["reading"] = SurfaceObservation.Reading() };
                    }
                    Require(args["item_id"] != null, "No guide is open; for a book, give its item_id.");
                    var manager = PaperUIManager.Instance; var item = resolveItem(args.Value<int>("item_id"));
                    Require(manager != null && Shown(manager.bookPanel) && PaperUIManager.currentBook == item, "Target is not the currently open book.");
                    if (direction == "left") { Require(BookHelper.CanFlipLeft(item), "Cannot flip left."); manager.BookLeftClick(); }
                    else { Require(direction == "right" && BookHelper.CanFlipRight(item), "Cannot flip right."); manager.BookRightClick(); }
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["reading"] = SurfaceObservation.Reading() };
                }
                default: throw new BridgeException("unknown_action", "Unknown surface action: " + action);
            }
            return new JObject { ["dispatched"] = true, ["action"] = action };
        }
    }
}

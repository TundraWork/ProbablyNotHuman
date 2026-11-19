using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyNotHuman
{
    internal sealed class NightSurface
    {
        private static List<(StoreService service, TabletCheckbox checkbox)> Rows(EndOfDayUIManager manager) => ModReflect.Get<List<(StoreService service, TabletCheckbox checkbox)>>(manager, "rows");
        internal static bool CanSleep
        {
            get
            {
                var manager = EndOfDayUIManager.Instance;
                if (!PlayerStore.IsInstanceExist() || manager == null || !manager.IsOpen) return false;
                var button = ModReflect.Get(manager, "endDayButton") as TabletButton;
                return PlayerStore.Instance.storeState == PlayerStore.StoreState.CLOSED && PlayerStore.Instance.playerLocation == PlayerStore.PlayerLocation.IN_STORE && button != null && button.gameObject.activeInHierarchy && button.button != null && button.button.IsInteractable();
            }
        }
        public JObject Observe()
        {
            var manager = EndOfDayUIManager.Instance;
            var result = new JObject { ["open"] = manager != null && manager.IsOpen, ["can_sleep"] = CanSleep };
            if (manager == null || !manager.IsOpen) return result;
            var services = new JArray();
            if (Rows(manager) is List<(StoreService service, TabletCheckbox checkbox)> rows)
                foreach (var row in rows)
                    if (row.checkbox != null && row.checkbox.gameObject.activeInHierarchy)
                        services.Add(new JObject { ["id"] = row.service.id, ["name"] = row.service.displayName, ["cost"] = row.service.cost, ["selected"] = row.checkbox.IsOn, ["mandatory"] = row.service.isMandatory, ["on_cooldown"] = row.service.IsOnCooldown(), ["toggleable"] = row.checkbox.button != null && row.checkbox.button.IsInteractable(), ["ui_path"] = UIAdapter.PathOf(row.checkbox.transform) });
            result["services"] = services;
            return result;
        }
        public JArray DescribeActions() => new JArray(
            new JObject { ["name"] = "night.open", ["description"] = "Use the normal bedroll to open nightly services after closing the store.", ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false } },
            new JObject { ["name"] = "night.go_out", ["description"] = "After closing, leave through the door: opens the backpack choice (game.response_choice), skipping the clear-the-counter warning.", ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false } },
            new JObject { ["name"] = "night.travel", ["description"] = "From the open transit map, travel to a discovered destination (surfaces.afterhours.discovered_destinations).", ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("destination_id"), ["properties"] = new JObject { ["destination_id"] = new JObject { ["type"] = "string" } } } },
            new JObject { ["name"] = "night.scavenge", ["description"] = "Use one scavenging attempt at the dumping ground; loot lands in afterhours_ground.", ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false } },
            new JObject { ["name"] = "night.return", ["description"] = "Go back to the store. Items still on afterhours_ground are left behind (the game's confirmation is skipped).", ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false } },
            new JObject { ["name"] = "night.service", ["description"] = "Set a shown toggleable nightly service through its native checkbox event. Mandatory/cooldown services cannot be changed.", ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("service_id", "selected"), ["properties"] = new JObject { ["service_id"] = new JObject { ["type"] = "string" }, ["selected"] = new JObject { ["type"] = "boolean" } } } });
        public JObject Act(string action, JObject args)
        {
            var map = MapUIManager.Instance;
            switch (action)
            {
                case "night.go_out":
                    if (map == null) throw new BridgeException("precondition_failed", "No afterhours map in this scene.");
                    if (PlayerStore.Instance.storeState != PlayerStore.StoreState.CLOSED || PlayerStore.Instance.visitLeftTonight <= 0) throw new BridgeException("precondition_failed", "Close the store first; one visit per night.");
                    map.OnGoOutsideConfirm();
                    // The backpack picker's options are items to item.activate (game.response_choice).
                    var picked = new JObject { ["dispatched"] = true, ["action"] = action };
                    var scene = EmporiumEntry.Instance;
                    foreach (var (window, inventory) in new[] { (scene.responseWindow, (GameInventory)scene.responseInventory), (scene.responseWindowClosable, scene.responseInventoryClosable) })
                        if (window != null && inventory != null && window.IsVisible())
                            picked["choose_one"] = new JArray(inventory.childItems.Select(option => new JObject { ["item_id"] = option.GetUniqueID(), ["name"] = option.GetDisplayName(true) }));
                    if (picked["choose_one"] == null) picked["choose_one"] = "wait {\"response_choice\": true}; the options are in game.response_choice";
                    return picked;
                case "night.travel":
                {
                    if (map == null) throw new BridgeException("precondition_failed", "No afterhours map in this scene.");
                    AfterhourLocation target = null;
                    foreach (var location in AfterhourLocation.GetDiscovered()) if (location.Id == (string)args["destination_id"]) target = location;
                    if (target == null) throw new BridgeException("unknown_destination", "Not a discovered destination.");
                    if (!target.IsAvailable()) throw new BridgeException("precondition_failed", "Destination is not available tonight.");
                    if (map.IsInTransit) throw new BridgeException("precondition_failed", "Already travelling.");
                    map.Travel(target);
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["destination_id"] = target.Id };
                }
                case "night.scavenge":
                    if (map == null || map.Current == null || map.Current.Id != "dumping_ground") throw new BridgeException("precondition_failed", "Not at the dumping ground.");
                    if (PlayerStore.Instance.healthData.isWoundedFresh) throw new BridgeException("precondition_failed", "You were wounded tonight; scavenging is over until tomorrow night.");
                    if (!ScavHelper.CanScavenge()) throw new BridgeException("precondition_failed", "No scavenging attempts left tonight.");
                    map.ScavengeButtonClick();
                    // The outcome (loot on afterhours_ground, a wound, an empty search) is in the result's effects and notices.
                    var ground = new JArray();
                    foreach (var item in EmporiumEntry.Instance.afterhourInventory.childItems) ground.Add(new JObject { ["item_id"] = item.GetUniqueID(), ["name"] = item.GetDisplayName(true), ["count"] = item.unitCount });
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["attempts_left"] = ScavHelper.GetScavTimeLeft(), ["wounded"] = PlayerStore.Instance.healthData.isWoundedFresh, ["on_ground"] = ground };
                case "night.return":
                    // From the destination map, before travelling: close it as Escape does (StoreUIManager.ResolveEscape), staying home.
                    if (map != null && map.Current == null && TransitUI.IsOpen && !map.IsInTransit)
                    {
                        map.CloseUI();
                        return new JObject { ["dispatched"] = true, ["action"] = action, ["closed"] = "destination map" };
                    }
                    if (map == null || map.Current == null) throw new BridgeException("precondition_failed", map != null && map.IsInTransit ? "Still travelling: wait {\"afterhours\": \"at_location\"}." : "Not out on a night trip.");
                    map.ReturnNow();
                    return new JObject { ["dispatched"] = true, ["action"] = action };
            }
            if (!SurfaceAdapter.ActiveSave || PlayerStore.Instance.storeState != PlayerStore.StoreState.CLOSED || PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE || PlayerStore.Instance.lockAllFlag != 0 || PlayerStore.Instance.cinematicMode)
                throw new BridgeException("precondition_failed", "Finish closing the store and return from afterhours first.");
            if (action == "night.open")
            {
                foreach (var bed in UnityEngine.Object.FindObjectsOfType<StoreBedroll>())
                    if (bed.isActiveAndEnabled)
                    {
                        bed.OnClick();
                        return new JObject { ["dispatched"] = true, ["action"] = action };
                    }
                throw new BridgeException("not_visible", "No active bedroll in this scene.");
            }
            if (action != "night.service") throw new BridgeException("unknown_action", action);
            var manager = EndOfDayUIManager.Instance;
            if (manager == null || !manager.IsOpen) throw new BridgeException("precondition_failed", "Open the nightly services tablet.");
            if (Rows(manager) is List<(StoreService service, TabletCheckbox checkbox)> rows)
                foreach (var row in rows)
                    if (row.service.id == (string)args["service_id"] && row.checkbox != null && row.checkbox.gameObject.activeInHierarchy)
                    {
                        if (row.service.isMandatory || row.service.IsOnCooldown() || row.checkbox.button == null || !row.checkbox.button.IsInteractable())
                            throw new BridgeException("precondition_failed", "This service is mandatory, on cooldown, or disabled.");
                        bool desired = (bool)args["selected"];
                        bool changed = row.checkbox.IsOn != desired;
                        if (changed) row.checkbox.Toggle();
                        return new JObject { ["action"] = action, ["service_id"] = row.service.id, ["changed"] = changed, ["selected"] = row.checkbox.IsOn };
                    }
            throw new BridgeException("not_visible", "Service is not shown in the current nightly menu.");
        }
    }
}

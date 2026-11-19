using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace ProbablyNotHuman
{
    // Context is read from the game's existing controllers, not inferred from screenshots.
    internal static class ContentObservation
    {
        internal static JObject Afterhours()
        {
            var map = MapUIManager.Instance;
            var result = new JObject { ["available"] = map != null };
            if (map == null || !SurfaceAdapter.ActiveSave) return result;
            result["outside"] = map.IsOutside;
            result["travelling"] = map.IsInTransit;
            result["transit_open"] = TransitUI.IsOpen;
            result["phase"] = map.IsInTransit ? "travelling" : map.IsOutside ? "at_location" : TransitUI.IsOpen ? "choose_destination" : "in_store";
            result["current_location"] = map.Current == null ? null : Location(map.Current);
            result["last_visited_tonight"] = map.LastVisitedTonightId;
            result["visits_remaining"] = PlayerStore.Instance.visitLeftTonight;
            // A fresh wound ends tonight's scavenging; woundState counts toward a serious wound (HealthData.SERIOUS_WOUND_THRESHOLD).
            var health = PlayerStore.Instance.healthData;
            result["health"] = new JObject { ["wounded_tonight"] = health.isWoundedFresh, ["wound_level"] = health.woundState, ["serious_wound_level"] = HealthData.SERIOUS_WOUND_THRESHOLD, ["wound_stable"] = health.isWoundStable };
            if (map.Current != null && map.Current.Id == "dumping_ground")
                result["scavenging"] = new JObject { ["can_scavenge"] = ScavHelper.CanScavenge(), ["attempts_left"] = ScavHelper.GetScavTimeLeft(),
                    ["minor_wound_percent"] = ScavHelper.GetMinorWoundChance(), ["major_wound_percent"] = ScavHelper.GetMajorWoundChance() };

            var bag = map.IsOutside ? EmporiumEntry.Instance.afterhourPocketSlotInvBackpack?.childItem : EmporiumEntry.Instance.selectedBackpack;
            result["selected_backpack"] = bag == null ? null : new JObject { ["id"] = bag.GetUniqueID(), ["name"] = bag.GetDisplayName(true) };
            var picker = OverlayHandler.current;
            if (picker != null && picker.itemChooseWindow != null && picker.itemChooseWindow.IsVisible())
                result["item_choice_title"] = picker.itemChooseWindow.titleString;
            var locations = new JArray();
            if (TransitUI.IsOpen)
                foreach (var location in AfterhourLocation.GetDiscovered())
                    locations.Add(Location(location));
            result["discovered_destinations"] = locations;
            return result;
        }
        private static JObject Location(AfterhourLocation location)
        {
            var events = new JArray();
            foreach (var entry in location.ActiveEvents)
                events.Add(new JObject { ["id"] = entry.id, ["title"] = entry.GetTitle(),
                    ["description"] = entry.GetDescription(), ["days_remaining"] = AfterhourEvents.GetDaysLeft(entry.id),
                    ["closes_location"] = entry.closesLocation, ["effects"] = new JArray(entry.GetEffectTexts()) });
            return new JObject { ["id"] = location.Id, ["name"] = location.GetTitle(),
                ["available"] = location.IsAvailable(), ["events"] = events };
        }
        internal static JObject Phone()
        {
            var phone = PhoneUIManager.Instance;
            var result = new JObject { ["open"] = phone != null && SurfaceAdapter.Shown(phone.phonePanel) };
            if (phone == null || !SurfaceAdapter.Shown(phone.phonePanel)) return result;
            result["number_display"] = SurfaceObservation.Text(phone.numberDisplay);
            result["status"] = SurfaceObservation.Text(phone.statusDisplay);
            var dialogue = DialoguePhoneUIManager.Instance;
            // Choices come from the dialog itself: their bubbles only appear a moment after the line has played.
            var choices = new JArray();
            var current = dialogue?.currentDialog;
            if (current?.choices != null)
                for (int i = 0; i < current.choices.Count; i++)
                    choices.Add(new JObject { ["index"] = i, ["text"] = current.choices[i].Item2?.plainText, ["enabled"] = current.IsChoiceEnabledAt(i) });
            result["dialogue"] = new JObject {
                ["active"] = dialogue != null && dialogue.currentDialog != null,
                ["waiting_for_advance"] = dialogue != null && dialogue.isWaitingForManualAdvance,
                ["speaker"] = dialogue == null ? null : SurfaceObservation.Text(dialogue.clientNameTMP),
                ["text"] = dialogue?.currentDialog?.plainText,
                ["last_line"] = dialogue == null || (dialogue.IsCurrentDialogEnd() && choices.Count == 0),
                ["choices"] = choices };
            return result;
        }
        internal static JObject MultiPage()
        {
            var manager = MultiPageUIManager.Instance;
            var result = new JObject { ["open"] = manager != null && SurfaceAdapter.Shown(manager.panel) };
            if (manager == null || !SurfaceAdapter.Shown(manager.panel)) return result;
            result["page_label"] = SurfaceObservation.Text(manager.pageNumberText);
            result["can_previous"] = SurfaceAdapter.Shown(manager.buttonLeft);
            result["can_next"] = SurfaceAdapter.Shown(manager.buttonRight);
            foreach (var book in manager.books)
                if (SurfaceAdapter.Shown(book))
                {
                    result["book_object"] = book.name;
                    result["page_count"] = book.transform.childCount;
                    for (int i = 0; i < book.transform.childCount; i++)
                        if (book.transform.GetChild(i).gameObject.activeSelf) result["page_index"] = i;
                    var text = new JArray();
                    foreach (var label in book.GetComponentsInChildren<TMP_Text>())
                        if (SurfaceAdapter.Shown(label)) text.Add(label.text);
                    result["shown_text"] = text;
                    break;
                }
            return result;
        }
        internal static JObject Scratch()
        {
            var manager = ScratcherUIManager.Instance;
            var result = new JObject { ["open"] = manager != null && SurfaceAdapter.Shown(manager.ui) };
            if (manager == null || !SurfaceAdapter.Shown(manager.ui) || manager.currentActiveScratcherItem == null) return result;
            var item = manager.currentActiveScratcherItem;
            result["item_id"] = item.GetUniqueID();
            result["back_shown"] = manager.back != null && manager.back.activeSelf;
            var panels = new JArray();
            for (int slot = 1; slot <= 3; slot++)
            {
                panels.Add(new JObject { ["slot"] = slot, ["revealed"] = item.GetTagReadonly("SCRATCHER_STATE" + slot).GetInt() == 1, ["symbol_id"] = item.GetTagReadonly("SCRATCHER_SYMBOL" + slot).GetInt(), ["ui_path"] = UIAdapter.PathOf(manager.scratchInputs[slot - 1].transform) });
            }
            result["panels"] = panels;
            result["fully_scratched"] = ScratcherHelper.IsFullyScratched(item);
            result["can_redeem"] = ScratcherHelper.CanRedeemPrize(item);
            result["prize_value"] = item.unitValue;
            return result;
        }
    }
}

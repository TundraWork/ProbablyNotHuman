using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyNotHuman
{
    internal static class CommerceObservation
    {

        // Wilde's network window, shown as its modal: one row per upgrade, details only where they can still matter.
        internal static JObject Network()
        {
            var store = PlayerStore.Instance;
            int funds = store.playerCash + store.wildFavor;
            var upgrades = new JArray();
            foreach (var upgrade in store.networkUpgrade.Values)
            {
                var row = new JObject { ["id"] = upgrade.id, ["name"] = upgrade.GetLocalizedTitle() };
                upgrades.Add(row);
                if (upgrade.IsUnlocked()) { row["status"] = "bought"; continue; }
                int cost = upgrade.GetCost();
                row["category"] = upgrade.GetLocalizedSubtitle();
                row["cost"] = cost;
                // IsReady order: prerequisites, then cooldown.
                if (!upgrade.IsHavePrerequisite()) { row["status"] = "locked"; row["needs"] = upgrade.GetMissingPrerequisite(); }
                else if (!upgrade.IsReady()) { row["status"] = "cooldown"; row["cooldown_days"] = upgrade.cooldownCurrent; }
                else row["status"] = cost <= funds ? "buyable" : "unaffordable";
                if (upgrade.isRepeatable) row["repeatable"] = true;
                row["description"] = upgrade.GetLocalizedDescription();
            }
            // WildNetworkUI.OnUseClicked spends favor first, then cash; these are not two separate prices.
            return new JObject { ["cash"] = store.playerCash, ["wild_favor"] = store.wildFavor, ["payment"] = "wild_favor_first_then_cash", ["upgrades"] = upgrades };
        }

        internal static JObject Marketplace()
        {
            var manager = PCUIManager.Instance;
            var result = new JObject { ["open"] = manager != null && SurfaceAdapter.Shown(manager.panel) };
            if (!SurfaceAdapter.ActiveSave) return result;
            result["unlocked"] = NetworkUpgrade.IsMarketplaceRequestsUnlocked();
            result["pc_state"] = manager.storePC.state;
            var requests = new JArray();
            // PCUIManager.OnChange displays exactly this active list, never StoreRequestList's future candidates.
            var elements = SurfaceAdapter.Shown(manager.panel) ? SurfaceAdapter.Field<List<StoreRequestElement>>(manager, "storeRequestElements") : null;
            foreach (var request in PlayerStore.Instance.storeRequests)
            {
                var observation = new JObject
                {
                    ["id"] = request.id,
                    ["title"] = request.title,
                    ["description"] = request.description,
                    ["requester_name"] = request.requesterName,
                    ["faction_id"] = request.factionId,
                    ["faction_name"] = request.GetFactionDisplayName(),
                    ["requirements"] = new JObject
                    {
                        ["item_id"] = request.requiredItem,
                        ["item_display_name"] = request.useCustomItemName ? request.requestedItem : GeneralHelper.GetDisplayNameFromID(request.requiredItem),
                        ["count"] = request.requiredItemCount
                    },
                    ["reward"] = new JObject { ["credits"] = request.creditReward, ["reputation"] = request.reputationReward, ["faction_id"] = request.factionId },
                    // StoreStation.HandleStoreRequests decrements this once per day and removes at zero.
                    ["expiry_days_remaining"] = request.duration,
                    ["can_fulfill"] = request.CanFillRequest()
                };
                observation["display"] = request.requiredItemCount + " " + observation["requirements"]["item_display_name"].Value<string>();
                if (elements != null)
                    foreach (var element in elements)
                        if (element.storeRequest == request && SurfaceAdapter.Shown(element))
                        {
                            observation["display"] = SurfaceObservation.Text(element.requestedItem);
                            observation["reward_display"] = SurfaceObservation.Text(element.reward);
                            observation["expiry_display"] = SurfaceObservation.Text(element.duration);
                            observation["fulfill_control"] = new JObject { ["visible"] = SurfaceAdapter.Shown(element.acceptButton), ["enabled"] = SurfaceAdapter.Shown(element.acceptButton) && element.acceptButton.IsInteractable(), ["ui_path"] = UIAdapter.PathOf(element.acceptButton.transform) };
                            break;
                        }
                requests.Add(observation);
            }
            result["requests"] = requests;
            return result;
        }
        internal static JObject Commissary()
        {
            var manager = CommissaryUIManager.Instance;
            var result = new JObject { ["open"] = manager != null && SurfaceAdapter.Shown(manager.panel) };
            if (!(bool)result["open"]) return result;
            var data = PlayerStore.Instance.commissaryData;
            result["vouchers"] = data.redeemedCredit;
            var products = new JArray();
            foreach (var element in manager.elementParent.GetComponentsInChildren<CommissaryElement>())
                products.Add(new JObject { ["id"] = element.id, ["name"] = SurfaceObservation.Text(element.productName),
                    ["voucher_price"] = data.GetItemData(element.id).price, ["stock"] = data.GetStock(element.id),
                    ["can_buy"] = CommissaryUIManager.CanBuy(element.id),
                    ["buy_control"] = UIAdapter.PathOf(element.buyButton.transform) });
            result["products"] = products;
            return result;
        }

        internal static JObject Phonebook()
        {
            var phone = PhoneUIManager.Instance;
            var book = phone == null ? null : phone.phoneBookUIManager;
            var result = new JObject { ["open"] = SurfaceAdapter.Shown(book) };
            if (!SurfaceAdapter.ActiveSave) return result;
            var contacts = new JArray();
            var elements = SurfaceAdapter.Shown(book) ? book.layoutGroup.GetComponentsInChildren<ContactElement>() : null;
            foreach (var entry in PlayerStore.Instance.PhoneClientDict)
            {
                var client = entry.Value;
                var contact = new JObject
                {
                    ["id"] = client.dialogFuncId, // Stable across languages: "Wild" is Wilde's assistant.
                    ["shown_in_phonebook"] = client.ShownInPhoneBook(),
                    ["name"] = client.GetLocalizedDisplayName(),
                    ["number"] = entry.Key,
                    ["state"] = client.phoneState.ToString(),
                    ["callable"] = PhoneUIManager.WillAnswerCall(entry.Key),
                    ["service_ready"] = client.phoneState == StorePhoneClient.PhoneState.Regular,
                    ["cooldown_days_remaining"] = client.currentCooldown
                };
                if (elements != null)
                    foreach (var element in elements)
                        if (element.number == entry.Key && SurfaceAdapter.Shown(element))
                        {
                            var button = element.GetComponentInChildren<Button>(true);
                            contact["status_display"] = SurfaceObservation.Text(element.statusTMP);
                            contact["call_control"] = new JObject { ["visible"] = SurfaceAdapter.Shown(button), ["enabled"] = SurfaceAdapter.Shown(button) && button.IsInteractable(), ["ui_path"] = button == null ? null : UIAdapter.PathOf(button.transform) };
                            break;
                        }
                contacts.Add(contact);
            }
            result["contacts"] = contacts;
            return result;
        }
    }
}

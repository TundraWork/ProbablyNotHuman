using Newtonsoft.Json.Linq;

namespace ProbablyNotHuman
{
    // Phone calls and Wilde's network through the same calls as the phone keypad, phonebook and network window.
    internal sealed class ServiceSurface
    {
        private static JObject Define(string name, string description, JObject properties = null, params string[] required) => new JObject {
            ["name"] = name, ["description"] = description,
            ["arguments"] = new JObject { ["type"] = "object", ["properties"] = properties ?? new JObject(), ["required"] = new JArray(required), ["additionalProperties"] = false } };
        public JArray DescribeActions() => new JArray(
            Define("phone.call", "Open the wall phone and quick-dial a number, as the phonebook does. Numbers come from surfaces.phonebook.", new JObject { ["number"] = new JObject { ["type"] = "integer" } }, "number"),
            Define("phone.advance", "Advance the current phone conversation line."),
            Define("phone.choose", "Pick a phone dialogue choice by index (surfaces.phone.dialogue.choices).", new JObject { ["index"] = new JObject { ["type"] = "integer", ["minimum"] = 0 } }, "index"),
            Define("phone.hang_up", "End the call and put the phone down."),
            Define("network.buy", "Buy a Wilde network upgrade by ID while his network is open (flow.modal wilde_network; phone.call his assistant, surfaces.phonebook id Wild, and choose to browse). The catalog is in observe's modal. Spends Wilde favor first, then credits.", new JObject { ["upgrade_id"] = new JObject { ["type"] = "string" } }, "upgrade_id"),
            Define("marketplace.open", "Open the store PC's marketplace requests (surfaces.marketplace), as clicking the PC does."),
            Define("marketplace.close", "Close the store PC's marketplace requests."),
            Define("marketplace.fulfill", "Fill a marketplace request by id (surfaces.marketplace.requests) with its accept button, opening the PC first if needed; the game takes the required owned items and pays the reward.", new JObject { ["request_id"] = new JObject { ["type"] = "string" } }, "request_id"));

        public JObject Act(string action, JObject args)
        {
            var phone = PhoneUIManager.Instance;
            var talk = DialoguePhoneUIManager.Instance;
            switch (action)
            {
                case "phone.call":
                    if (phone == null) throw new BridgeException("precondition_failed", "No phone in this scene.");
                    phone.OpenUI();
                    phone.AutoCall((long)args["number"]);
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["answers"] = PhoneUIManager.WillAnswerCall((long)args["number"]) };
                case "phone.advance":
                    if (talk == null || talk.currentDialog == null) throw new BridgeException("precondition_failed", "No phone conversation.");
                    talk.OnAdvancePressed();
                    break;
                case "phone.choose":
                {
                    int index = (int)args["index"];
                    if (talk == null || talk.currentDialog == null) throw new BridgeException("precondition_failed", "No phone conversation.");
                    if (!talk.currentDialog.IsChoiceEnabledAt(index)) throw new BridgeException("precondition_failed", "Choice is not enabled.");
                    talk.SelectChoice(index);
                    break;
                }
                case "phone.hang_up":
                    if (phone == null) throw new BridgeException("precondition_failed", "No phone in this scene.");
                    phone.CloseUI();
                    break;
                case "network.buy":
                {
                    // WildNetworkUI.OnUseClicked, without selecting the slot first.
                    if (!WildNetworkUI.IsOpen) throw new BridgeException("precondition_failed", "Open Wilde's network first: phone his assistant and choose to browse.");
                    var store = PlayerStore.Instance;
                    string id = (string)args["upgrade_id"];
                    if (store == null || store.networkUpgrade == null || !store.networkUpgrade.TryGetValue(id, out var upgrade)) throw new BridgeException("unknown_upgrade", "Unknown upgrade " + id + ".");
                    if (upgrade.IsUnlocked()) throw new BridgeException("precondition_failed", "Already unlocked.");
                    if (!upgrade.IsReady()) throw new BridgeException("precondition_failed", "Not ready: " + upgrade.GetMissingPrerequisite());
                    int cost = upgrade.GetCost();
                    if (cost > store.playerCash + store.wildFavor) throw new BridgeException("precondition_failed", "Costs " + cost + "; favor plus credits is " + (store.playerCash + store.wildFavor) + ".");
                    int cashSpent = 0;
                    if (store.wildFavor >= cost) store.wildFavor -= cost;
                    else { cashSpent = cost - store.wildFavor; store.wildFavor = 0; store.playerCash -= cashSpent; }
                    RunStats.OnWildUpgradeBought(cashSpent);
                    upgrade.Unlock();
                    WildNetworkUI.RefreshAll();
                    return new JObject { ["action"] = action, ["upgrade_id"] = id, ["cost"] = cost, ["credits_spent"] = cashSpent };
                }
                case "marketplace.open":
                case "marketplace.close":
                case "marketplace.fulfill":
                {
                    var pc = PCUIManager.Instance;
                    if (pc == null || pc.storePC == null) throw new BridgeException("precondition_failed", "No store PC in this scene.");
                    if (!NetworkUpgrade.IsMarketplaceRequestsUnlocked()) throw new BridgeException("precondition_failed", "Marketplace requests are not unlocked (Wilde's network).");
                    bool open = pc.panel != null && pc.panel.activeInHierarchy;
                    if (action == "marketplace.close") { if (open) pc.HideUI(); break; }
                    if (!open) pc.storePC.OnClick(); // StorePC.OnClick toggles the panel and clears the new-request mark.
                    if (action == "marketplace.open") break;
                    string id = (string)args["request_id"];
                    var request = PlayerStore.Instance.storeRequests.Find(candidate => candidate.id == id);
                    if (request == null) throw new BridgeException("precondition_failed", "No active request " + id + "; active: " + string.Join(", ", PlayerStore.Instance.storeRequests.ConvertAll(candidate => candidate.id)) + ".");
                    if (!request.CanFillRequest())
                    {
                        int owned = EmporiumEntry.Instance.GetAllOwnedItems().FindAll(request.CanThisItemFillRequest).Count;
                        throw new BridgeException("precondition_failed", "The request needs " + request.requiredItemCount + " owned items that fill it; you own " + owned + ".");
                    }
                    var elements = SurfaceAdapter.Field<System.Collections.Generic.List<StoreRequestElement>>(pc, "storeRequestElements");
                    var element = elements?.Find(candidate => candidate != null && candidate.storeRequest == request);
                    if (element == null) throw new BridgeException("precondition_failed", "The request is not shown on the PC.");
                    element.OnClick();
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["request_id"] = id, ["filled"] = !PlayerStore.Instance.storeRequests.Contains(request) };
                }
                default: throw new BridgeException("unknown_action", action);
            }
            return new JObject { ["dispatched"] = true, ["action"] = action };
        }
    }
}

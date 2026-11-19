using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyNotHuman
{
    // Match the negotiation tablet's actual actions, including its bulk primary buttons.
    internal sealed class TradeSurface
    {
        // The tablet's private state and row helpers, read through ModReflect.
        private static T Get<T>(string member) where T : class => ModReflect.Get(Manager, member) as T;
        private static List<GameItem> Rows(string helper, params object[] args) => (List<GameItem>)ModReflect.CallStatic(typeof(NegociationUIManager), helper, args);
        private readonly GameplayAdapter gameplay;
        public TradeSurface(GameplayAdapter gameplay) { this.gameplay = gameplay; }
        private static NegociationUIManager Manager => NegociationUIManager.Instance;
        private static string Mode => ModReflect.Get(Manager, "mode")?.ToString();
        private static TabletButton Primary => Get<TabletButton>("primaryButton");
        private static TabletButton Secondary => Get<TabletButton>("secondaryButton");
        // The tablet's yes/no panel: selling more than the budget asks whether to let everything go for the budget.
        private static GameObject ConfirmRoot => (ModReflect.Get(Manager, "frame") as NegotiationTabletFrame)?.confirmRoot;
        private static bool Confirming => ConfirmRoot != null && ConfirmRoot.activeInHierarchy;
        private static bool Enabled(TabletButton button) => button != null && button.gameObject.activeInHierarchy && button.Interactable && button.button != null && button.button.IsInteractable();
        private static JObject Button(TabletButton button, string action)
        {
            if (button == null || !button.gameObject.activeInHierarchy) return null;
            return new JObject { ["action"] = action, ["label"] = button.label?.text, ["enabled"] = Enabled(button), ["ui_path"] = UIAdapter.PathOf(button.transform) };
        }
        private static string PrimaryAction(string mode)
        {
            switch (mode)
            {
                case "Buying": return "trade.sell_all"; // Game modes name the customer's side.
                case "Selling": return "trade.buy_all";
                case "Barter": case "InfoDealer": case "GunOrder": return "trade.accept";
                default: return null;
            }
        }
        public JObject Observe()
        {
            var result = new JObject { ["open"] = Manager != null && Manager.IsOpen };
            var store = PlayerStore.IsInstanceExist() ? PlayerStore.Instance : null;
            var client = store?.currentClientInstance?.GetClientBlueprint();
            // Counter terms come from the game's own checks and need no open tablet.
            if (GameplayAdapter.Active) result["at_counter"] = GameplayAdapter.AtCounter();
            if (client != null && Manager != null) result["counter"] = Counter(client);
            if (Manager == null || !Manager.IsOpen) return result;
            string mode = Mode;
            result["view_mode"] = mode == "Buying" ? "customer_buys" : mode == "Selling" ? "customer_sells" : mode;
            result["primary"] = Button(Primary, PrimaryAction(mode));
            result["secondary"] = Button(Secondary, mode == "Selling" ? "trade.wholesale" : null);
            var selected = Get<GameItem>("selectedItem");
            result["selected_item_id"] = selected == null ? JValue.CreateNull() : new JValue(selected.GetUniqueID());
            var weighed = store?.currentNegociatedItem;
            result["weighed_item_id"] = weighed == null ? JValue.CreateNull() : new JValue(weighed.GetUniqueID());
            var listed = new JArray();
            if (Get<List<GameItem>>("listedItems") is List<GameItem> items)
                foreach (var item in items) listed.Add(item.GetUniqueID());
            result["listed_item_ids"] = listed;
            if (Confirming && client != null && !Manager.IsClientHaveEnoughToBuy()) result["confirmation"] = new JObject { ["text"] = SurfaceObservation.Text((ModReflect.Get(Manager, "frame") as NegotiationTabletFrame).confirmText),
                ["how"] = "trade.confirm {accept: true} sells every eligible counter item although they exceed the budget (the customer may pay less than their value; see effects); false cancels" };
            if (client == null) return result;
            if (mode == "Barter" && client.barterOffer != null)
                result["barter"] = new JObject { ["description"] = client.barterOffer.barterTextBody, ["offer_index"] = client.barterOffer.currentSetIndex, ["offer_count"] = client.barterOffer.OfferSetCount(), ["acceptable"] = client.barterOffer.IsBarterAcceptable() };
            if (mode == "InfoDealer") result["service_cost"] = client.serviceCost;
            return result;
        }
        // What trade.sell_all / trade.buy_all / trade.wholesale would do with the weighing counter right now.
        private JObject Counter(StoreClient client)
        {
            var store = PlayerStore.Instance;
            var scene = EmporiumEntry.Instance;
            var faction = store.storeReputations?.FirstOrDefault(rep => rep.factionId == client.clientFaction);
            // How full the weighing counter is, whatever is on it (yours, the customer's).
            var scale = scene.backInvinvElement;
            var occupancy = new JObject { ["owned_items"] = scale.childItems.Count(GeneralHelper.IsItemOwned), ["other_items"] = scale.childItems.Count(item => !GeneralHelper.IsItemOwned(item)) };
            if (scale is GameGridInventory scaleGrid) { occupancy["free_cells"] = GameplayAdapter.FreeCellsOf(scaleGrid); occupancy["largest_free"] = GameplayAdapter.LargestFreeRect(scaleGrid); }
            var summary = GameplayAdapter.ClientSummary(client);
            summary["shootable"] = GameplayAdapter.Shootable;
            var counter = new JObject { ["customer"] = summary, ["weighing_counter"] = occupancy,
                ["your_standing_with_their_faction"] = faction == null ? null : new JObject { ["faction"] = faction.factionDisplay, ["reputation"] = faction.GetReputation() }, ["weighing_counter_id"] = gameplay.InventoryIdOf(scene.backInvinvElement), ["customer_buying"] = client.IsClientBuying(), ["customer_selling"] = client.IsClientSelling(),
                ["budget"] = client.GetBudget(), ["uses_budget"] = client.useClientBudget, ["requires_variety"] = client.isRequireVariety,
                ["variety_bought"] = client.varietyTrackingList == null ? new JArray() : new JArray(client.varietyTrackingList) };
            // Owned counter goods: the ones this customer takes in full, the rest only by name and the game's reason.
            var mine = new JArray(); var refused = new JArray();
            foreach (var item in Rows("GetOwnedBackInvItems"))
            {
                string reason = Manager.GetNotBuyingReason(item);
                if (!client.IsClientBuyingThisItem(item)) { refused.Add(new JObject { ["item_id"] = item.GetUniqueID(), ["name"] = item.GetDisplayName(true), ["reason"] = string.IsNullOrEmpty(reason) ? null : reason }); continue; }
                mine.Add(new JObject { ["item_id"] = item.GetUniqueID(), ["name"] = item.GetDisplayName(true), ["price"] = item.GetNegociatedValue(),
                    // trade.sell checks single_sale_allowed; trade.sell_all sells every listed item, exposures included.
                    ["single_sale_allowed"] = store.CanSellThisItem(item), ["would_expose"] = client.CanClientExposeAnyFeatureDeep(item), ["reason"] = string.IsNullOrEmpty(reason) ? null : reason });
            }
            // NegociationUIManager.OnSellAllClick: over budget opens the shortfall confirmation instead of selling.
            int sellTotal = Manager.GetMultiSellValue();
            // Prices here are this customer's terms on the weighing counter; wanted_stock values are before those terms.
            // A customer who does not buy (a barterer, a pure seller) has no sale; barter terms are in trade.barter.
            if (client.IsClientBuying())
            {
                counter["sell"] = new JObject { ["items"] = mine, ["total"] = sellTotal, ["over_budget"] = mine.Count > 0 && !Manager.IsClientHaveEnoughToBuy() };
                if (refused.Count > 0) counter["sell"]["not_wanted"] = refused;
            }
            // Owned stock elsewhere that this buyer would take (fixed fittings excluded): move it onto the weighing counter (item.transfer to weighing_counter_id) to offer it.
            if (client.IsClientBuying())
            {
                var wanted = new JArray();
                foreach (var item in gameplay.OwnedItems())
                    if (item.parentInventory != scene.backInvinvElement && item.MayRemove() && GameplayAdapter.InStock(item) && gameplay.RoleOf(item.parentInventory) != "trash" && client.IsClientBuyingThisItem(item)) // Fixtures cannot be taken out; the bin's contents are being thrown away.
                    {
                        var row = new JObject { ["item_id"] = item.GetUniqueID(), ["name"] = item.GetDisplayName(true), ["value"] = item.GetNegociatedValue(), ["count"] = item.unitCount,
                            ["at"] = gameplay.RoleOf(item.parentInventory) ?? gameplay.InventoryIdOf(item.parentInventory) };
                        // A container's price includes what is in it; its contents are listed too, marked with the container.
                        if (item.contentWindow?.child is GameInventory inside && inside.childItems.Count > 0) { row["holds_items"] = inside.childItems.Count; row["price_includes_contents"] = true; }
                        if (GraphUtils.FindParentType<GameItem>(item.parentInventory) is GameItem holder && holder.contentWindow?.child == item.parentInventory) row["inside"] = holder.GetUniqueID();
                        wanted.Add(row);
                    }
                // Identical stacks (same name, value and place) as one row with their ids.
                // One row per kind of item (name and value), with every id; where each lies is in game.inventories.
                counter["wanted_stock"] = new JArray(wanted.GroupBy(row => (string)row["name"] + "|" + (string)row["value"] + "|" + (row["holds_items"] != null))
                    .Select(group => { var first = group.First(); var row = new JObject { ["name"] = first["name"], ["value"] = first["value"], ["count"] = group.Sum(item => (int)item["count"]), ["item_ids"] = new JArray(group.Select(item => item["item_id"])) };
                        if (first["holds_items"] != null) { row["holds_items"] = first["holds_items"]; row["price_includes_contents"] = true; } return row; }));
                // Some buyers' interests are set during their opening lines; until then they have none and nothing matches.
                if (client.clientBuyingTagList.Count == 0 && client.clientBuyingIdList.Count == 0 && DialogUIManager.Instance?.currentDialog != null)
                    counter["interests_pending"] = true;
            }
            // The seller's whole lot, on either counter; trade.wholesale takes all of it at wholesale.price, trade.buy_all the weighing-counter part.
            var theirs = new JArray(); long buyTotal = 0, buyValue = 0;
            foreach (var item in scene.GetAllNonOwnedItem())
            {
                theirs.Add(new JObject { ["item_id"] = item.GetUniqueID(), ["name"] = item.GetDisplayName(true), ["count"] = item.unitCount, ["price"] = item.GetNegociatedValue(), ["true_value"] = ItemObservation.TrueValue(item),
                    ["at"] = item.parentInventory == scene.backInvinvElement ? "weighing_counter" : "front_counter" });
                buyTotal += item.GetNegociatedValue(); buyValue += ItemObservation.TrueValue(item);
            }
            counter["buy"] = new JObject { ["items"] = theirs, ["total"] = buyTotal, ["true_value_total"] = buyValue, ["affordable"] = buyTotal <= store.playerCash, ["multi_buy_disabled"] = client.isMultiBuyDisabled };
            // PlayerStore.RefreshCounterItem prices counter goods without the seller's onItemAddedToWeighted terms; those apply when the item is weighed.
            if (theirs.Any(row => (string)row["at"] != "weighing_counter")) counter["buy"]["price_note"] = "Prices of items not on the weighing counter can leave out terms the seller adds when an item is weighed (a supplier's markup); trade.weigh shows the price they ask.";
            // Today's market price effects by item type (the newspaper's events), for pricing at the counter.
            var market = new JArray();
            foreach (var entry in StoreStation.Instance.storeEventManager.GetActiveEventOnDay(StoreStation.Instance.dayCounter))
                foreach (var effect in entry.negociationDatas)
                    if (effect.value != 0) market.Add(new JObject { ["event"] = entry.GetDisplayName(), ["item_type"] = effect.itemType, ["percent"] = effect.value });
            if (market.Count > 0) counter["market_today"] = market;
            counter["wholesale"] = new JObject { ["offered"] = client.isOfferWholesale, ["price"] = store.GetWholesalePrice(), ["discount_percent"] = client.wholesaleDiscount };
            if (BargainUIManager.Instance != null) counter["bargain"] = Bargain(client, store);
            return counter;
        }
        private static readonly int[] BargainPercents = { 10, 25, 50, 75 };
        private static readonly int[] MarkupBudgetCut = { 10, 15, 20, 25 };
        // BargainUIManager.OfferMarkup / DemandBuyingDiscount roll RNG 0..99: below accept succeeds, below stay refuses and stays,
        // otherwise the customer leaves (-3 faction reputation). Discounts and buying markups are always accepted.
        private static JObject Bargain(StoreClient client, PlayerStore store)
        {
            var bargain = BargainUIManager.Instance;
            int rep = (int)ModReflect.CallStatic(typeof(BargainUIManager), "GetFactionRepBonus", client.clientFaction);
            int bonus = rep + (bargain.IsEventCustomer() ? 15 : 0);
            JObject Rolled(bool available)
            {
                var options = new JObject();
                for (int i = 0; i < BargainPercents.Length; i++)
                {
                    int percent = BargainPercents[i];
                    int accept = client.alwaysRejectBadBargain ? 0 : Mathf.RoundToInt(100f - percent * 2f + bonus + store.dealMakerBargainBonus);
                    int stay = Mathf.RoundToInt(100f - percent * 1.2f + bonus);
                    accept = Mathf.Clamp(accept, 0, 100);
                    int leave = client.staysAfterFailedBargain ? 0 : 100 - Mathf.Clamp(Mathf.Max(stay, accept), 0, 100);
                    options[percent.ToString()] = new JObject { ["accept_percent"] = accept, ["refuse_stay_percent"] = 100 - accept - leave, ["leave_percent"] = leave, ["budget_cut_percent"] = MarkupBudgetCut[i] };
                    if (leave > 0) options[percent.ToString()]["leave_reputation_change"] = -3; // BargainUIManager: a customer who walks out costs their faction 3.
                }
                return new JObject { ["available"] = available && !client.isAug, ["options"] = options };
            }
            var discounts = new JObject();
            foreach (int percent in BargainPercents) discounts[percent.ToString()] = new JObject { ["budget_raise_percent"] = percent * 2 }; // BargainUIManager.OfferDiscount, once the customer has a budget (uses_budget).
            return new JObject {
                ["markup"] = Rolled(bargain.CanOfferMarkup()),
                ["buying_discount"] = Rolled(bargain.CanDemandBuyingDiscount()),
                ["discount"] = new JObject { ["available"] = bargain.CanOfferDiscount(), ["options"] = discounts, ["always_accepted"] = true },
                ["buying_markup"] = new JObject { ["available"] = bargain.CanOfferBuyingMarkup(), ["always_accepted"] = true },
                ["augmented_customer"] = client.isAug };
        }
        public JArray DescribeActions()
        {
            JObject Define(string name, string description, JObject properties = null, bool optional = false)
            {
                properties = properties ?? new JObject();
                var required = new JArray(); if (!optional) foreach (var property in properties.Properties()) required.Add(property.Name);
                return new JObject { ["name"] = name, ["description"] = description, ["arguments"] = new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false } };
            }
            return new JArray(
                Define("trade.select", "Select/expand a currently listed negotiation item through the real row callback.", new JObject { ["item_id"] = new JObject { ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = int.MaxValue } }),
                Define("trade.sell_all", "Use the tablet's Sell primary button for the eligible counter set, including mixed customers. When the counter is worth more than the budget the tablet asks first (trade.confirmation, answered by trade.confirm); accept_over_budget=true answers yes in the same call.", new JObject { ["accept_over_budget"] = new JObject { ["type"] = "boolean" } }, optional: true),
                Define("trade.buy_all", "Use the tablet's Buy primary button for the current listed counter set; normal single-purchase restrictions and funds checks apply."),
                Define("trade.wholesale", "Accept the currently enabled wholesale offer through the real tablet callback."),
                Define("trade.accept", "Accept the current enabled barter, information service, or gunsmith delivery through the real tablet callback."),
                Define("trade.quote", "Without moving anything: for each of your items, whether this customer would buy it and the game's reason if not, with the item's value (the counter price applies this customer's terms; trade.weigh gives it).", new JObject { ["item_ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["items"] = new JObject { ["type"] = "integer" } } }),
                Define("trade.confirm", "Answer the tablet's over-budget question (trade.confirmation) that trade.sell_all opens when the counter is worth more than the budget: true sells everything eligible anyway (the customer may pay less than its value), false cancels.", new JObject { ["accept"] = new JObject { ["type"] = "boolean" } }));
        }
        public JObject Act(string action, JObject args)
        {
            if (Manager == null || !PlayerStore.IsInstanceExist() || PlayerStore.Instance.currentClientInstance == null || NegociationUIManager.IsTradeLocked())
                throw new BridgeException("precondition_failed", NegociationUIManager.IsTradeLocked() ? "Trading is locked during an arrest or a shootout." : "No customer to trade with right now.");
            // Opening the tablet is only a step for the eyes: do it as part of the trade.
            if (!Manager.IsOpen) Manager.OpenUI();
            var client = PlayerStore.Instance.currentClientInstance.GetClientBlueprint();
            if (client.isBusinessComplete) throw new BridgeException("precondition_failed", "Customer's business is already complete.");
            if (action == "trade.quote")
            {
                var quotes = new JArray();
                foreach (var token in (JArray)args["item_ids"])
                {
                    GameItem item;
                    try { item = gameplay.ResolveItem((int)token); }
                    catch (BridgeException) { quotes.Add(new JObject { ["item_id"] = token, ["error"] = "unknown_item" }); continue; }
                    string refusal = GameplayAdapter.SellRefusal(client, item);
                    var quote = new JObject { ["item_id"] = item.GetUniqueID(), ["name"] = item.GetDisplayName(true), ["value"] = item.GetNegociatedValue(), ["buys"] = refusal == null };
                    if (refusal != null) quote["reason"] = refusal;
                    quotes.Add(quote);
                }
                return new JObject { ["dispatched"] = true, ["action"] = action, ["quotes"] = quotes };
            }
            if (action == "trade.confirm")
            {
                if (!Confirming) throw new BridgeException("precondition_failed", "No over-budget question is open.");
                if ((bool)args["accept"]) Manager.OnConfirmPanelYes(); else Manager.OnConfirmPanelNo();
                return new JObject { ["dispatched"] = true, ["action"] = action, ["accepted"] = (bool)args["accept"] };
            }
            if (action == "trade.select")
            {
                var item = gameplay.ResolveItem((int)args["item_id"]);
                if (!(Get<List<GameItem>>("listedItems") is List<GameItem> items) || !items.Contains(item))
                    throw new BridgeException("not_listed", "Item is not in the current negotiation list.");
                ModReflect.Call(Manager, "OnRowClick", item);
            }
            else if (action == "trade.wholesale")
            {
                if (!client.isOfferWholesale) throw new BridgeException("precondition_failed", "This customer does not sell wholesale: buy items one at a time with trade.buy {item_id}.");
                // The tablet builds its wholesale button only when it is rebuilt with goods present; rebuild it as the game does after item changes.
                // NegociationUIManager.BuildSelling enables the button on exactly these conditions; its handler is called directly, so a tablet not yet rebuilt does not matter.
                int price = PlayerStore.Instance.GetWholesalePrice(), cash = PlayerStore.Instance.playerCash;
                if (EmporiumEntry.Instance.GetAllNonOwendItemCount() == 0) throw new BridgeException("precondition_failed", "The seller's goods are not laid out yet (trade.counter.buy.items is empty).");
                if (price > cash) throw new BridgeException("precondition_failed", "Wholesale costs " + price + "; you have " + cash + ".");
                if (NegociationUIManager.IsTradeLocked()) throw new BridgeException("precondition_failed", "Trading is locked during an arrest or a shootout.");
                Manager.OnWholesaleClick();
            }
            else
            {
                // The tablet may have been built before this customer's trade was set up; rebuild it as the game does.
                if (PrimaryAction(Mode) != action || !Enabled(Primary)) Manager.RefreshUI();
                if (PrimaryAction(Mode) != action || !Enabled(Primary))
                {
                    string current = PrimaryAction(Mode);
                    throw new BridgeException("precondition_failed", current == null ? "The tablet offers no trade with this customer (mode " + Mode + ", intent " + client.clientIntent + ")."
                        : current != action ? "The tablet's trade with this customer is " + current + ", not " + action + "." + (action == "trade.buy_all" && client.IsClientSelling() ? " Their goods can be bought with trade.wholesale or trade.buy {item_id}." : "")
                        : action == "trade.accept" && Mode == "Barter" && client.barterOffer != null && !client.barterOffer.IsBarterAcceptable()
                            ? "The barter's conditions are not met by what is on the weighing counter: " + string.Join("; ", client.barterOffer.conditions.Select(condition => condition.requiredDisplay + " -> " + condition.offeredDisplay)) + "."
                        : action == "trade.accept" && Mode == "InfoDealer" && client.serviceCost > PlayerStore.Instance.playerCash ? "The information costs " + client.serviceCost + "; you have " + PlayerStore.Instance.playerCash + "."
                        : action != "trade.sell_all" ? action + " is disabled right now."
                        : Rows("GetOwnedBackInvItems").Any(client.IsClientBuyingThisItem) ? "trade.sell_all is disabled right now: the customer cannot pay for what is on the weighing counter (budget " + client.GetBudget() + ")."
                        : "trade.sell_all is disabled right now: nothing on the weighing counter is something this customer buys.");
                }
                switch (action)
                {
                    case "trade.sell_all":
                    {
                        // A question left open from an earlier customer is not this sale's: close it before asking anew.
                        if (Confirming) Manager.OnConfirmPanelNo();
                        var offered = Rows("GetOwnedBackInvItems").ToDictionary(item => item, item => item.GetNegociatedValue());
                        var exposing = offered.Keys.Where(client.CanClientExposeAnyFeatureDeep).Select(item => item.GetDisplayName(true)).ToList();
                        Manager.OnSellAllClick();
                        if (Confirming && args["accept_over_budget"]?.Type == JTokenType.Boolean && (bool)args["accept_over_budget"]) Manager.OnConfirmPanelYes();
                        if (Confirming) return new JObject { ["dispatched"] = true, ["action"] = action, ["sold"] = false, ["over_budget"] = true, ["budget"] = client.GetBudget(), ["counter_value"] = Manager.GetMultiSellValue(),
                            ["question_open"] = true, ["confirmation"] = "trade.confirm {accept: true} sells every eligible counter item although they exceed the budget (the customer may pay less than their value); false cancels" };
                        // What went and at what price; what stayed and why (SellRefusal follows the game's own checks).
                        var sold = new JArray(); var kept = new JArray();
                        foreach (var pair in offered)
                        {
                            if (pair.Key == null || pair.Key.parentInventory != EmporiumEntry.Instance.backInvinvElement || !GeneralHelper.IsItemOwned(pair.Key))
                                sold.Add(new JObject { ["name"] = pair.Key?.GetDisplayName(true), ["price"] = pair.Value });
                            else kept.Add(new JObject { ["item_id"] = pair.Key.GetUniqueID(), ["name"] = pair.Key.GetDisplayName(true), ["reason"] = GameplayAdapter.SellRefusal(client, pair.Key) ?? "not sold" });
                        }
                        var grouped = new JArray(sold.GroupBy(row => (string)row["name"] + "|" + (string)row["price"]).Select(group => new JObject { ["name"] = group.First()["name"], ["price"] = group.First()["price"], ["count"] = group.Count() }));
                        var soldResult = new JObject { ["dispatched"] = true, ["action"] = action, ["sold"] = grouped, ["kept"] = kept };
                        if (exposing.Count > 0) soldResult["exposed_by_customer"] = new JArray(exposing); // Items the customer could expose a feature of.
                        return soldResult;
                    }
                    case "trade.buy_all": Manager.OnBuyAllClick(); break;
                    case "trade.accept": Manager.OnOfferAcceptClick(); break;
                    default: throw new BridgeException("unknown_action", action);
                }
            }
            return new JObject { ["dispatched"] = true, ["action"] = action };
        }
    }
}

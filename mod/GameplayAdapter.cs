using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyNotHuman
{
    public sealed class GameplayAdapter
    {
        private readonly Dictionary<GameInventory, string> inventoryIds = new Dictionary<GameInventory, string>();
        private readonly Dictionary<string, GameInventory> knownInventories = new Dictionary<string, GameInventory>();
        private readonly Dictionary<int, GameItem> knownItems = new Dictionary<int, GameItem>();
        private readonly Dictionary<GameInventory, GameItem> owners = new Dictionary<GameInventory, GameItem>();
        private int refreshFrame = -1;

        internal static bool Active => SurfaceAdapter.ActiveSave && StoreUIManager.Instance != null &&
            EmporiumEntry.Instance != null && StoreStation.Instance != null;

        // inventories: only these inventory ids or roles get their items listed (null: all).
        public JObject Observe(HashSet<string> inventories = null)
        {
            var result = new JObject { ["active_save"] = Active };
            Refresh();
            if (!Active) return result;
            var store = PlayerStore.Instance;
            result["store"] = new JObject { ["name"] = store.storeName, ["player"] = store.playerName,
                ["save_slot"] = store.saveSlotId,
                ["day"] = StoreStation.Instance.dayCounter, ["phase"] = store.storeState.ToString(),
                ["location"] = store.playerLocation.ToString(), ["cash"] = store.playerCash,
                ["rent"] = store.GetRentValue(), ["days_until_rent"] = store.GetRentDayCounter(),
                ["mortgage_weekly_payment"] = store.mortgageWeeklyPayment,
                ["mortgage_weeks_left"] = store.mortgageWeekLeft, ["paying_mortgage"] = store.isPayingMortgage,
                ["property_owned"] = store.IsPropertyPaid, ["power_on"] = store.isPowerOn,
                ["garbage_level"] = store.garbageLevel, ["wild_favor"] = store.wildFavor, ["retail_markup_percent"] = store.retailMarkup, ["shooting_unlocked"] = store.isShootingUnlocked,
                ["gutterflow_purity_percent"] = StoreStation.Instance.todayGutterflowPurity / 100.0,
                ["rustwater_purity_percent"] = StoreStation.Instance.todayRustwaterPurity / 100.0,
                ["locks"] = new JObject { ["all"] = store.lockAllFlag, ["inventory"] = store.lockAllInventoryFlag,
                    ["physical"] = store.lockStorePhysicalFlag, ["customer"] = store.lockClientClickFlag,
                    ["next_customer"] = store.lockNextBellFlag, ["shutter"] = store.lockShutterButtonFlag } };
            result["store"]["can_open"] = store.storeState == PlayerStore.StoreState.MORNING && store.CanStartDay();
            result["store"]["can_close"] = store.storeState == PlayerStore.StoreState.OPEN && store.CanEndDay();
            result["store"]["can_call_customer"] = Flow.CustomerCallable;
            result["store"]["remaining_customers"] = store.storeClientManager == null ? 0 : store.storeClientManager.clientStack.Count;
            var queue = new JArray();
            if (store.storeClientManager?.clientStack != null)
                foreach (var queued in store.storeClientManager.clientStack) queue.Add(Client(queued));
            result["customer_queue"] = queue;
            result["store"]["can_sleep"] = NightSurface.CanSleep;
            // The run's difficulty as the new-game screen describes it, and the rule values behind it.
            var difficulty = Difficulty.Current;
            result["store"]["difficulty"] = new JObject { ["id"] = difficulty.id, ["title"] = difficulty.GetTitle(), ["effects"] = new JArray(Difficulty.GetEffectLines(difficulty)), ["rules"] = JObject.FromObject(difficulty.rules) };
            var reputations = new JArray();
            if (store.storeReputations != null)
                foreach (var rep in store.storeReputations)
                {
                    // What each standing does: the faction's perks (negative thresholds are penalties), as the reputation panel lists them.
                    var perks = new JArray();
                    if (rep.perks != null)
                        foreach (var perk in rep.perks.OrderBy(p => p.perkThreshold))
                            perks.Add(new JObject { ["threshold"] = perk.perkThreshold, ["name"] = perk.GetLocalizedPerkName(), ["description"] = perk.GetLocalizedDescription(), ["active"] = perk.isUnlocked || perk.isAlwaysActive });
                    reputations.Add(new JObject { ["faction"] = rep.factionId, ["name"] = rep.factionDisplay, ["reputation"] = rep.GetReputation(), ["perks"] = perks });
                }
            result["reputations"] = reputations;
            var inventoryList = new JArray();
            var summary = new JArray();
            foreach (var pair in knownInventories)
            {
                string listedRole = InventoryRole(pair.Value);
                // Every inventory in brief, so a filtered view still shows where everything is.
                var brief = new JObject { ["id"] = pair.Key, ["role"] = listedRole, ["items"] = pair.Value.childItems.Count };
                if (owners.TryGetValue(pair.Value, out var briefOwner)) brief["owner_item_id"] = briefOwner.GetUniqueID();
                if (pair.Value is GameGridInventory briefGrid) { brief["free_cells"] = FreeCells(briefGrid); brief["largest_free"] = LargestFreeRect(briefGrid); }
                summary.Add(brief);
                if (inventories != null && !inventories.Contains(pair.Key) && (listedRole == null || !inventories.Contains(listedRole))) continue;
                var items = new JArray();
                foreach (var item in pair.Value.childItems) items.Add(ItemObservation.Read(item));
                var inv = new JObject { ["id"] = pair.Key, ["identifier"] = pair.Value.identifier, ["items"] = items };
                inv["role"] = InventoryRole(pair.Value);
                if (owners.TryGetValue(pair.Value, out var owner)) { inv["owner_item_id"] = owner.GetUniqueID(); inv["owner_identifier"] = owner.identifier; }
                inv["kind"] = pair.Value is GameGridInventory ? "grid" : pair.Value is GameSlotInventory ? "slot" : "inventory";
                if (pair.Value is PixelElement view && view.handler != null)
                {
                    inv["shown"] = view.handler.gameObject.activeInHierarchy;
                    inv["window_title"] = view.handler.FindParentWindow()?.titleString;
                    if (!ItemObservation.Compact) { inv["ui_path"] = UIAdapter.PathOf(view.handler.transform); inv["geometry"] = ViewGeometry.Read(view.handler); }
                }
                if (pair.Value is GameGridInventory grid)
                {
                    if (!ItemObservation.Compact) inv["grid"] = ItemObservation.ReadShape(grid.inventoryShape, true);
                    inv["free_cells"] = FreeCells(grid);
                }
                inventoryList.Add(inv);
            }
            result["inventories"] = inventoryList;
            result["inventory_summary"] = summary;
            // Response windows: backpack choice before going out, supply crate contents and similar pickers.
            result["response_choice"] = JValue.CreateNull();
            var scene = EmporiumEntry.Instance;
            foreach (var (window, inventory) in new[] { (scene.responseWindow, (GameInventory)scene.responseInventory), (scene.responseWindowClosable, scene.responseInventoryClosable) })
                if (window != null && inventory != null && window.IsVisible())
                {
                    var options = new JArray();
                    foreach (var option in inventory.childItems) options.Add(new JObject { ["item_id"] = option.GetUniqueID(), ["name"] = option.GetDisplayName(true) });
                    result["response_choice"] = new JObject { ["title"] = window.titleString, ["closable"] = window == scene.responseWindowClosable, ["options"] = options, ["how"] = "item.activate an option" };
                }
            result["dialogue_history"] = store.dialogHistory == null ? new JArray() : new JArray(store.dialogHistory);
            var dialog = DialogUIManager.Instance;
            var choices = new JArray();
            // Read choices from the dialogue itself: the choice bubbles only become active a frame later.
            if (dialog != null && dialog.currentDialog != null && dialog.IsChoice())
                for (int i = 0; i < dialog.currentDialog.choices.Count; i++)
                    choices.Add(new JObject { ["index"] = i, ["text"] = dialog.currentDialog.choices[i].Item2?.plainText, ["enabled"] = dialog.currentDialog.IsChoiceEnabledAt(i) });
            result["dialogue"] = new JObject { ["active"] = dialog != null && dialog.currentDialog != null,
                ["waiting_for_advance"] = dialog != null && dialog.isWaitingForManualAdvance, ["choices"] = choices };
            if (store.currentClientInstance != null && store.isClientArrived)
            {
                var current = store.currentClientInstance.GetClientBlueprint();
                result["customer"] = Client(current);
                result["customer"]["shootable"] = Shootable;
                // StoreClient.IsClientBuyingThisItem also evaluates custom willAcceptItem predicates (e.g. water grade).
                var buys = new JArray();
                foreach (var pair in knownItems)
                    if (GeneralHelper.IsItemOwned(pair.Value) && current.IsClientBuyingThisItem(pair.Value)) buys.Add(pair.Key);
                result["customer"]["buys_item_ids"] = buys;
            }
            else result["customer"] = JValue.CreateNull();
            var bargain = BargainUIManager.Instance;
            result["negotiation"] = new JObject { ["open"] = NegociationUIManager.Instance != null && NegociationUIManager.Instance.IsOpen,
                ["item_id"] = store.currentNegociatedItem != null ? (JToken)new JValue(store.currentNegociatedItem.GetUniqueID()) : JValue.CreateNull(),
                ["trade_locked"] = NegociationUIManager.IsTradeLocked() };
            if (bargain != null && bargain.IsOpen)
                result["bargain"] = new JObject { ["open"] = bargain.IsOpen, ["discount_offered"] = bargain.DiscountOffered,
                    ["discount_percent"] = bargain.DiscountPercent, ["markup_offered"] = bargain.MarkupOffered,
                    ["markup_percent"] = bargain.MarkupPercent, ["buying_markup_offered"] = bargain.BuyingMarkupOffered,
                    ["buying_markup_percent"] = bargain.BuyingMarkupPercent, ["buying_discount_offered"] = bargain.BuyingDiscountOffered,
                    ["buying_discount_percent"] = bargain.BuyingDiscountPercent };
            return result;
        }

        // Store inventories, open windows and every container nested inside them, open or closed.
        // Usable grid cells not covered by an item; fragmentation still decides whether a given shape fits.
        internal static int FreeCellsOf(GameGridInventory grid) => FreeCells(grid);
        // The largest empty rectangle (width x height): free cells say how much room there is, this says what shape still fits.
        internal static string LargestFreeRect(GameGridInventory grid)
        {
            var shape = grid.inventoryShape;
            int width = shape.maxX - shape.minX + 1, height = shape.maxY - shape.minY + 1;
            if (width <= 0 || height <= 0) return "0x0";
            var free = new bool[width, height];
            for (int y = shape.minY; y <= shape.maxY; y++)
                for (int x = shape.minX; x <= shape.maxX; x++) free[x - shape.minX, y - shape.minY] = (int)shape.Get(x, y) == 0;
            foreach (var item in grid.childItems)
            {
                var cells = item.modifiedShape;
                if (cells == null) continue;
                for (int y = cells.minY; y <= cells.maxY; y++)
                    for (int x = cells.minX; x <= cells.maxX; x++)
                        if ((int)cells.Get(x, y) != 0 && x - shape.minX >= 0 && x - shape.minX < width && y - shape.minY >= 0 && y - shape.minY < height) free[x - shape.minX, y - shape.minY] = false;
            }
            // Largest rectangle of free cells, row by row over column heights.
            var run = new int[width]; int bestArea = 0, bestW = 0, bestH = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) run[x] = free[x, y] ? run[x] + 1 : 0;
                for (int x = 0; x < width; x++)
                {
                    int minRun = int.MaxValue;
                    for (int x2 = x; x2 < width && run[x2] > 0; x2++)
                    {
                        minRun = Math.Min(minRun, run[x2]);
                        int area = minRun * (x2 - x + 1);
                        if (area > bestArea) { bestArea = area; bestW = x2 - x + 1; bestH = minRun; }
                    }
                }
            }
            return bestW + "x" + bestH;
        }
        private static int FreeCells(GameGridInventory grid)
        {
            int free = 0, used = 0;
            var shape = grid.inventoryShape;
            for (int y = shape.minY; y <= shape.maxY; y++)
                for (int x = shape.minX; x <= shape.maxX; x++) if ((int)shape.Get(x, y) == 0) free++;
            foreach (var item in grid.childItems)
            {
                var cells = item.modifiedShape;
                if (cells == null) continue;
                for (int y = cells.minY; y <= cells.maxY; y++)
                    for (int x = cells.minX; x <= cells.maxX; x++) if ((int)cells.Get(x, y) != 0) used++;
            }
            return free - used;
        }
        private GameInventory Destination(JObject args)
        {
            var inventoryId = args["inventory_id"];
            if (inventoryId == null || inventoryId.Type != JTokenType.String) throw new BridgeException("invalid_arguments", "inventory_id must be a string.");
            return Inventory((string)inventoryId);
        }
        // An inventory id, or a role that names exactly one inventory ("player_inventory", "weighing_counter", ...).
        private GameInventory Inventory(string idOrRole)
        {
            if (knownInventories.TryGetValue(idOrRole, out var byId)) return byId;
            var byRole = knownInventories.Values.Where(inventory => InventoryRole(inventory) == idOrRole).ToList();
            if (byRole.Count == 1) return byRole[0];
            throw new BridgeException("precondition_failed", byRole.Count == 0 ? "Unknown inventory: " + idOrRole + " (an inventory id or role from game.inventory_summary)."
                : "Several inventories have role " + idOrRole + "; use one of their ids: " + string.Join(", ", byRole.Select(InventoryIdOf)) + ".");
        }
        private JObject Transfer(GameItem item, GameInventory destination, int requested)
        {
            Require(destination != item.parentInventory, "Item is already in the destination inventory.");
            if (requested < 1 || requested > item.unitCount)
                throw new BridgeException("invalid_arguments", "amount must be between one and the current stack count.");
            int removable = Math.Min(requested, Math.Max(0, item.MaxNumRemove()));
            int rejected = requested;
            bool rotated = false;
            if (removable > 0)
            {
                int left = GraphUtils.TryAcceptAll(destination, item, removable);
                // The game drops an item in its current orientation only; a player turns it to fit the free space.
                if (left > 0 && destination is GameGridInventory turned && item.modifiedShape != null && destination.MayHaveValidInventorySlot(item))
                {
                    int start = new GridShapeBuilder(item.modifiedShape).orientation;
                    for (int turn = 0; turn < 4 && left > 0; turn++)
                        for (int y = 0; y < turned.inventoryShape.height && left > 0; y++)
                            for (int x = 0; x < turned.inventoryShape.width && left > 0; x++)
                            {
                                var slot = destination.TryInventorySlot(item, new GridShapeBuilder(item.modifiedShape).SetPosition(x, y).SetOrientation((start + turn) % 4).Build());
                                if (slot == null || !slot.IsValid() || slot.numTransfer < 1) continue;
                                int placed = slot.TryAcceptOnce(Math.Min(left, slot.numTransfer));
                                if (placed > 0) { left -= placed; rotated |= turn % 2 != 0; }
                            }
                }
                rejected = requested - removable + left;
            }
            var transfer = new JObject { ["dispatched"] = true, ["action"] = "item.transfer",
                ["requested"] = requested, ["transferred"] = requested - rejected, ["rejected"] = rejected };
            if (rotated) transfer["rotated"] = true;
            if (rejected > 0)
            {
                // The game's own checks, in order: can it leave, does the destination take it at all (insert lock, ownership, item rules), then room.
                if (removable < requested) transfer["removable"] = removable;
                if (destination is GameGridInventory grid) { transfer["destination_free_cells"] = FreeCells(grid); transfer["item_cells"] = Cells(item); }
                transfer["reason"] = removable == 0 ? "the item cannot be taken from where it is right now"
                    : destination.IsInsertLocked() ? "the destination is locked right now"
                    : !destination.MayHaveValidInventorySlot(item) ? Refusal(item, destination)
                    : destination is GameGridInventory g && FreeCells(g) < Cells(item) ? "not enough free cells" : "no free space of this item's shape";
            }
            return transfer;
        }
        // Which of GameGridInventory.MayHaveValidInventorySlot's checks refuses the item, in the game's order.
        private static string Refusal(GameItem item, GameInventory destination)
        {
            if (GraphUtils.FindParentEquals(destination, item)) return "the destination is inside this item";
            if (!GeneralHelper.MayOwnershipAcceptItem(item, destination)) return "ownership: an owned item cannot go into a container that is not owned";
            if (destination is GameGridInventory grid && item.modifiedShape != null
                && (item.modifiedShape.width > grid.inventoryShape.width || item.modifiedShape.height > grid.inventoryShape.height)
                && (item.modifiedShape.width > grid.inventoryShape.height || item.modifiedShape.height > grid.inventoryShape.width))
                return "the item (" + item.modifiedShape.width + "x" + item.modifiedShape.height + ") is larger than this inventory (" + grid.inventoryShape.width + "x" + grid.inventoryShape.height + ")";
            var own = item.mayInventoryAddItemFunc;
            if (own != null && !own.InvokeAllReduce(item, destination, InvokeFuncExtensions.BoolReduceOR)) return "the item's own placement rule refuses this inventory";
            var theirs = destination.mayInventoryAddItemFunc;
            if (theirs != null && !theirs.InvokeAllReduce(item, destination, InvokeFuncExtensions.BoolReduceOR)) return "this inventory's rule refuses the item";
            return "the destination does not accept this item";
        }
        // What an item does support right now, for a refusal: the game's own checks behind each item action.
        internal static GameplayAdapter Current { get; private set; }
        public GameplayAdapter() { Current = this; }
        internal string Interactions(GameItem item)
        {
            var can = new List<string>();
            if (item.CanActivateSlotItem()) can.Add("item.activate");
            if (item.CanToggleSlotItem()) can.Add("item.toggle");
            if (item.CanConfigureSlotItem()) can.Add("item.configure");
            if (item.contentWindow != null) can.Add("item.open");
            if (item.examineWindow != null) can.Add("item.examine");
            if (item.MaySelectSlotItem() && item.CanSelectSlotItem()) can.Add("surface.select");
            var targets = knownItems.Values.Where(other => other != item && item.CanTarget(other)).Take(5).Select(other => other.GetDisplayName(true) + " (" + other.GetUniqueID() + ")").ToList();
            if (targets.Count > 0) can.Add("item.use on " + string.Join(", ", targets));
            string picker = Flow.ResponseChoice ? " An item picker is open: its options are in game.response_choice (item.activate one)." : "";
            return (can.Count > 0 ? "; it supports: " + string.Join("; ", can) + "." : "; it has no item interactions right now.") + picker;
        }
        private static string CounterRoom(GameItem item)
        {
            var counter = EmporiumEntry.Instance.backInvinvElement as GameGridInventory;
            return "No room on the weighing counter for it (" + Cells(item) + " cells; the counter has " + (counter != null ? FreeCells(counter).ToString() : "?") + " free, "
                + EmporiumEntry.Instance.backInvinvElement.childItems.Count(GeneralHelper.IsItemOwned) + " of your items on it).";
        }
        private static int Cells(GameItem item)
        {
            int cells = 0; var shape = item.modifiedShape;
            if (shape == null) return 0;
            for (int y = shape.minY; y <= shape.maxY; y++)
                for (int x = shape.minX; x <= shape.maxX; x++) if ((int)shape.Get(x, y) != 0) cells++;
            return cells;
        }
        private static JArray List(List<string> values) => values == null ? new JArray() : new JArray(values);
        // The first reason this customer will not buy this item, following StoreClient.IsClientRefusingItem / IsClientBuyingThisItem
        // and PlayerStore.CanSellThisItem in their order, or null.
        internal static string SellRefusal(StoreClient client, GameItem item)
        {
            if (item.IsTag("NOT_FOR_RESALE")) return "the item is not for resale";
            if (client.clientBlackIdList.Contains(item.identifier) || item.GetGameItemType().Any(type => client.clientBlackTagList.Contains(type))) return "this customer refuses this kind of item (excluded)";
            if (client.noAcceptingContraband && ContrabandHelper.IsContraband(item)) return "this customer refuses contraband";
            if (client.isRequireVariety && client.IsItemInVarietyList(item)) return "this customer already bought this kind of item (variety)";
            if (client.IsClientRefusingItem(item)) return "this customer refuses this item (grade, hazard or label)";
            if (!client.IsClientBuyingThisItem(item)) return "this customer is not interested in this kind of item";
            if (client.CanClientExposeAnyFeatureDeep(item)) return "this customer would expose one of its features";
            if (client.useClientBudget && !client.HasBudgetLeftToBuy(item)) return "it costs more than the customer's remaining budget (" + client.GetBudget() + ")";
            return null;
        }
        // Who stands at the counter right now (the one customer.* actions act on), or null.
        internal static JToken AtCounter()
        {
            var store = PlayerStore.Instance;
            if (store.currentClientInstance == null) return JValue.CreateNull();
            var client = store.currentClientInstance.GetClientBlueprint();
            var at = new JObject { ["id"] = client.identifier, ["name"] = client.displayName, ["intent"] = client.clientIntent.ToString(), ["arrived"] = store.isClientArrived };
            if (client.isAug) at["augmented"] = true;
            if (client.isWanted) at["wanted"] = true;
            if (!client.dismissable) at["dismissable"] = false;
            return at;
        }
        // Client without its unset fields: what a counter view needs to know about who is there.
        // Shotgun.CanPickup for the customer at the counter, with the shotgun owned and in place.
        internal static bool Shootable
        {
            get
            {
                var gun = StoreUIManager.Instance?.shotgun != null ? StoreUIManager.Instance.shotgun.GetComponent<Shotgun>() : null;
                return PlayerStore.Instance.isShootingUnlocked && gun != null && gun.gameObject.activeInHierarchy && gun.CanPickup();
            }
        }
        internal static JObject ClientSummary(StoreClient client)
        {
            var summary = Client(client);
            foreach (var property in summary.Properties().ToList())
            {
                var value = property.Value;
                bool unset = value.Type == JTokenType.Null || (value.Type == JTokenType.Boolean && !(bool)value) || (value is JArray array && array.Count == 0)
                    || (value.Type == JTokenType.String && string.IsNullOrEmpty((string)value)) || ((value.Type == JTokenType.Integer || value.Type == JTokenType.Float) && (double)value == 0);
                if (unset && property.Name != "budget") property.Remove();
            }
            return summary;
        }
        private static JObject Client(StoreClient client) => new JObject {
            ["id"] = client.identifier, ["name"] = client.displayName, ["real_name"] = client.realName, ["intent"] = client.clientIntent.ToString(),
            ["faction"] = client.clientFaction, ["tags"] = List(client.tag), ["introduced"] = client.isIntroduced, ["business_complete"] = client.isBusinessComplete,
            ["dismissable"] = client.dismissable, ["arrestable"] = client.arrestable, ["security"] = client.isSecurity, ["wanted"] = client.isWanted,
            ["augmented"] = client.isAug,
            ["stealth_wanted"] = client.isStealthWanted, ["report_reward"] = client.reportReward, ["cash"] = client.clientCash,
            ["uses_budget"] = client.useClientBudget, ["budget"] = client.GetBudget(), ["buy_price_modifier"] = client.buyPriceModifier,
            ["sell_price_modifier"] = client.sellPriceModifier, ["buying_tags"] = List(client.clientBuyingTagList), ["buying_ids"] = List(client.clientBuyingIdList),
            ["excluded_tags"] = List(client.clientBlackTagList), ["excluded_ids"] = List(client.clientBlackIdList), ["wanted_features"] = List(client.clientItemFeatureBuying),
            ["requires_variety"] = client.isRequireVariety, ["no_contraband"] = client.noAcceptingContraband, ["accepts_retail_markup"] = client.acceptRetailMarkup,
            ["impatience"] = client.clientImpatience, ["service_cost"] = client.serviceCost, ["offers_wholesale"] = client.isOfferWholesale,
            ["wholesale_discount"] = client.wholesaleDiscount, ["can_expose_chemicals"] = client.CanExposeChem, ["accepted_chemical_grade"] = client.acceptedChemicalProductGrade,
            ["barter"] = client.barterOffer == null ? null : new JObject { ["text"] = client.barterOffer.barterTextBody, ["free_reward"] = client.barterOffer.isFreeReward,
                ["conditions"] = new JArray(client.barterOffer.conditions.Select(condition => new JObject { ["item_id"] = condition.requiredItemId, ["item_tag"] = condition.requiredItemTag,
                    ["required"] = condition.requiredDisplay, ["offered"] = condition.offeredDisplay })) },
            ["procurement"] = client.procurementData == null ? null : new JObject { ["client"] = client.procurementData.clientName, ["day"] = client.procurementData.day,
                ["initial_reward"] = client.procurementData.initialReward, ["final_reward"] = client.procurementData.finalReward,
                ["offer"] = client.procurementData.descriptionOffer, ["collect"] = client.procurementData.descriptionCollect },
            ["gun_order"] = client.gunOrderData == null ? null : new JObject { ["tier"] = client.gunOrderData.tier, ["base_reward"] = client.gunOrderData.baseReward,
                ["service_order"] = client.gunOrderData.isServiceOrder, ["best_order"] = client.gunOrderData.isBestOrder,
                ["mandatory"] = GunRequirements(client.gunOrderData.mandatoryRequirements), ["bonus"] = GunRequirements(client.gunOrderData.bonusRequirements) } };
        private static JArray GunRequirements(List<GunOrderRequirement> requirements) => new JArray(requirements.Select(requirement => new JObject {
            ["id"] = requirement.id, ["name"] = requirement.GetDisplayName(), ["compare"] = requirement.GetCompareSymbol(), ["value"] = requirement.value,
            ["caliber"] = requirement.isCaliber, ["reward"] = requirement.GetReward() }));

        private void Refresh()
        {
            refreshFrame = Time.frameCount;
            knownItems.Clear(); knownInventories.Clear(); owners.Clear();
            if (!Active) { inventoryIds.Clear(); return; }
            var visited = new HashSet<GraphNodeStorage>();
            var scene = EmporiumEntry.Instance;
            // Roots of EmporiumEntry.GetAllItems plus the remaining fixed store inventories.
            foreach (GraphNodeStorage root in new GraphNodeStorage[] { scene.invElement, scene.backInvinvElement, scene.backInvinvElementCounter,
                scene.frontInvinvElement, scene.showcaseElement, scene.docInvElement, scene.trashInvElement, scene.trashcanInvElement,
                scene.hiddenElement, scene.drainInvElement, scene.faucetElement, scene.cassettePlayerElement, scene.vendingMachineElement,
                scene.vendingFountainElement, scene.soldElement, scene.hirelingInv, scene.bazarLeftinvElement })
                Collect(root, visited);
            // Pickers and the afterhours ground and pockets exist only while their window is up or the player is out.
            if (scene.responseWindow != null && scene.responseWindow.IsVisible()) Collect(scene.responseInventory, visited);
            if (scene.responseWindowClosable != null && scene.responseWindowClosable.IsVisible()) Collect(scene.responseInventoryClosable, visited);
            if (Flow.AfterhoursPhase != null && Flow.AfterhoursPhase != "in_store")
                foreach (GraphNodeStorage root in new GraphNodeStorage[] { scene.afterhourWindow, scene.afterhourInventory, scene.afterhourPocketSlotInvLeft,
                    scene.afterhourPocketSlotInvRight, scene.afterhourPocketSlotInvBackpack })
                    Collect(root, visited);
            if (WindowsHandler.current != null) foreach (var window in WindowsHandler.current.visibleWindows) Collect(window, visited);
        }

        // owner: the item whose content window this node belongs to (machines lay out several inventories there).
        private void Collect(GraphNodeStorage node, HashSet<GraphNodeStorage> visited, GameItem owner = null)
        {
            if (node == null || !visited.Add(node)) return;
            if (node is GameItem item)
            {
                knownItems[item.GetUniqueID()] = item;
                Collect(item.contentWindow?.child, visited, item);
            }
            if (node is GameInventory inventory)
            {
                if (owner != null && !owners.ContainsKey(inventory)) owners[inventory] = owner;
                if (!inventoryIds.TryGetValue(inventory, out var id)) { id = StableId(inventory); inventoryIds.Add(inventory, id); }
                knownInventories[id] = inventory;
                foreach (var child in inventory.childItems) Collect(child, visited);
            }
            if (node.children != null) foreach (var child in node.children) Collect(child, visited, owner);
        }
        internal void InvalidateItems() { refreshFrame = -1; }
        // Ids that survive restarts: the role for the shop's own places, item_<id>[/role] for an item's inventories (item ids are saved with the game).
        private string StableId(GameInventory inventory)
        {
            string role = InventoryRole(inventory);
            string id = owners.TryGetValue(inventory, out var holder)
                ? "item_" + holder.GetUniqueID() + (role != null && role != "container" && role != "trash" ? "/" + role : "")
                : role ?? "inventory";
            string unique = id;
            for (int n = 2; knownInventories.ContainsKey(unique) || inventoryIds.ContainsValue(unique); n++) unique = id + "_" + n;
            return unique;
        }

        // The conversation after an advance: state, latest line, whether it is the last one, and the choices when it stops at one.
        // The conversation log (counter and phone), oldest first; the game keeps the last 100 lines.
        internal static string[] History() => Active && PlayerStore.Instance.dialogHistory != null ? PlayerStore.Instance.dialogHistory.ToArray() : new string[0];
        // Lines logged since an earlier History() and where the conversation stands: null when nothing was said and none is going on.
        internal static JObject Talk(string[] before)
        {
            var after = History();
            // The log drops its oldest lines when full: align the earlier log's tail with the current log's head.
            int shift = 0;
            while (shift < before.Length && !before.Skip(shift).SequenceEqual(after.Take(before.Length - shift))) shift++;
            var lines = after.Skip(before.Length - shift).ToArray();
            var talk = DialogUIManager.Instance; var current = talk?.currentDialog;
            if (lines.Length == 0 && current == null) return null;
            var result = new JObject { ["lines"] = new JArray(lines) };
            if (current != null) result["state"] = Flow.DialogueState;
            if (current != null && talk.IsChoice())
                result["choices"] = new JArray(current.choices.Select((choice, i) => new JObject { ["index"] = i, ["text"] = choice.Item2?.plainText, ["enabled"] = current.IsChoiceEnabledAt(i) }));
            else if (current != null) result["last_line"] = current.nextDialogue == null && current.choices.Count == 0;
            // Whether the customer can also be sent away instead of answering (customer.dismiss).
            if (result["choices"] != null && PlayerStore.Instance.currentClientInstance != null) result["customer_dismissable"] = PlayerStore.Instance.IsClientDimissable();
            return result;
        }
        // dialogue.advance all=true: one advance every few frames until the conversation stops at a choice or ends.
        internal static bool Advancing;
        private static int advanceCount, advanceFrame, idleFrames;
        internal static void PumpDialogue()
        {
            if (!Advancing) return;
            var talk = DialogUIManager.Instance;
            // A scripted scene may start its lines a moment later or pause between them: stop only after a quiet second.
            if (talk == null || talk.currentDialog == null) { if (++idleFrames > 60) Advancing = false; return; }
            idleFrames = 0;
            if (talk.IsChoice() || advanceCount > 300) { Advancing = false; return; }
            if (Time.frameCount - advanceFrame < 4) return;
            advanceFrame = Time.frameCount; advanceCount++;
            talk.OnAdvancePressed();
        }

        // Cash and owned stock around an action, so its result can say what it actually changed. Sold goods count as gone.
        internal sealed class Stock { public int Cash; public readonly Dictionary<string, int> Units = new Dictionary<string, int>(); public readonly HashSet<int> Ids = new HashSet<int>(); public readonly Dictionary<int, string> Places = new Dictionary<int, string>(); public Dictionary<string, int> Reputation; }
        internal static Dictionary<string, int> Reputations()
        {
            var standing = new Dictionary<string, int>();
            if (PlayerStore.Instance.storeReputations != null) foreach (var rep in PlayerStore.Instance.storeReputations) standing[rep.factionDisplay] = rep.GetReputation();
            return standing;
        }
        internal static JObject ReputationChange(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            var change = new JObject();
            foreach (var pair in after) if (before.TryGetValue(pair.Key, out var was) && was != pair.Value) change[pair.Key] = pair.Value - was;
            return change.Count > 0 ? change : null;
        }
        internal Stock Snapshot()
        {
            Refresh();
            var stock = new Stock { Cash = PlayerStore.Instance.playerCash, Reputation = Reputations() };
            foreach (var item in knownItems.Values)
            {
                var scene = EmporiumEntry.Instance;
                // Picker options (backpack choice, crate contents) are choices, not stock.
                if (!GeneralHelper.IsItemEffectivelyOwned(item) || item.parentInventory == scene.soldElement || item.parentInventory == scene.responseInventory || item.parentInventory == scene.responseInventoryClosable) continue;
                string name = item.GetDisplayName(true);
                stock.Units[name] = (stock.Units.TryGetValue(name, out var units) ? units : 0) + item.unitCount;
                stock.Ids.Add(item.GetUniqueID());
                stock.Places[item.GetUniqueID()] = RoleOf(item.parentInventory) ?? InventoryIdOf(item.parentInventory);
            }
            return stock;
        }
        // moves: also list items of yours that changed place without changing hands (what the game moved for you, e.g. coming back from a night trip).
        internal JObject Effects(Stock before, bool moves = false)
        {
            var after = Snapshot();
            var effects = new JObject();
            if (after.Cash != before.Cash) effects["cash_change"] = after.Cash - before.Cash;
            if (ReputationChange(before.Reputation, after.Reputation) is JObject standing) effects["reputation_change"] = standing;
            var gained = new JArray(); var lost = new JArray();
            foreach (var pair in after.Units)
            {
                int change = pair.Value - (before.Units.TryGetValue(pair.Key, out var had) ? had : 0);
                if (change <= 0) continue;
                var ids = new JArray();
                foreach (var item in knownItems.Values)
                    if (item.GetDisplayName(true) == pair.Key && after.Ids.Contains(item.GetUniqueID()) && !before.Ids.Contains(item.GetUniqueID())) ids.Add(item.GetUniqueID());
                var row = new JObject { ["name"] = pair.Key, ["count"] = change, ["item_ids"] = ids };
                if (ids.Count > 0 && knownItems.TryGetValue((int)ids[0], out var first)) row["at"] = RoleOf(first.parentInventory) ?? InventoryIdOf(first.parentInventory);
                gained.Add(row);
            }
            foreach (var pair in before.Units)
            {
                int change = pair.Value - (after.Units.TryGetValue(pair.Key, out var has) ? has : 0);
                if (change > 0) lost.Add(new JObject { ["name"] = pair.Key, ["count"] = change });
            }
            if (gained.Count > 0) effects["gained"] = gained;
            if (lost.Count > 0) effects["lost"] = lost;
            if (moves)
            {
                var moved = new JArray();
                foreach (var pair in after.Places)
                    if (before.Places.TryGetValue(pair.Key, out var was) && was != pair.Value && knownItems.TryGetValue(pair.Key, out var item))
                        moved.Add(new JObject { ["item_id"] = pair.Key, ["name"] = item.GetDisplayName(true), ["from"] = was, ["to"] = pair.Value });
                if (moved.Count > 0) effects["moved"] = moved;
            }
            return effects.Count > 0 ? effects : null;
        }
        internal GameItem ResolveItem(int id)
        {
            if (refreshFrame != Time.frameCount) Refresh();
            if (!knownItems.TryGetValue(id, out var item)) throw new BridgeException("unknown_item", "No item with this ID in the store, afterhours or any open window.");
            return item;
        }
        private string InventoryRole(GameInventory inventory)
        {
            var scene = EmporiumEntry.Instance;
            if (inventory == scene.invElement) return "player_inventory";
            if (inventory == scene.backInvinvElement) return "weighing_counter";
            if (inventory == scene.backInvinvElementCounter) return "weighing_counter_overflow";
            if (inventory == scene.hiddenElement) return "hidden_storage";
            if (inventory == scene.soldElement) return "sold";
            if (inventory == scene.hirelingInv) return "hireling";
            if (inventory == scene.vendingMachineElement) return "vending_machine";
            if (inventory == scene.vendingFountainElement) return "vending_fountain";
            if (inventory == scene.trashcanInvElement) return "trashcan";
            if (inventory == scene.bazarLeftinvElement) return "bazar";
            if (inventory == scene.responseInventory || inventory == scene.responseInventoryClosable) return "response";
            if (inventory == scene.frontInvinvElement) return "front_counter";
            if (inventory == scene.showcaseElement) return "display_case";
            if (inventory == scene.docInvElement) return "document_holder";
            if (inventory == scene.trashInvElement) return "trash_container_slot";
            if (inventory == scene.drainInvElement) return "drain";
            if (inventory == scene.faucetElement || inventory == scene.faucet?.parentInventory) return "faucet";
            if (inventory == scene.cassettePlayerElement) return "cassette_player";
            if (inventory == scene.afterhourInventory) return "afterhours_ground";
            if (inventory == scene.afterhourPocketSlotInvLeft) return "afterhours_pocket_left";
            if (inventory == scene.afterhourPocketSlotInvRight) return "afterhours_pocket_right";
            if (inventory == scene.afterhourPocketSlotInvBackpack) return "afterhours_backpack_slot";
            // Machine slots sit in labelled panels (seed, water, power, module, output, note...).
            if (inventory is PixelElement element)
                for (var node = element.handler?.parent; node != null; node = node.parent)
                    if (node.element is MachineryPanel panel && !string.IsNullOrEmpty(panel.role)) return "machine_" + panel.role;
            var carriedBag = scene.afterhourPocketSlotInvBackpack?.childItem;
            if (carriedBag != null && inventory == carriedBag.contentWindow?.child) return "carried_backpack_contents";
            if (owners.TryGetValue(inventory, out var holder))
                return holder.parentInventory == scene.trashInvElement || holder.parentInventory == scene.trashcanInvElement ? "trash" : "container"; // owner_item_id says which.
            return null;
        }
        // What each role is, for describe: a role name alone does not say what the place is in the shop.
        internal static JObject RoleGuide() => new JObject {
            ["player_inventory"] = "The player's own inventory behind the counter.",
            ["container"] = "Contents of an item with its own inventory (storage bay, bag, box); owner_item_id names it.",
            ["trash"] = "Contents of the rubbish bin.",
            ["weighing_counter"] = "The trading surface between the shop and the customer at the counter. Owned items here are what trade.sell_all offers; the item here is what trade.buy/trade.sell weigh.",
            ["weighing_counter_overflow"] = "Overflow area of the weighing counter.",
            ["front_counter"] = "The customer's side of the counter, where sellers lay out goods and deliveries arrive.",
            ["display_case"] = "The shop's display case, visible to passers-by.",
            ["document_holder"] = "Slot holding the file box for documents.",
            ["trashcan"] = "Slot holding the rubbish bin.", ["trash_container_slot"] = "Slot for the bin.", ["drain"] = "Drain for liquids.", ["faucet"] = "Water tap slot.",
            ["hidden_storage"] = "Hidden compartment (fence start).",
            ["vending_machine"] = "Vending machine contents (sold at night).", ["vending_fountain"] = "Water vending machine contents (sold at night).",
            ["response"] = "Options of an open item picker (game.response_choice); item.activate picks one.",
            ["afterhours_ground"] = "Items lying at the current night location.",
            ["machine_*"] = "A machine's slots (seed, water, power, module, output, ...)." };
        internal string InventoryIdOf(GameInventory inventory) => inventory != null && inventoryIds.TryGetValue(inventory, out var id) ? id : null;
        // Every owned item the store currently knows, with the inventory it sits in.
        internal IEnumerable<GameItem> OwnedItems()
        {
            if (refreshFrame != Time.frameCount) Refresh();
            foreach (var item in knownItems.Values) if (GeneralHelper.IsItemOwned(item)) yield return item;
        }
        // Owned and still in the shop: not sold or offered away, nor inside something that was.
        internal static bool InStock(GameItem item)
        {
            if (!GeneralHelper.IsItemEffectivelyOwned(item)) return false;
            var scene = EmporiumEntry.Instance;
            for (var inventory = item.parentInventory; inventory != null; inventory = GraphUtils.FindParentType<GameItem>(inventory)?.parentInventory)
                if (inventory == scene.soldElement || inventory == scene.responseInventory || inventory == scene.responseInventoryClosable) return false;
            return true;
        }
        internal string RoleOf(GameInventory inventory) => inventory == null ? null : InventoryRole(inventory);

        public JArray DescribeActions()
        {
            var empty = new JObject();
            var item = new JObject { ["item_id"] = Integer(int.MinValue, null) };
            return new JArray {
                Definition("menu.open", "Open the normal in-game escape menu without injecting a keyboard shortcut.", empty),
                Definition("day.open", "Open the morning shutter through the game's normal shutter control.", empty),
                Definition("day.close", "Close the shutter. With customers still queued the game asks for confirmation; skip_queue=true answers it (StoreShutterButton.EndAndClose), otherwise the native confirmation opens.",
                    new JObject { ["skip_queue"] = new JObject { ["type"] = "boolean" } }, "skip_queue"),
                Definition("day.sleep", "Sleep using the open end-of-day tablet; advances and saves normally.", empty),
                Definition("customer.next", "Ring for the next customer, without auto-skipping dialogue.", empty),
                Definition("customer.dismiss", "Dismiss a dismissable customer without automatically calling another.", empty),
                Definition("customer.aim", "Pick up the shotgun and aim at the current customer, as clicking the shotgun does (needs customer.shootable).", empty),
                Definition("customer.shoot", "Fire while aiming (flow.shooting_mode), as clicking in aiming mode does.", empty),
                Definition("customer.lower_gun", "Put the shotgun down without firing.", empty),
                Definition("customer.report", "Press the security alarm on the current customer, as the counter's call-security button does (wanted customers pay their report reward).", empty),
                Definition("dialogue.advance", "Finish typing the current line or advance to the next. all=true keeps advancing every few frames until a choice or the end of the conversation; the result comes back then (talk.lines has what was said), or after a few seconds with flow.advancing still true.", new JObject { ["all"] = new JObject { ["type"] = "boolean" } }, "all"),
                Definition("dialogue.choose", "Select an enabled, visible dialogue choice.", new JObject { ["index"] = Integer(0, null) }),
                Definition("negotiation.open", "Open the game's negotiation tablet.", empty),
                Definition("trade.weigh", "Put an item on the weighing counter, as dragging it there does, and return its price there: a seller's item at the price trade.buy would charge, one of yours at what this customer would pay.", new JObject { ["item_id"] = Integer(int.MinValue, null) }),
                new JObject { ["name"] = "trade.buy", ["description"] = "Buy a seller's items at the game's offer: item_id (or item_ids, one after another) puts each on the weighing counter first (as dragging it there does) and buys it; without either, the item already weighed (trade.weighed_item_id). trade.wholesale buys a whole lot when the seller offers it.",
                    ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JObject { ["item_id"] = Integer(int.MinValue, null), ["item_ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["items"] = Integer(int.MinValue, null) } } } },
                Definition("trade.sell", "Sell the current owned weighted-counter item using normal sale inspection and budget checks.", empty),
                Definition("bargain.offer", "Use a normal bargain option; percentages match game buttons.", new JObject {
                    ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("discount", "markup", "buying_markup", "buying_discount") },
                    ["percent"] = new JObject { ["type"] = "integer", ["enum"] = new JArray(10, 25, 50, 75) } }),
                new JObject { ["name"] = "item.inspect", ["description"] = "Full observation of items (description, every value, features, tooltip, internal tags): item_id for one, item_ids for several. Screen geometry and cell grids only with geometry=true.",
                    ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JObject { ["item_id"] = Integer(int.MinValue, null),
                        ["item_ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["items"] = Integer(int.MinValue, null) }, ["geometry"] = new JObject { ["type"] = "boolean" } } } },
                Definition("item.activate", "Perform the normal validated item activation (distinct from opening its windows).", item),
                Definition("item.open", "Open or focus a container or machinery content window through the normal item interaction handler.", item),
                Definition("item.examine", "Open or focus an item's examination window; its displayed appearance is returned by subsequent observations.", item),
                Definition("item.toggle", "Perform the normal validated item toggle.", item),
                Definition("item.configure", "Open the normal item configuration UI.", item),
                Definition("item.use", "Use one item on another through the game's target mechanic.", new JObject { ["item_id"] = Integer(int.MinValue, null), ["target_id"] = Integer(int.MinValue, null) }),
                new JObject { ["name"] = "item.transfer", ["description"] = "Auto-place into an inventory (including fixed slots/machine inputs): item_id (optional amount for part of a stack), item_ids to move several whole stacks, or from_inventory_id to move everything you own out of one inventory (types / exclude_types filter by item type). item_ids are placed in the order given, from_inventory_id's items largest first; also_into lists further destinations for what does not fit. Results report transferred and rejected counts, with a reason when rejected.",
                    ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("inventory_id"), ["properties"] = new JObject { ["item_id"] = Integer(int.MinValue, null),
                        ["item_ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["items"] = Integer(int.MinValue, null) }, ["from_inventory_id"] = new JObject { ["type"] = "string" }, ["also_into"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                        ["types"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } }, ["exclude_types"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } }, ["inventory_id"] = new JObject { ["type"] = "string" }, ["amount"] = Integer(1, null) } } },
                Definition("item.move", "Move the complete stack to an explicit grid location, respecting placement rules.", new JObject { ["item_id"] = Integer(int.MinValue, null), ["inventory_id"] = new JObject { ["type"] = "string" }, ["x"] = Integer(0, null), ["y"] = Integer(0, null), ["orientation"] = Integer(0, 3) }),
                Definition("appraisal.estimate", "Submit an appraisal estimate for a customer's [appraisal] item, as the inspection panel's estimate button does. items report appraisal_value.", new JObject { ["item_id"] = Integer(int.MinValue, null), ["value"] = Integer(0, null) }),
                Definition("item.rotate", "Rotate a complete item in place if the rotated placement is legal.", new JObject { ["item_id"] = Integer(int.MinValue, null), ["orientation"] = Integer(0, 3) })
            };
        }

        private static JObject Integer(int min, int? max)
        {
            var schema = new JObject { ["type"] = "integer", ["minimum"] = min };
            if (max.HasValue) schema["maximum"] = max.Value;
            return schema;
        }
        private static JObject Definition(string name, string description, JObject properties, string optionalArgument = null)
        {
            var required = new JArray(); foreach (var prop in properties.Properties()) if (prop.Name != optionalArgument) required.Add(prop.Name);
            return new JObject { ["name"] = name, ["description"] = description, ["arguments"] = new JObject {
                ["type"] = "object", ["properties"] = properties.DeepClone(), ["required"] = required, ["additionalProperties"] = false } };
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new BridgeException("precondition_failed", message); }
        private static int Number(JObject args, string key)
        {
            var value = args[key];
            if (value == null || value.Type != JTokenType.Integer || !int.TryParse(value.ToString(), out var n))
                throw new BridgeException("invalid_arguments", key + " must be a 32-bit integer.");
            return n;
        }
        private GameItem Item(JObject args, string key)
        {
            var id = Number(args, key);
            if (!knownItems.TryGetValue(id, out var item)) throw new BridgeException("unknown_item", "No item with this ID in the store, afterhours or any open window.");
            return item;
        }

        public JObject Act(string action, JObject args)
        {
            args = args ?? new JObject();
            if (action == "trade.buy" && args["item_ids"] is JArray buying)
            {
                // One purchase after another, each through the single-item path; a refusal stops nothing else.
                var bought = new JArray();
                foreach (var token in buying)
                {
                    try { var one = Act("trade.buy", new JObject { ["item_id"] = token }); bought.Add(new JObject { ["item_id"] = token, ["bought"] = true }); }
                    catch (BridgeException error) { bought.Add(new JObject { ["item_id"] = token, ["bought"] = false, ["reason"] = error.Message }); }
                }
                return new JObject { ["dispatched"] = true, ["action"] = action, ["results"] = bought };
            }
            Require(Active, "No active store save; load or start a game first.");
            var store = PlayerStore.Instance;
            Require(store.lockAllFlag == 0 && !store.cinematicMode, "Gameplay is locked by a transition or cinematic.");
            Refresh();
            if (action == "item.inspect")
            {
                // Reading only: no inventory lock applies.
                var ids = args["item_ids"] is JArray list ? list.Select(id => (int)id).ToList() : args["item_id"] != null ? new List<int> { Number(args, "item_id") } : null;
                if (ids == null) throw new BridgeException("invalid_arguments", "item_id or item_ids is required.");
                bool geometry = args["geometry"]?.Type == JTokenType.Boolean && (bool)args["geometry"];
                bool wasCompact = ItemObservation.Compact;
                ItemObservation.Compact = !geometry;
                try
                {
                    var items = new JArray();
                    foreach (int id in ids)
                    {
                        if (!knownItems.TryGetValue(id, out var found)) { items.Add(new JObject { ["id"] = id, ["error"] = "unknown_item" }); continue; }
                        var read = ItemObservation.Read(found, withTags: true);
                        // Its own inventories (a container's space, a machine's slots), as game.inventories lists them.
                        var own = new JArray();
                        foreach (var pair in owners)
                            if (pair.Value == found)
                                own.Add(new JObject { ["inventory_id"] = InventoryIdOf(pair.Key), ["role"] = InventoryRole(pair.Key), ["items"] = pair.Key.childItems.Count, ["free_cells"] = pair.Key is GameGridInventory ownGrid ? (JToken)FreeCells(ownGrid) : JValue.CreateNull() });
                        if (own.Count > 0) read["inventories"] = own;
                        // What item.use can do with it: the items it can target right now, by the game's own check.
                        var targets = new JArray();
                        foreach (var other in knownItems.Values)
                            if (other != found && found.CanTarget(other)) targets.Add(new JObject { ["item_id"] = other.GetUniqueID(), ["name"] = other.GetDisplayName(true) });
                        if (targets.Count > 0) read["can_use_on"] = targets;
                        items.Add(read);
                    }
                    return args["item_ids"] != null ? new JObject { ["items"] = items } : (JObject)items[0];
                }
                finally { ItemObservation.Compact = wasCompact; }
            }
            if (action == "item.transfer" && args["from_inventory_id"] != null)
            {
                var source = Inventory((string)args["from_inventory_id"]);
                // types / exclude_types filter by the item's types (e.g. exclude_types ["DOCUMENT"]).
                var onlyTypes = (args["types"] as JArray)?.Values<string>().ToList();
                var skipTypes = (args["exclude_types"] as JArray)?.Values<string>().ToList();
                var moving = source.childItems.Where(GeneralHelper.IsItemOwned)
                    .Where(child => onlyTypes == null || child.GetGameItemType().Any(onlyTypes.Contains))
                    .Where(child => skipTypes == null || !child.GetGameItemType().Any(skipTypes.Contains));
                // Largest first, so big items get the contiguous room before small ones fragment it; explicit item_ids keep the caller's order.
                args = new JObject { ["inventory_id"] = args["inventory_id"], ["also_into"] = args["also_into"], ["item_ids"] = new JArray(moving.OrderByDescending(Cells).Select(child => child.GetUniqueID())) };
                if (((JArray)args["item_ids"]).Count == 0) return new JObject { ["dispatched"] = true, ["action"] = action, ["results"] = new JArray(), ["source_owned_items"] = 0 };
            }
            if (action == "item.transfer" && args["item_ids"] is JArray batch)
            {
                Require(store.lockAllInventoryFlag == 0 && store.lockStorePhysicalFlag == 0, "Inventory interaction is locked.");
                if (args["item_id"] != null || args["amount"] != null) throw new BridgeException("invalid_arguments", "item_ids moves whole stacks; leave out item_id and amount.");
                var destination = Destination(args);
                // also_into: further destinations tried in order for what did not fit.
                var fallbacks = new List<GameInventory>();
                if (args["also_into"] is JArray further)
                    foreach (var id in further.Values<string>()) fallbacks.Add(Inventory(id));
                var results = new JArray();
                var ordered = batch.Select(token => (int)token).ToList();
                foreach (int id in ordered)
                {
                    JObject row;
                    if (!knownItems.TryGetValue(id, out var found)) row = new JObject { ["error"] = "unknown_item" };
                    else if (found.parentInventory == destination) row = new JObject { ["transferred"] = 0, ["reason"] = "already in the destination" };
                    else
                    {
                        row = Transfer(found, destination, found.unitCount);
                        foreach (var extra in fallbacks)
                        {
                            if ((int)row["rejected"] == 0 || found.parentInventory == extra) break;
                            var retry = Transfer(found, extra, found.unitCount);
                            if ((int)retry["transferred"] > 0) { retry["into"] = InventoryIdOf(extra); row = retry; }
                        }
                    }
                    row.Remove("dispatched"); row.Remove("action");
                    row.AddFirst(new JProperty("item_id", id));
                    results.Add(row);
                }
                return new JObject { ["dispatched"] = true, ["action"] = action, ["results"] = results };
            }
            if (action.StartsWith("item.", StringComparison.Ordinal))
            {
                Require(store.lockAllInventoryFlag == 0 && store.lockStorePhysicalFlag == 0, "Inventory interaction is locked.");
                var item = Item(args, "item_id");
                switch (action)
                {
                    case "item.activate":
                        Require(item.CanActivateSlotItem(), "Item cannot be activated here" + Interactions(item));
                        item.ActivateSlotItem();
                        // Documents open a reader; its text is in surfaces.reading (some readers fill in a frame later).
                        if (item.IsTag("DOCUMENT"))
                        {
                            var read = new JObject { ["dispatched"] = true, ["action"] = action, ["reading"] = SurfaceObservation.Reading() };
                            // A reader over the counter locks the store until closed (flow.modal); lay it out first so the check sees it.
                            Canvas.ForceUpdateCanvases();
                            if (Modal.Current is Modal.Panel opened) read["modal"] = opened.Id + ": covers the store; modal.close when done";
                            return read;
                        }
                        break;
                    case "item.open":
                    case "item.examine":
                        Require(!GeneralHelper.IsDisplayOnly(item) && !ItemPinHelper.BlocksClicks && ItemMouseDoubleClickHandler.current != null && item is PixelElement, "Item window interaction is unavailable.");
                        var itemView = (PixelElement)item;
                        var canvas = itemView.handler.GetComponentInParent<Canvas>();
                        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                        Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, itemView.rectTransform.TransformPoint(itemView.rectTransform.rect.center));
                        if (action == "item.open")
                        {
                            Require(item.contentWindow != null, "Item has no content window.");
                            ItemMouseDoubleClickHandler.current.OpenContentAction(item, point);
                        }
                        else
                        {
                            Require(item.examineWindow != null, "Item has no examination window.");
                            ItemMouseDoubleClickHandler.current.OpenExamineAction(item, point);
                        }
                        break;
                    case "item.toggle": Require(item.CanToggleSlotItem(), "Item cannot be toggled here" + Interactions(item)); item.ToggleSlotItem(); break;
                    case "item.configure": Require(item.CanConfigureSlotItem(), "Item cannot be configured here" + Interactions(item)); item.ConfigureSlotItem(); break;
                    case "item.use":
                    {
                        var target = Item(args, "target_id");
                        Require(item.CanTarget(target), "This item cannot target that item here (item.inspect lists can_use_on).");
                        string beforeUse = ItemObservation.Read(item, withTags: true).ToString() + ItemObservation.Read(target, withTags: true).ToString();
                        item.Target(target);
                        bool changedByUse = ItemObservation.Read(item, withTags: true).ToString() + ItemObservation.Read(target, withTags: true).ToString() != beforeUse;
                        // Show both as they are now (liquid levels, contents, value), compactly.
                        bool wasCompact = ItemObservation.Compact; ItemObservation.Compact = true;
                        try { return new JObject { ["dispatched"] = true, ["action"] = action, ["changed"] = changedByUse, ["item"] = item != null ? ItemObservation.Read(item) : null, ["target"] = target != null ? ItemObservation.Read(target) : null }; }
                        finally { ItemObservation.Compact = wasCompact; }
                    }
                    case "item.transfer": return Transfer(item, Destination(args), args["amount"] == null ? item.unitCount : Number(args, "amount"));
                    case "item.move":
                    case "item.rotate":
                        GameInventory destination = item.parentInventory;
                        if (action == "item.move")
                            destination = Inventory((string)args["inventory_id"]);
                        Require(destination is GameGridInventory, "Explicit placement only supports grid inventories; use normal UI for fixed slots.");
                        int x = action == "item.move" ? Number(args, "x") : item.modifiedShape.minX;
                        int y = action == "item.move" ? Number(args, "y") : item.modifiedShape.minY;
                        if (x < 0 || y < 0 || Number(args, "orientation") < 0 || Number(args, "orientation") > 3)
                            throw new BridgeException("invalid_arguments", "Grid coordinates must be nonnegative and orientation must be 0..3.");
                        var shape = new GridShapeBuilder(item.modifiedShape).SetPosition(x, y).SetOrientation(Number(args, "orientation")).Build();
                        var slot = destination.TryInventorySlot(item, shape);
                        if (!(slot != null && slot.IsValid() && slot.numTransfer >= item.unitCount && item.MaxNumRemove() >= item.unitCount))
                            throw new BridgeException("precondition_failed", item.MaxNumRemove() < item.unitCount ? "The item cannot be taken from where it is right now."
                                : destination.IsInsertLocked() ? "The destination is locked right now."
                                : !destination.MayHaveValidInventorySlot(item) ? "Refused: " + Refusal(item, destination) + "."
                                : "Its shape does not fit at (" + x + ", " + y + ") with orientation " + Number(args, "orientation") + ": those cells are taken or outside the grid (" + FreeCells((GameGridInventory)destination) + " free cells).");
                        int count = item.unitCount;
                        Require(slot.TryAcceptOnce(count) == count, "Placement was rejected.");
                        break;
                    default: throw new BridgeException("unknown_action", "Unknown gameplay action: " + action);
                }
            }
            else switch (action)
            {
                case "customer.aim":
                {
                    var shotgun = StoreUIManager.Instance.shotgun != null ? StoreUIManager.Instance.shotgun.GetComponent<Shotgun>() : null;
                    Require(store.isShootingUnlocked && shotgun != null && shotgun.gameObject.activeInHierarchy, "No shotgun in this shop yet.");
                    Require(shotgun.CanPickup(), "This customer cannot be shot (customer.shootable).");
                    if (!StoreUIManager.Instance.isShootingMode) StoreUIManager.Instance.EnterShootingMode();
                    break;
                }
                case "customer.shoot":
                    // Arm.Update: a left click in aiming mode fires when StoreUIManager.CanShoot allows it.
                    Require(StoreUIManager.Instance.isShootingMode && StoreUIManager.Instance.CanShoot(), "Aim first (customer.aim, then wait for flow.shooting_mode).");
                    StoreUIManager.Instance.Shoot();
                    break;
                case "customer.lower_gun":
                    Require(StoreUIManager.Instance.isShootingMode, "Not aiming.");
                    StoreUIManager.Instance.ExitShootingMode();
                    break;
                case "customer.report":
                    // StoreSecButton.OnClick: the game's own report path interrupts any dialogue or choice in progress.
                    Require(store.IsClientPresent() && store.currentClientInstance.GetClientBlueprint().CanReport(), "No reportable customer.");
                    Require(!StoreReputation.IsPerkUnlocked("SEC_ALARM_IGNORED"), "Security currently ignores this shop's alarm.");
                    store.OnCustomerReported();
                    // Some customers answer a report with their own lines first (tryInterceptReport); then no arrest has started yet.
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["at_counter"] = AtCounter(), ["arrest_started"] = store.isClientBeingArrested,
                        ["customer_responding"] = !store.isClientBeingArrested && DialogUIManager.Instance?.currentDialog != null };
                case "appraisal.estimate":
                {
                    var appraised = Item(args, "item_id");
                    Require(appraised.IsTag("APPRAISAL_ITEM"), "Not an appraisal item.");
                    int estimated = Number(args, "value"), correct = (int)AppraisalHelper.GetCorrectBaseValue(appraised);
                    AppraisalServiceHelper.OnEstimate(appraised, estimated);
                    if (InspectionUIManager.Instance != null && InspectionUIManager.Instance.currentInspectingItem == appraised) InspectionUIManager.Instance.CloseUI();
                    // The fee follows from the estimate's accuracy (effects.cash_change).
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["estimate"] = estimated, ["correct_value"] = correct, ["off_by_percent"] = correct == 0 ? 0 : Math.Round(100.0 * Math.Abs(estimated - correct) / correct, 1) };
                }
                case "menu.open":
                    Require(StoreUIManager.Instance.escapeUIManager != null, "Escape menu is unavailable.");
                    StoreUIManager.Instance.escapeUIManager.OpenEscapeMenu(); break;
                case "day.open":
                case "day.close":
                    Require(StoreShutterButton.Instance != null && store.lockShutterButtonFlag == 0 && !NegociationUIManager.IsTradeLocked(), "Shutter is locked.");
                    if (action == "day.close" && store.storeState == PlayerStore.StoreState.OPEN && !store.CanEndDay())
                    {
                        // PlayerStore.CanEndDay: visitors who cannot be dismissed must be seen first.
                        var waiting = store.storeClientManager.clientStack.Where(client => !client.dismissable).Select(client => client.displayName).ToList();
                        if (store.currentClientInstance != null && !store.currentClientInstance.GetClientBlueprint().dismissable) waiting.Insert(0, store.currentClientInstance.GetClientBlueprint().displayName);
                        throw new BridgeException("precondition_failed", "The day cannot end until these visitors are seen: " + string.Join(", ", waiting) + ".");
                    }
                    Require(action == "day.open" ? store.storeState == PlayerStore.StoreState.MORNING && store.CanStartDay() : store.storeState == PlayerStore.StoreState.OPEN, "Shutter cannot change in this phase.");
                    if (action == "day.close" && store.storeClientManager.clientStack.Count > 0)
                    {
                        if (args["skip_queue"]?.Type != JTokenType.Boolean || !(bool)args["skip_queue"])
                        {
                            StoreShutterButton.Instance.ShowConfirmCloseUI();
                            return new JObject { ["dispatched"] = true, ["action"] = action, ["confirmation_shown"] = true, ["queued_customers"] = store.storeClientManager.clientStack.Count };
                        }
                        StoreShutterButton.Instance.EndAndClose();
                        break;
                    }
                    StoreShutterButton.Instance.OnClick(); break;
                case "day.sleep":
                    Require(NightSurface.CanSleep, "Open the nightly services menu and use its enabled end-day button.");
                    EndOfDayUIManager.Instance.OnEndDayButtonClick(); break;
                case "customer.next":
                    Require(Flow.CustomerCallable, store.storeState != PlayerStore.StoreState.OPEN ? "The store is not open (phase " + store.storeState + ")."
                        : store.currentClientInstance != null ? store.currentClientInstance.GetClientBlueprint().displayName + " is still at the counter" + (store.IsClientDimissable() ? " (customer.dismiss sends them away)." : " and cannot be dismissed.")
                        : (store.storeClientManager?.clientStack?.Count ?? 0) == 0 ? "Nobody is left in today's queue."
                        : "Next customer cannot be called yet: wait {\"customer_callable\": true} first."); store.TryCallNextClient(); break;
                case "dialogue.advance":
                {
                    var talk = DialogUIManager.Instance;
                    Require(talk != null && talk.currentDialog != null, store.currentClientInstance != null && !Flow.CustomerReady
                        ? "The customer has not started talking yet: wait {\"customer_ready\": true} first." : "No active dialogue.");
                    // OnAdvancePressed only finishes the typing while a line is being typed; the next call moves on.
                    if (args["all"]?.Type == JTokenType.Boolean && (bool)args["all"])
                    {
                        Advancing = true; advanceCount = 0; idleFrames = 0;
                        return new JObject { ["dispatched"] = true, ["action"] = action, ["did"] = "advanced to a choice or the end" };
                    }
                    bool typing = (bool)ModReflect.Call(talk, "IsAnyBubbleTyping");
                    talk.OnAdvancePressed();
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["did"] = typing ? "finished_typing" : "advanced" };
                }
                case "dialogue.choose":
                    // DialogChoiceBubble.OnDialogChoiceClick calls DialogUIManager.SelectChoice with the choice index.
                    var dialog = DialogUIManager.Instance; int index = Number(args, "index");
                    Require(dialog != null && dialog.currentDialog != null && dialog.IsChoice() && index < dialog.currentDialog.choices.Count && dialog.currentDialog.IsChoiceEnabledAt(index), "Choice is not offered or not enabled.");
                    dialog.SelectChoice(index);
                    return new JObject { ["dispatched"] = true, ["action"] = action, ["did"] = "chose " + index };
                default:
                    Require(store.currentClientInstance != null, "No customer at the counter.");
                    Require(store.isClientArrived, "The customer is still walking in or out: wait {\"customer_arrived\": true} or {\"no_customer\": true}.");
                    Require(store.lockClientClickFlag == 0, "The customer cannot be interacted with during the current scene (locks.customer).");
                    Require(!NegociationUIManager.IsTradeLocked(), "Trading is locked during an arrest or a shootout (NegociationUIManager.IsTradeLocked).");
                    if (action == "customer.dismiss") { Require(store.IsClientDimissable(), store.currentClientInstance.GetClientBlueprint().displayName + " cannot be dismissed (dismissable: false)."); store.TryPlayerDismissCurrentClient(false); }
                    else if (action == "negotiation.open") { Require(NegociationUIManager.Instance != null, "Negotiation UI unavailable."); NegociationUIManager.Instance.OpenUI(); }
                    else if (action == "trade.buy" || action == "trade.sell" || action == "trade.weigh")
                    {
                        var client = store.currentClientInstance.GetClientBlueprint();
                        if (action == "trade.weigh")
                        {
                            var weighed = Item(args, "item_id");
                            if (weighed.parentInventory != EmporiumEntry.Instance.backInvinvElement)
                                Require(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.backInvinvElement, weighed, weighed.unitCount) == 0, CounterRoom(weighed));
                            store.currentNegociatedItem = weighed; // As PlayerStore.AddedItemToWeightedArea, also when it was already there.
                            store.RefreshCounterItem();
                            var scale = new JObject { ["dispatched"] = true, ["action"] = action, ["item_id"] = weighed.GetUniqueID(), ["name"] = weighed.GetDisplayName(true), ["owned"] = GeneralHelper.IsItemOwned(weighed),
                                ["price"] = weighed.GetNegociatedValue(), ["true_value"] = ItemObservation.TrueValue(weighed), ["weighed"] = store.currentNegociatedItem == weighed };
                            if (GeneralHelper.IsItemOwned(weighed)) { var refusal = SellRefusal(client, weighed); scale["sellable"] = refusal == null; if (refusal != null) scale["not_sellable_because"] = refusal; }
                            else scale["affordable"] = weighed.GetNegociatedValue() <= store.playerCash;
                            return scale;
                        }
                        if (action == "trade.buy" && args["item_id"] != null)
                        {
                            // Weigh the chosen item: onto the weighing counter, as a player drags it there.
                            var chosen = Item(args, "item_id");
                            Require(!GeneralHelper.IsItemOwned(chosen), "You already own that item.");
                            if (chosen.parentInventory != EmporiumEntry.Instance.backInvinvElement)
                                Require(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.backInvinvElement, chosen, chosen.unitCount) == 0, CounterRoom(chosen));
                            // Already on the scale: the one weighed is the last put there (PlayerStore.AddedItemToWeightedArea), so weigh this one again.
                            store.currentNegociatedItem = chosen;
                            store.RefreshCounterItem();
                        }
                        var item = store.currentNegociatedItem;
                        Require(!client.isBusinessComplete, "This customer's business is complete.");
                        Require(item != null, action == "trade.buy" ? "Nothing is weighed: give item_id (one of trade.counter.buy.items) to weigh and buy it." : "Nothing is weighed: put the item to sell on the weighing counter.");
                        Require(NegociationUIManager.Instance != null, "Negotiation UI unavailable.");
                        if (!NegociationUIManager.Instance.IsOpen) NegociationUIManager.Instance.OpenUI(); // As TradeSurface: the tablet opens with the trade.
                        if (action == "trade.buy")
                        {
                            Require(!GeneralHelper.IsItemOwned(item), "The weighed item is already yours.");
                            Require(client.IsClientSelling(), "This customer is not selling.");
                            Require(store.CanBuyItemFromTable(), "Cannot buy it at the current offer (" + item.GetNegociatedValue() + " credits; you have " + store.playerCash + ").");
                            NegociationUIManager.Instance.BuyItem();
                        }
                        else
                        {
                            Require(GeneralHelper.IsItemOwned(item), "The weighed item is not yours.");
                            Require(client.IsClientBuying(), "This customer is not buying.");
                            Require(store.CanSellItemFromTable(), "Cannot sell the weighed item: " + (SellRefusal(client, item) ?? "the game refuses the sale") + ".");
                            NegociationUIManager.Instance.TrySellItem();
                        }
                    }
                    else if (action == "bargain.offer")
                    {
                        var bargain = BargainUIManager.Instance; Require(bargain != null, "Bargaining is unavailable."); // Can* below refresh the tracked customer.
                        int percent = Number(args, "percent");
                        if (percent != 10 && percent != 25 && percent != 50 && percent != 75)
                            throw new BridgeException("invalid_arguments", "percent must match a game button: 10, 25, 50 or 75.");
                        var haggled = store.currentClientInstance.GetClientBlueprint();
                        int budgetBefore = haggled.GetBudget();
                        // The price features a bargain applies land on the items on the weighing counter at that moment.
                        var counterBefore = EmporiumEntry.Instance.backInvinvElement.childItems.Concat(EmporiumEntry.Instance.GetAllNonOwnedItem()).Distinct().ToDictionary(counterItem => counterItem, counterItem => counterItem.GetNegociatedValue());
                        int wholesaleBefore = haggled.isOfferWholesale ? store.GetWholesalePrice() : -1;
                        var intentBefore = haggled.clientIntent;
                        switch ((string)args["kind"])
                        {
                            case "discount": Require(bargain.CanOfferDiscount(), "Discount unavailable."); bargain.OfferDiscount(percent); break;
                            case "markup": Require(bargain.CanOfferMarkup(), "Markup unavailable."); bargain.OfferMarkup(percent); break;
                            case "buying_markup": Require(bargain.CanOfferBuyingMarkup(), "Buying markup unavailable."); bargain.OfferBuyingMarkup(percent); break;
                            case "buying_discount": Require(bargain.CanDemandBuyingDiscount(), "Buying discount unavailable."); bargain.DemandBuyingDiscount(percent); break;
                            default: throw new BridgeException("invalid_arguments", "Unsupported bargain kind.");
                        }
                        // BargainUIManager: a refused markup or buying discount may send the customer away (-3 faction reputation, intent DIALOGUE).
                        string kind = (string)args["kind"];
                        int applied = kind == "discount" ? bargain.DiscountPercent : kind == "markup" ? bargain.MarkupPercent : kind == "buying_markup" ? bargain.BuyingMarkupPercent : bargain.BuyingDiscountPercent;
                        string outcome = haggled.clientIntent == StoreClient.ClientIntent.DIALOGUE && intentBefore != StoreClient.ClientIntent.DIALOGUE ? "refused_and_leaving"
                            : applied == percent ? "accepted" : "refused";
                        var offered = new JObject { ["dispatched"] = true, ["action"] = action, ["outcome"] = outcome, ["budget_before"] = budgetBefore, ["budget_after"] = haggled.GetBudget(),
                            ["counter_prices"] = new JArray(counterBefore.Where(pair => pair.Key != null).Select(pair => new JObject { ["item_id"] = pair.Key.GetUniqueID(), ["name"] = pair.Key.GetDisplayName(true), ["before"] = pair.Value, ["after"] = pair.Key.GetNegociatedValue() })) };
                        if (wholesaleBefore >= 0) { offered["wholesale_before"] = wholesaleBefore; offered["wholesale_after"] = store.GetWholesalePrice(); }
                        if (outcome == "refused_and_leaving") offered["note"] = "the customer leaves after their line; the shop lost 3 reputation with their faction";
                        return offered;
                    }
                    else throw new BridgeException("unknown_action", "Unknown gameplay action: " + action);
                    break;
            }
            var done = new JObject { ["dispatched"] = true, ["action"] = action };
            if (action.StartsWith("customer.", StringComparison.Ordinal)) done["at_counter"] = AtCounter(); // After the call: who is there now.
            return done;
        }
    }
}

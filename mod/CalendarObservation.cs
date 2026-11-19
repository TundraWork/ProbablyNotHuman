using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;

namespace ProbablyNotHuman
{
    internal static class CalendarObservation
    {
        internal static JObject Read()
        {
            var calendar = AdvCalendarUIManager.Instance;
            var result = new JObject { ["available"] = calendar != null && SurfaceAdapter.ActiveSave,
                ["open"] = calendar != null && SurfaceAdapter.Shown(calendar.UIPanel) };
            if (calendar == null || !SurfaceAdapter.ActiveSave) return result;

            var store = PlayerStore.Instance;
            var station = StoreStation.Instance;
            int today = station.dayCounter;
            int weekday = today % 7;
            if (weekday == 0) weekday = 7;
            int firstDay = today - weekday + 1;
            var active = new JArray();
            var future = new JArray();
            var seen = new HashSet<StoreEvent>();
            // Do not call OpenUI/HandleInnateEvent: both mutate the event queues.
            for (int day = firstDay; day < firstDay + 14; day++)
                foreach (var entry in station.storeEventManager.GetActiveEventOnDay(day))
                {
                    if (!seen.Add(entry)) continue;
                    bool queued = station.storeEventManager.futurEvents.Contains(entry);
                    var effects = new JArray();
                    foreach (var effect in entry.negociationDatas)
                        if (effect.value != 0)
                            effects.Add(new JObject { ["item_type"] = effect.itemType,
                                ["item_type_display"] = TypeHelper.GetTypeDisplayName(effect.itemType),
                                ["percent"] = effect.value });
                    var observed = new JObject { ["id"] = entry.identifier,
                        ["news_title"] = entry.newsName, ["news_description"] = entry.newsDescription, ["hidden_from_newspaper"] = entry.isHiddenFromNewsPaper,
                        ["display_name"] = entry.GetDisplayName(), ["known"] = entry.IsKnown, ["hidden"] = entry.isHidden,
                        ["start_day"] = entry.startDay, ["duration"] = entry.totalDuration,
                        ["end_day"] = entry.startDay + entry.totalDuration - 1,
                        ["permanent"] = entry.isPermanent,
                        ["calendar_description"] = entry.GetEventPriceEffect(), ["price_effects"] = effects };
                    if (queued)
                    {
                        int index = station.storeEventManager.futurEvents.IndexOf(entry);
                        observed["days_until_start"] = station.storeEventManager.futurEventsDay[index];
                        future.Add(observed);
                    }
                    else
                    {
                        observed["days_remaining"] = entry.duration;
                        active.Add(observed);
                    }
                }
            result["first_day"] = firstDay;
            result["last_day"] = firstDay + 13;
            result["active_events"] = active;
            result["future_events"] = future;
            var queue = new JArray();
            for (int i = 0; i < station.storeEventManager.futurEvents.Count; i++)
            {
                var entry = station.storeEventManager.futurEvents[i];
                var queuedEffects = new JArray();
                foreach (var effect in entry.negociationDatas)
                    if (effect.value != 0) queuedEffects.Add(new JObject { ["item_type"] = effect.itemType, ["percent"] = effect.value });
                queue.Add(new JObject { ["id"] = entry.identifier, ["display_name"] = entry.GetDisplayName(), ["known"] = entry.IsKnown, ["hidden"] = entry.isHidden,
                    ["days_until_start"] = station.storeEventManager.futurEventsDay[i], ["duration"] = entry.totalDuration, ["calendar_description"] = entry.GetEventPriceEffect(),
                    ["price_effects"] = queuedEffects, ["news_title"] = entry.newsName });
            }
            result["future_event_queue"] = queue;

            int attractiveness = store.GetCurrentStoreAttractiveness();
            int penalty = store.GetTrashAttPenalty();
            result["attractiveness"] = new JObject { ["total"] = attractiveness,
                ["base"] = store.baseStoreAttractiveness, ["trash_penalty"] = penalty,
                ["projector_bonus"] = store.projectorAttractivenessBonus,
                ["trash_description"] = NightLog.GetGarbageDescription(store.garbageLevel),
                ["vandalized"] = store.isVandalized };

            var pendingLogs = new JArray();
            foreach (var log in store.nightLogs) pendingLogs.Add(log);
            var renderedLogs = new JArray();
            var morning = StartOfDayUIManager.Instance;
            // InitPanel clears nightLogs immediately after rendering; the retained native
            // report labels are the player's actual last report, even after dismissal.
            if (morning != null && morning.contentGroupObject != null)
                foreach (var label in morning.contentGroupObject.GetComponentsInChildren<TMP_Text>(true))
                    if (label.enabled && !string.IsNullOrEmpty(label.text)) renderedLogs.Add(label.text);
            result["nightly_report"] = new JObject { ["pending_entries"] = pendingLogs, ["last_rendered_entries"] = renderedLogs,
                ["last_report"] = new JObject { ["day"] = Flow.ReportDay, ["entries"] = new JArray(Flow.ReportEntries) } };

            if (!store.IsPropertyPaid)
            {
                string deadline = AdvCalendarUIManager.GetFormattedDayDisplay(store.dayUntilRent);
                string notice = store.isPayingMortgage
                    ? LocHelper.GetLocalizedMechanic("mech_rent_loan_shark", store.mortgageWeeklyPayment, deadline)
                    : store.temporaryRent == 0
                        ? LocHelper.GetLocalizedMechanic("mech_rent_landlord", store.rentValue, deadline)
                        : LocHelper.GetLocalizedMechanic("mech_rent_landlord_missed", store.rentValue, store.temporaryRent, deadline);
                result["property_payment"] = new JObject { ["kind"] = store.isPayingMortgage ? "mortgage" : "rent",
                    ["days_until_due"] = store.dayUntilRent,
                    ["amount"] = store.isPayingMortgage ? store.mortgageWeeklyPayment : store.rentValue,
                    ["notice"] = notice };
                if (!store.isPayingMortgage) result["property_payment"]["rent_arrears"] = store.temporaryRent;
            }

            // GetNormalCustomerCount consumes RNG; never invoke it from observation.
            // Read counts/predictions only from the actual visible clientele UI instead.
            var clientele = ClienteleUIManager.Instance;
            if (clientele != null && SurfaceAdapter.Shown(clientele.panel))
            {
                string title = SurfaceObservation.Text(clientele.normalCustomerTitleTMP);
                var customers = new JObject { ["normal_customer_title"] = title,
                    ["normal_customer_display"] = SurfaceObservation.Text(clientele.normalCustomerTMP),
                    ["showcase_customer_display"] = SurfaceObservation.Text(clientele.merchandiseCustomerTMP) };
                for (int count = 0; count <= 6; count++)
                    if (title == LocHelper.GetLocalizedUI("ui_clientele_random", count) + " <link=\"ui_clientele_normal_tooltip\"><sprite name=\"info\" tint=1></link>")
                        customers["normal_customer_count"] = count;
                result["customers"] = customers;
            }
            return result;
        }
    }
}

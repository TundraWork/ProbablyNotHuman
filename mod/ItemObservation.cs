using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace ProbablyNotHuman
{
    internal static class ItemObservation
    {
        internal static bool Compact;
        internal static JObject Read(GameItem item, bool withTags = false)
        {
            if (Compact && !withTags) return Brief(item);
            var shape = item.modifiedShape;
            var result = new JObject {
                ["id"] = item.GetUniqueID(), ["identifier"] = item.identifier, ["name"] = item.GetDisplayName(true),
                ["description"] = item.shortDescription, ["count"] = item.unitCount, ["types"] = Types(item),
                ["owned"] = GeneralHelper.IsItemOwned(item), ["unit_value"] = item.unitValue,
                ["value"] = new JObject { ["base"] = item.GetBaseValue(), ["current"] = item.GetCurrentValue(), ["retail"] = item.GetCurrentValue(useRetailMarkup: true),
                    ["negotiated"] = item.GetNegociatedValue(), ["true"] = TrueValue(item) },
                ["features"] = Features(item),
                ["can_activate"] = item.CanActivateSlotItem(),
                ["can_toggle"] = item.CanToggleSlotItem(),
                ["can_configure"] = item.CanConfigureSlotItem(),
                ["can_remove"] = item.MayRemove()
            };
            result["can_select"] = item.MaySelectSlotItem() && item.CanSelectSlotItem();
            if (item.IsTag("BOUGHT_PRICE_TAG")) result["paid"] = item.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt();
            if (item.IsTag("APPRAISAL_ITEM")) result["appraisal_value"] = AppraisalHelper.GetCorrectBaseValue(item);
            if (StolenHelper.IsStolenItem(item)) result["stolen_heat_percent"] = StolenHelper.GetFoundChance(item);
            if (item.IsTag("LIQUID_CONTAINER_TAG"))
            {
                var parts = new JObject();
                foreach (var liquid in Liquid.Liquids) { int amount = item.GetTagReadonly(liquid.currentPart).GetInt(); if (amount != 0) parts[liquid.identifier] = amount; }
                result["liquid"] = new JObject { ["water_purity_percent"] = WaterHelper.GetWaterPurity(item) / 100.0, ["volume"] = WaterHelper.GetTotalVolume(item), ["parts"] = parts };
            }
            if (withTags)
            {
                var tags = new JObject();
                foreach (var pair in item.modifiedState.dict)
                {
                    var tag = pair.Value;
                    if (!tag.valueEnabled) continue;
                    tags[pair.Key] = !string.IsNullOrEmpty(tag.valueString) ? tag.valueString : tag.valueInt != 0 ? tag.valueInt : tag.valueLong != 0 ? tag.valueLong
                        : tag.valueFloat != 0 ? tag.valueFloat : tag.valueDouble != 0 ? tag.valueDouble : (JToken)true;
                }
                result["tags"] = tags;
            }
            if (!Compact && item is GameItemElement rendered && rendered.background != null)
            {
                result["geometry"] = ViewGeometry.Read(rendered.background);
                result["ui_path"] = UIAdapter.PathOf(rendered.handler.transform);
            }
            if (shape != null)
                result["shape"] = Compact ? new JObject { ["x"] = shape.minX, ["y"] = shape.minY, ["width"] = shape.globalWidth, ["height"] = shape.globalHeight, ["orientation"] = shape.orientation } : ReadShape(shape, false);
            // Invoke the player's complete formatter, including the default item tooltip.
            if (item is TooltipListener tooltip && tooltip.HasTooltip())
                result["tooltip"] = tooltip.GetTooltip()?.nakedText;
            result["has_content_window"] = item.contentWindow != null;
            result["content_visible"] = item.contentWindow != null && item.contentWindow.IsVisible();
            result["has_examination_window"] = item.examineWindow != null;
            bool inspectionVisible = item.examineWindow != null && item.examineWindow.IsVisible() &&
                item.examineWindow.handler != null && item.examineWindow.handler.gameObject.activeInHierarchy;
            result["inspection_visible"] = inspectionVisible;
            if (inspectionVisible)
            {
                // Read rendered output, not tags: e.g. a double-meat ribwich hides its bun
                // and pickles, while barcode/facility text may independently be covered.
                var root = item.examineWindow.handler;
                var images = new JArray();
                foreach (var image in root.GetComponentsInChildren<Image>(false))
                {
                    if (!image.enabled || !image.gameObject.activeInHierarchy || image.color.a <= 0f || image.sprite == null) continue;
                    var p = image.transform.localPosition;
                    var c = image.color;
                    images.Add(new JObject { ["element"] = image.gameObject.name, ["sprite"] = image.sprite.name,
                        ["color"] = new JArray(c.r, c.g, c.b, c.a),
                        ["position"] = new JArray(p.x, p.y, p.z),
                        ["rotation_degrees"] = image.transform.localEulerAngles.z });
                }
                var text = new JArray();
                foreach (var label in root.GetComponentsInChildren<TMP_Text>(false))
                    if (label.enabled && label.gameObject.activeInHierarchy && label.color.a > 0f)
                        text.Add(new JObject { ["element"] = label.gameObject.name, ["text"] = label.text });
                result["inspection_appearance"] = new JObject { ["images"] = images, ["text"] = text };
            }
            return result;
        }
        private static JArray Types(GameItem item)
        {
            var types = new JArray();
            foreach (string type in TypeHelper.ALL_TYPES_LIST) if (item.IsGameItemType(type)) types.Add(type);
            return types;
        }
        // Compact observations: identity, value, liquid and only the features whose public claim differs from the truth.
        private static JObject Brief(GameItem item)
        {
            var result = new JObject { ["id"] = item.GetUniqueID(), ["identifier"] = item.identifier, ["name"] = item.GetDisplayName(true), ["count"] = item.unitCount, ["types"] = Types(item),
                ["owned"] = GeneralHelper.IsItemOwned(item), ["value"] = item.GetCurrentValue(), ["price"] = item.GetNegociatedValue(), ["true_value"] = TrueValue(item) };
            if (item.IsTag("BOUGHT_PRICE_TAG")) result["paid"] = item.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt();
            if (item.IsTag("APPRAISAL_ITEM")) result["appraisal_value"] = AppraisalHelper.GetCorrectBaseValue(item);
            if (StolenHelper.IsStolenItem(item)) result["stolen_heat_percent"] = StolenHelper.GetFoundChance(item);
            var shape = item.modifiedShape;
            if (shape != null) result["at"] = new JArray(shape.minX, shape.minY, shape.globalWidth, shape.globalHeight, shape.orientation);
            var lies = new JArray();
            if (item.itemFeatures != null)
                foreach (var feature in item.itemFeatures)
                    if (feature.featureType != ItemFeature.FeatureType.Event && feature.isExposable && !feature.isDisabled && !feature.IsExposed() && !feature.IsFeatureMatch()
                        && (feature.GetFakeValueModifier() != feature.GetActualValueModifier() || feature.GetBaseFakeDisplay() != feature.GetBaseRealDisplay()))
                        lies.Add(new JObject { ["id"] = feature.identifier, ["claims"] = feature.GetBaseFakeDisplay(), ["really"] = feature.GetBaseRealDisplay(),
                            ["claimed_modifier"] = feature.GetFakeValueModifier(), ["real_modifier"] = feature.GetActualValueModifier() });
            if (lies.Count > 0) result["false_features"] = lies;
            if (item.contentWindow != null) result["container"] = true;
            if (item.IsTag("LIQUID_CONTAINER_TAG")) result["water_purity_percent"] = WaterHelper.GetWaterPurity(item) / 100.0;
            return result;
        }
        // GameItem.GetCurrentValue with every feature at its real modifier, as if all were exposed.
        internal static long TrueValue(GameItem item)
        {
            long innate = 0, market = 0; double final = 1;
            foreach (var active in StoreStation.Instance.storeEventManager.GetActiveEvents())
                foreach (var data in active.negociationDatas) if (item.IsTag(data.itemType)) market += data.value;
            if (item.itemFeatures != null)
                foreach (var feature in item.itemFeatures)
                {
                    if (feature.isDisabled || feature.featureType == ItemFeature.FeatureType.Event) continue;
                    int mod = feature.GetActualValueModifier();
                    switch (feature.GetValueStage())
                    {
                        case ItemFeature.ValueStage.Market: market += mod; break;
                        case ItemFeature.ValueStage.Final: final *= Math.Max(0.0, 1.0 + mod / 100.0); break;
                        default: innate += mod; break;
                    }
                }
            double innateValue = Math.Max(0.0, (item.unitValue + item.GetComplexeItemValue()) * (1.0 + innate / 100.0));
            return Math.Max(0L, (long)Math.Round(((innateValue + WaterHelper.GetContainerPrice(item)) * Math.Max(0.0, 1.0 + market / 100.0) + item.GetItemMiscValue()) * final));
        }
        private static JArray Features(GameItem item)
        {
            var result = new JArray();
            if (item.itemFeatures == null) return result;
            foreach (var feature in item.itemFeatures)
                result.Add(new JObject { ["id"] = feature.identifier, ["category"] = feature.category, ["type"] = feature.featureType.ToString(),
                    ["stage"] = feature.GetValueStage().ToString(), ["public_display"] = feature.GetBaseFakeDisplay(), ["real_display"] = feature.GetBaseRealDisplay(),
                    ["public_modifier"] = feature.GetFakeValueModifier(), ["real_modifier"] = feature.GetActualValueModifier(), ["current_modifier"] = feature.GetCurrentValueMod(),
                    ["matches_real"] = feature.IsFeatureMatch(), ["exposed"] = feature.IsExposed(), ["discovered"] = feature.IsFeatureDiscovered(),
                    ["initially_shown"] = feature.IsInitiallyShown(), ["public_hidden"] = feature.GetPublicHidden(), ["exposable"] = feature.isExposable, ["disabled"] = feature.isDisabled });
            return result;
        }
        internal static JObject ReadShape(GridShape shape, bool inventory)
        {
            var rows = new JArray();
            for (int y = shape.minY; y <= shape.maxY; y++)
            {
                var row = new JArray();
                for (int x = shape.minX; x <= shape.maxX; x++) row.Add((int)shape.Get(x, y));
                rows.Add(row);
            }
            return new JObject { ["x"] = shape.minX, ["y"] = shape.minY, ["width"] = shape.globalWidth,
                ["height"] = shape.globalHeight, ["orientation"] = shape.orientation, ["flipped"] = shape.flipped,
                ["cells"] = rows, ["cell_policy"] = inventory ? "0 is usable; nonzero is blocked. Rows increase y, columns increase x." : "1 marks occupied item cells. Rows increase y, columns increase x." };
        }
    }
}

using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace ProbablyNotHuman
{
    internal static class SurfaceObservation
    {
        internal static string Text(TMP_Text text) => SurfaceAdapter.Shown(text) ? text.text : null;
        private static JArray ActiveList(List<GameObject> objects, string[] labels = null)
        {
            var result = new JArray();
            if (objects == null) return result;
            for (int i = 0; i < objects.Count; i++) if (SurfaceAdapter.Shown(objects[i]))
                result.Add(new JObject { ["index"] = i, ["label"] = labels != null && i < labels.Length ? labels[i] : null, ["object"] = objects[i].name });
            return result;
        }
        private static JArray FuelIndicators(FuelUI ui, string field)
        {
            var result = new JArray();
            var pairs = SurfaceAdapter.Field<List<FuelUI.StringGameObjectPair>>(ui, field);
            if (pairs != null) foreach (var pair in pairs) if (SurfaceAdapter.Shown(pair.Value)) result.Add(pair.Key);
            return result;
        }
        internal static JObject Inspection(Func<GameItem,JToken> reference)
        {
            var manager = InspectionUIManager.Instance;
            var result = new JObject { ["active"] = manager != null && SurfaceAdapter.Shown(manager.panel) };
            if (manager == null) return result;
            // Both small preview and enlarged panels can be visible; never initialize them from hidden tags.
            result["item"] = SurfaceAdapter.Shown(manager.panel) ? reference(manager.currentInspectingItem) : JValue.CreateNull();
            var panels = new JArray();
            foreach (var ui in UnityEngine.Object.FindObjectsOfType<FuelUI>()) if (SurfaceAdapter.Shown(ui))
                panels.Add(new JObject { ["kind"]="fuel", ["path"]=UIAdapter.PathOf(ui.transform), ["color"]=FuelIndicators(ui,"color"), ["logo"]=FuelIndicators(ui,"logo"), ["condition"]=FuelIndicators(ui,"condition"), ["seal"]=FuelIndicators(ui,"seal"), ["serial"]=Text(ui.bottomSerial) });
            foreach (var ui in UnityEngine.Object.FindObjectsOfType<AppHandUI>()) if (SurfaceAdapter.Shown(ui))
            {
                var appearance = new JObject();
                Hand(appearance,"steel",ui.steelHands,ui.steelAddons,ui.steelFullInterfaces,ui.steelSymbolInterfaces);
                Hand(appearance,"carbon_composite",ui.carbonHands,ui.carbonAddons,ui.carbonFullInterfaces,ui.carbonSymbolInterfaces);
                Hand(appearance,"gold_plated",ui.goldHands,ui.goldAddons,ui.goldFullInterfaces,ui.goldSymbolInterfaces);
                panels.Add(new JObject { ["kind"]="hand", ["path"]=UIAdapter.PathOf(ui.transform), ["appearance"]=appearance });
            }
            foreach (var ui in UnityEngine.Object.FindObjectsOfType<RibwichUI>()) if (SurfaceAdapter.Shown(ui))
                panels.Add(new JObject { ["kind"]="ribwich", ["path"]=UIAdapter.PathOf(ui.transform), ["normal_meat_visible"]=SurfaceAdapter.Shown(ui.meatNormal), ["double_meat_visible"]=SurfaceAdapter.Shown(ui.meatDouble), ["top_bun"]=ActiveList(ui.topBun), ["wrap"]=ActiveList(ui.wrap), ["pickles"]=ActiveList(ui.pickles), ["davre_logo"]=ActiveList(ui.davreLogo), ["ribwich_logo"]=ActiveList(ui.ribwichLogo), ["barcode_visible"]=SurfaceAdapter.Shown(ui.barcode), ["production_date"]=SurfaceAdapter.Shown(ui.barcode) ? Text(ui.barcodeTMP) : null, ["facility_visible"]=SurfaceAdapter.Shown(ui.facility), ["facility"]=SurfaceAdapter.Shown(ui.facility) ? Text(ui.facilityTMP) : null });
            result["appearance_panels"] = panels;
            return result;
        }
        private static void Hand(JObject result, string material, List<GameObject> hands, List<GameObject> addons, List<GameObject> full, List<GameObject> symbols)
        {
            var body = ActiveList(hands,new[] { "normal","wear","damage" });
            if (body.Count == 0) return;
            result[material] = new JObject { ["condition"]=body, ["addons"]=ActiveList(addons,new[] { "armored","bladed" }), ["full_interfaces"]=ActiveList(full,new[] { "EMG","EMG_PLUS","NEURAL" }), ["symbol_interfaces"]=ActiveList(symbols,new[] { "EMG","EMG_PLUS","NEURAL" }) };
        }
        internal static JArray Scanners(Func<GameItem,JToken> reference)
        {
            var result = new JArray(); var chem = ChemScannerUIManager.Instance;
            if (chem != null && SurfaceAdapter.Shown(chem.panel))
            {
                var effects = new JObject();
                Effect(effects,"euphoria",chem.euphoriaEffect); Effect(effects,"toxicity",chem.toxicityEffect); Effect(effects,"synaprest",chem.synaprestEffect); Effect(effects,"birth_control",chem.birthControlEffect);
                result.Add(new JObject { ["kind"]="chem_scanner", ["mode"]=chem.currentScannedItem != null && chem.currentScannedItem.IsTag("CHEM_ITEM_TAG") ? "chemical" : "water", ["item"]=reference(chem.currentScannedItem), ["product_name"]=Text(chem.productNameTMP), ["tested"]=Text(chem.testedTMP), ["label"]=Text(chem.labelTMP), ["contaminants"]=Text(chem.contaminantTMP), ["total_volume"]=Text(chem.totalVolumeTMP), ["effects"]=effects, ["confront_available"]=SurfaceAdapter.Shown(chem.confrontButton) && chem.confrontButton.IsInteractable(), ["match_available"]=SurfaceAdapter.Shown(chem.matchButton) && chem.matchButton.IsInteractable() });
            }
            var water = WaterScannerUIManager.Instance;
            if (water != null && SurfaceAdapter.Shown(water.panel)) result.Add(new JObject { ["kind"]="water_scanner", ["mode"]="water", ["item"]=reference(water.currentWaterContainer), ["tested"]=Text(water.testedTMP), ["label"]=Text(water.labelTMP), ["contaminants"]=Text(water.contaminantTMP), ["confront_available"]=SurfaceAdapter.Shown(water.confrontButton) && water.confrontButton.IsInteractable(), ["match_available"]=SurfaceAdapter.Shown(water.matchButton) && water.matchButton.IsInteractable() });
            var strip = WaterStripUIManager.Instance;
            if (strip != null && SurfaceAdapter.Shown(strip.panel)) result.Add(new JObject { ["kind"]="water_test_strip", ["item"]=reference(strip.currentWaterContainer), ["tested"]=Text(strip.testedTMP), ["label"]=Text(strip.labelTMP), ["purity"]=Text(strip.purityTMP), ["confront_available"]=SurfaceAdapter.Shown(strip.confrontButton) && strip.confrontButton.IsInteractable(), ["match_available"]=SurfaceAdapter.Shown(strip.matchButton) && strip.matchButton.IsInteractable() });
            var plant = PlantAnalyserUIManager.Instance;
            if (plant != null && SurfaceAdapter.Shown(plant.UIPanel)) result.Add(new JObject { ["kind"]="plant_analyser", ["item"]=reference(plant.currentItem), ["title"]=Text(plant.titleTMP), ["body"]=Text(plant.bodyTMP), ["nutriment_range"]=Text(plant.nutrimentRangeTMP), ["nutrient_use"]=Text(plant.nutrientUseTMP), ["contaminant_sensitivity"]=Text(plant.contaminantSensitivityTMP), ["water_usage"]=Text(plant.waterUsageTMP), ["growth_text"]=Text(plant.body2TMP), ["growth_fill"]=SurfaceAdapter.Shown(plant.growthScoreTilledImage) ? (JToken)new JValue(plant.growthScoreTilledImage.fillAmount) : JValue.CreateNull() });
            return result;
        }
        private static void Effect(JObject result, string key, ChemScannerEffect effect) { if (SurfaceAdapter.Shown(effect)) result[key]=Text(effect.effectTMP); }
        internal static JArray Papers()
        {
            var result = new JArray();
            foreach (var panel in UnityEngine.Object.FindObjectsOfType<PaperUIPanel>())
            {
                var paper = panel.owner;
                if (!SurfaceAdapter.Shown(panel) || paper == null || !paper.IsVisible) continue;
                var elements = new JArray();
                foreach (var binding in paper.VisibleBindings())
                {
                    if (!SurfaceAdapter.Shown(binding.go) || binding.data == null) continue;
                    elements.Add(new JObject { ["id"]=binding.go.GetInstanceID(), ["element_key"]=binding.data.id, ["type"]=binding.data.type, ["action"]=binding.data.action, ["text"]=Text(binding.text), ["sprite"]=binding.image != null && binding.image.enabled && binding.image.sprite != null ? binding.image.sprite.name : null, ["clickable"]=SurfaceAdapter.Shown(binding.button) && binding.button.IsInteractable(), ["path"]=UIAdapter.PathOf(binding.go.transform) });
                }
                result.Add(new JObject { ["id"]=paper.Id, ["current_page"]=paper.CurrentPage, ["page_count"]=paper.PageCount, ["editor_mode"]=paper.EditorMode, ["can_flip_left"]=paper.CurrentPage > 0, ["can_flip_right"]=paper.CurrentPage+1 < paper.PageCount, ["elements"]=elements });
            }
            return result;
        }
        // Every open reader (newspaper, notes, guides, books, papers) as its visible text in reading order: top to bottom, then left to right.
        internal static JArray Images(Transform root)
        {
            var names = new JArray();
            foreach (var image in root.GetComponentsInChildren<UnityEngine.UI.Image>())
            {
                // Buttons are listed as controls; a sprite made at runtime has no name of its own, so its texture's is used.
                if (image.sprite == null || !SurfaceAdapter.Shown(image) || image.GetComponentInParent<UnityEngine.UI.Selectable>() != null) continue;
                string name = !string.IsNullOrEmpty(image.sprite.name) ? image.sprite.name : image.sprite.texture != null && !string.IsNullOrEmpty(image.sprite.texture.name) ? image.sprite.texture.name : "unnamed image";
                if (!names.Any(known => (string)known == name)) names.Add(name);
            }
            return names;
        }
        internal static JArray Reading()
        {
            var roots = new List<(string kind, GameObject root)> {
                ("newspaper", NewsUIManager.Instance?.uiGameObject), ("handnote", HandnoteUIManager.Instance?.panel), ("multi_page", MultiPageUIManager.Instance?.panel),
                ("paper", PaperUIManager.Instance?.panel), ("book", PaperUIManager.Instance?.bookPanel), ("aug_id_guide", PaperUIManager.Instance?.augIdGuide) };
            foreach (var panel in UnityEngine.Object.FindObjectsOfType<PaperUIPanel>()) roots.Add(("paper_ui", panel.gameObject));
            // Any other item panel on the game's panel canvas (guides, lists, ledgers), named as flow.modal names it; the tablet, phone and night menu have their own sections.
            var canvas = StoreUIManager.Instance?.paperUICanvasObject;
            if (canvas != null)
                foreach (Transform child in canvas.transform)
                {
                    var go = child.gameObject;
                    if (!go.activeSelf || go == NegociationUIManager.Instance?.uiObject || go == PhoneUIManager.Instance?.phonePanel || (EndOfDayUIManager.Instance != null && EndOfDayUIManager.Instance.IsOpen && go.name.Contains("EndOfDay"))) continue;
                    if (roots.Exists(known => known.root != null && (known.root == go || known.root.transform.IsChildOf(go.transform) || go.transform.IsChildOf(known.root.transform)))) continue;
                    roots.Add((Modal.PanelId(go), go));
                }
            var result = new JArray();
            var corners = new Vector3[4];
            foreach (var (kind, root) in roots)
            {
                if (!SurfaceAdapter.Shown(root)) continue;
                var lines = new List<(float y, float x, string text)>();
                foreach (var text in root.GetComponentsInChildren<TMP_Text>())
                    if (SurfaceAdapter.Shown(text) && !string.IsNullOrWhiteSpace(text.text)) { text.rectTransform.GetWorldCorners(corners); lines.Add((corners[1].y, corners[1].x, text.text.Trim())); }
                // Lines whose tops are within a few units share a row.
                lines.Sort((a, b) => Mathf.Abs(a.y - b.y) > 2f ? b.y.CompareTo(a.y) : a.x.CompareTo(b.x));
                var texts = new JArray();
                foreach (var line in lines) texts.Add(line.text);
                if (texts.Count > 0) result.Add(new JObject { ["kind"] = kind, ["texts"] = texts });
                // A known reader that is all pictures (the augment identification guide): its images by sprite name; ui.capture shows them.
                else if (!kind.StartsWith("panel:") && Images(root.transform) is JArray images && images.Count > 0) result.Add(new JObject { ["kind"] = kind, ["texts"] = texts, ["images"] = images });
            }
            return result;
        }
        // The security investigation into the shop (PlayerStore.secData), as the security record shows it.
        internal static JObject SecurityCase()
        {
            var case_ = PlayerStore.Instance.secData;
            if (case_ == null) return null;
            var crimes = new JArray();
            if (case_.crimeTypes != null)
                for (int i = 0; i < case_.crimeTypes.Count; i++)
                    crimes.Add(new JObject { ["crime"] = case_.crimeTypes[i], ["value"] = i < case_.crimeAmount?.Count ? case_.crimeAmount[i] : 0, ["value_total"] = i < case_.crimeAmountTotal?.Count ? case_.crimeAmountTotal[i] : 0 });
            return new JObject { ["crimes"] = crimes, ["total_value"] = case_.GetTotalCrimeValue(), ["evidence_level"] = case_.evidenceLevel, ["evidence"] = case_.GetEvidenceLevelDisplay(),
                ["recommended_action"] = case_.GetAction(), ["recommended_action_code"] = case_.recommendedAction, ["state"] = case_.state, ["projected_sentence"] = case_.GetProjectedSentence(), ["case_officer"] = case_.assignedCaseOfficer, ["officer_pending"] = case_.IsOfficerPending() };
        }
        internal static JObject LegacyPaper(Func<GameItem,JToken> reference)
        {
            var manager = PaperUIManager.Instance; var result = new JObject(); if (manager == null) return result;
            if (SurfaceAdapter.Shown(manager.panel)) result["paper"] = new JObject { ["title"]=Text(manager.title), ["body"]=Text(manager.body), ["signature"]=Text(manager.signature) };
            if (SurfaceAdapter.Shown(manager.bookPanel))
            {
                var item = PaperUIManager.currentBook;
                result["book"] = new JObject { ["item"]=reference(item), ["current_page"]=item != null ? (JToken)new JValue(item.GetTagReadonly("CURRENT_BOOK_PAGE").GetInt()) : JValue.CreateNull(), ["title"]=Text(manager.bookTitle), ["subtitle"]=Text(manager.bookSubtitle), ["body"]=Text(manager.bookBody), ["page_label"]=Text(manager.bookPage), ["can_flip_left"]=item != null && BookHelper.CanFlipLeft(item), ["can_flip_right"]=item != null && BookHelper.CanFlipRight(item) };
                var index = new JArray(); if (manager.indexElements != null) foreach (var text in manager.indexElements) if (SurfaceAdapter.Shown(text)) index.Add(text.text); result["book_index"] = index;
            }
            return result;
        }
    }
}

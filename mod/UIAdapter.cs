using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProbablyNotHuman
{
    // Handles refer to a particular observed surface, never a screen coordinate supplied by the agent.
    public sealed class UIAdapter
    {
        private sealed class Target
        {
            public Component component;
            public SelectableElement element;
            public string kind;
            public string label;
            public Vector2 point;
            public string id;
        }
        private readonly Dictionary<string, Target> targets = new Dictionary<string, Target>();
        private readonly List<RaycastResult> hits = new List<RaycastResult>();
        private readonly Vector3[] corners = new Vector3[4];
        private int revision;
        private bool compactRows; // Rows hold only id/kind/label/state, and only for controls a click reaches now.
        // Compact rows keep one control per GameObject: its hover, pointer and click components are one thing to an agent.
        private readonly Dictionary<GameObject, (JObject row, int rank)> compactByObject = new Dictionary<GameObject, (JObject, int)>();
        private static int Rank(string kind) => kind == "hover_surface" ? 0 : kind == "pointer_surface" ? 1 : kind == "drag_surface" || kind == "scroll_surface" ? 2 : kind == "pointer_button" ? 3 : 4;
        private Target holding;
        private float holdUntil;
        private PointerEventData holdEvents;
        private JObject holdStatus = new JObject { ["status"] = "idle" };
        public bool IsHolding => holding != null;
        internal static UIAdapter Current { get; private set; }
        public UIAdapter() { Current = this; }
        // After an action, controls stay addressable until destroyed or re-observed: a ticket's cells or a list's buttons need no observe between uses.
        public void Invalidate()
        {
            var dead = new List<string>();
            foreach (var pair in targets) if (pair.Value.component == null) dead.Add(pair.Key);
            foreach (var id in dead) targets.Remove(id);
        }
        public void CancelHold()
        {
            if (holding?.element != null) holding.element.Defocus();
            else if (holding?.component != null && holdEvents != null)
            {
                ExecuteEvents.Execute(holding.component.gameObject, holdEvents, ExecuteEvents.pointerExitHandler);
                ExecuteEvents.Execute(holding.component.gameObject, holdEvents, ExecuteEvents.pointerUpHandler);
            }
            holding = null;
            holdStatus = new JObject { ["status"] = "cancelled" };
        }
        public bool PumpHold()
        {
            if (holding == null || Time.unscaledTime < holdUntil) return false;
            var target = holding;
            holding = null;
            try
            {
                if (target.element != null)
                {
                    if (target.element.isSelected) target.element.Deselect(); else target.element.Select();
                }
                else ExecuteEvents.Execute(target.component.gameObject, holdEvents, ExecuteEvents.pointerUpHandler);
                holdStatus["status"] = "completed";
            }
            catch (Exception exception) { holdStatus["status"] = "failed"; holdStatus["error"] = exception.Message; }
            finally
            {
                if (target.element != null) target.element.Defocus();
                else if (target.component != null)
                {
                    ExecuteEvents.Execute(target.component.gameObject, holdEvents, ExecuteEvents.pointerExitHandler);
                    ExecuteEvents.Execute(target.component.gameObject, holdEvents, ExecuteEvents.pointerUpHandler);
                }
                Invalidate();
            }
            return true;
        }

        // Inverse of PathOf: each "Name[sibling]" segment is followed by sibling index, checked against the name.
        internal static Transform FindByPath(string path)
        {
            string[] segments = path.Split('/');
            (string name, int index) Parse(string segment)
            {
                int open = segment.LastIndexOf('[');
                if (open < 0 || !segment.EndsWith("]", StringComparison.Ordinal)) return (segment, -1);
                return int.TryParse(segment.Substring(open + 1, segment.Length - open - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? (segment.Substring(0, open), i) : (segment, -1);
            }
            var (rootName, rootIndex) = Parse(segments[0]);
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (candidate.parent != null || candidate.name != rootName || candidate.GetSiblingIndex() != rootIndex) continue;
                Transform current = candidate;
                for (int i = 1; i < segments.Length && current != null; i++)
                {
                    var (name, index) = Parse(segments[i]);
                    current = index >= 0 && index < current.childCount && current.GetChild(index).name == name ? current.GetChild(index) : null;
                }
                if (current != null) return current;
            }
            return null;
        }
        internal static string PathOf(Transform transform)
        {
            string path = transform.name + "[" + transform.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + "]";
            while (transform.parent != null) { transform = transform.parent; path = transform.name + "[" + transform.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + "]/" + path; }
            return path;
        }
        private static bool Active(Component component)
        {
            if (component == null || !component.gameObject.activeInHierarchy) return false;
            if (component is Behaviour behaviour && !behaviour.enabled) return false;
            float alpha = 1f;
            for (Transform t = component.transform; t != null; t = t.parent)
            {
                foreach (var group in t.GetComponents<CanvasGroup>())
                {
                    alpha *= group.alpha;
                    if (alpha < 0.01f) return false;
                }
            }
            return true;
        }
        private bool ScreenPoint(Component component, out Vector2 point)
        {
            point = Vector2.zero;
            var rect = component.transform as RectTransform;
            if (rect == null)
            {
                var worldCamera = Camera.main;
                if (worldCamera == null) return false;
                var collider = component.GetComponent<Collider>();
                var collider2D = component.GetComponent<Collider2D>();
                Vector3 center;
                if (collider != null && collider.enabled) center = collider.bounds.center;
                else if (collider2D != null && collider2D.enabled) center = collider2D.bounds.center;
                else
                {
                    var renderer = component.GetComponentInChildren<SpriteRenderer>();
                    if (renderer == null || !renderer.enabled || renderer.color.a < 0.01f) return false;
                    center = renderer.bounds.center;
                }
                Vector3 screen = worldCamera.WorldToScreenPoint(center);
                point = screen;
                return screen.z > 0 && point.x >= 0 && point.y >= 0 && point.x <= Screen.width && point.y <= Screen.height;
            }
            var canvas = component.GetComponentInParent<Canvas>();
            if (canvas == null || !canvas.isActiveAndEnabled) return false;
            var camera = ViewGeometry.CameraFor(canvas);
            rect.GetWorldCorners(corners);
            point = RectTransformUtility.WorldToScreenPoint(camera, (corners[0] + corners[2]) * 0.5f);
            if (point.x < 0 || point.y < 0 || point.x > Screen.width || point.y > Screen.height) return false;
            foreach (var mask in component.GetComponentsInParent<RectMask2D>())
                if (mask.isActiveAndEnabled && !RectTransformUtility.RectangleContainsScreenPoint(mask.rectTransform, point, camera)) return false;
            return true;
        }
        private bool Reachable(Target target)
        {
            if (!Active(target.component) || !ScreenPoint(target.component, out target.point)) return false;
            if (target.element != null && (target.element.isHidden || target.element.isDisabled)) return false;
            if (target.component is Selectable selectable && !selectable.IsInteractable()) return false;
            var world = target.component.GetComponent<Interactable>();
            if (world != null && (!world.isAvailable || Interactable.isPaused)) return false;
            if (EventSystem.current == null) return false;
            hits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = target.point }, hits);
            if (hits.Count == 0) return false;
            Transform hit = hits[0].gameObject.transform;
            return hit == target.component.transform || hit.IsChildOf(target.component.transform);
        }
        private string Label(Component component)
        {
            var tooltip = component.GetComponent<TooltipInterface>();
            if (tooltip != null && tooltip.IsTooltipEnabled()) return tooltip.GetTooltip();
            var text = component.GetComponentInChildren<TMP_Text>();
            if (text != null && !string.IsNullOrWhiteSpace(text.text)) return text.text;
            var legacy = component.GetComponentInChildren<Text>();
            if (legacy != null && !string.IsNullOrWhiteSpace(legacy.text)) return legacy.text;
            var image = component.GetComponent<Image>();
            if (image != null && image.sprite != null) return image.sprite.name;
            return component.gameObject.name;
        }
        private static bool Labelled(Component component)
        {
            var tooltip = component.GetComponent<TooltipInterface>();
            return (tooltip != null && tooltip.IsTooltipEnabled()) || !string.IsNullOrWhiteSpace(component.GetComponentInChildren<TMP_Text>()?.text) || !string.IsNullOrWhiteSpace(component.GetComponentInChildren<Text>()?.text);
        }
        private void Add(JArray controls, Target target)
        {
            if (!Active(target.component) || !ScreenPoint(target.component, out target.point)) return;
            target.id = revision.ToString(CultureInfo.InvariantCulture) + ":" + target.component.GetInstanceID().ToString(CultureInfo.InvariantCulture);
            if (targets.ContainsKey(target.id)) return;
            bool reachable = Reachable(target);
            // A disabled control is still information (the action exists but is off now); covered or hidden ones are left out.
            bool disabled = !reachable && ((target.component is Selectable selectable && !selectable.IsInteractable()) || (target.element != null && target.element.isDisabled));
            if (compactRows && !reachable && !disabled) return;
            if (compactRows && compactByObject.TryGetValue(target.component.gameObject, out var existing))
            {
                if (Rank(target.kind) <= existing.rank) return;
                targets.Remove((string)existing.row["id"]);
                targets[target.id] = target;
                existing.row["id"] = target.id; existing.row["kind"] = target.kind;
                if (string.IsNullOrWhiteSpace((string)existing.row["label"])) existing.row["label"] = target.label;
                compactByObject[target.component.gameObject] = (existing.row, Rank(target.kind));
                return;
            }
            targets[target.id] = target;
            var row = new JObject { ["id"] = target.id, ["kind"] = target.kind, ["label"] = target.label };
            if (disabled) row["disabled"] = true;
            if (compactRows) compactByObject[target.component.gameObject] = (row, Rank(target.kind));
            if (!compactRows)
            {
                row["path"] = PathOf(target.component.transform);
                row["interactable"] = reachable;
                row["geometry"] = ViewGeometry.Read(target.component);
                row["activation_point"] = new JArray(target.point.x / Mathf.Max(1, Screen.width - 1), target.point.y / Mathf.Max(1, Screen.height - 1));
                var pointerEvents = new JArray();
                if (target.component is IPointerEnterHandler) pointerEvents.Add("enter");
                if (target.component is IPointerExitHandler) pointerEvents.Add("exit");
                if (target.component is IPointerDownHandler) pointerEvents.Add("down");
                if (target.component is IPointerUpHandler) pointerEvents.Add("up");
                if (target.component is IPointerClickHandler) pointerEvents.Add("click");
                if (target.component is IDragHandler) pointerEvents.Add("drag");
                if (target.component is IScrollHandler) pointerEvents.Add("scroll");
                row["pointer_events"] = pointerEvents;
            }
            if (target.component is TreeNodeRender renderNode)
            {
                row["window"] = renderNode.FindParentWindow()?.titleString;
                var operations = new JArray();
                if (target.element != null) operations.Add(target.element.requiresHold ? "ui.hold" : "ui.click");
                else if (renderNode.eventHandler?.mouseClickFunc.Count > 0) operations.Add("ui.click");
                if (renderNode.eventHandler?.mouseDragFunc.Count > 0) operations.Add("ui.drag");
                if (renderNode.eventHandler?.mouseScrollFunc.Count > 0) operations.Add("ui.scroll");
                row["operations"] = operations;
            }
            if (target.component is TMP_InputField input) { row["value"] = input.text; row["read_only"] = input.readOnly; row["max_length"] = input.characterLimit; row["validation"] = input.characterValidation.ToString(); }
            else if (target.component is InputField legacy) { row["value"] = legacy.text; row["read_only"] = legacy.readOnly; row["max_length"] = legacy.characterLimit; row["validation"] = legacy.characterValidation.ToString(); }
            else if (target.component is Toggle toggle) row["value"] = toggle.isOn;
            else if (target.component is Slider slider) { row["value"] = slider.value; row["min"] = slider.minValue; row["max"] = slider.maxValue; row["whole_numbers"] = slider.wholeNumbers; }
            else if (target.component is Scrollbar bar) { row["value"] = bar.value; row["min"] = 0; row["max"] = 1; }
            else if (target.component is TMP_Dropdown dropdown) { row["value"] = dropdown.value; var options = new JArray(); foreach (var option in dropdown.options) options.Add(option.text); row["options"] = options; }
            else if (target.component is Dropdown oldDropdown) { row["value"] = oldDropdown.value; var options = new JArray(); foreach (var option in oldDropdown.options) options.Add(option.text); row["options"] = options; }
            if (target.component is TreeNodeRender machinery)
            {
                if (machinery.element is MachinerySlider machinerySlider) { row["value"] = machinerySlider.value; row["min"] = machinerySlider.min; row["max"] = machinerySlider.max; row["step"] = machinerySlider.step; row["disabled"] = machinerySlider.isDisabled; }
                else if (machinery.element is MachineryRangeSlider range) { row["value"] = new JArray(range.low, range.high); row["min"] = range.min; row["max"] = range.max; row["step"] = range.step; row["disabled"] = range.isDisabled; }
                else if (machinery.element is MachineryProgressBar progress) row["value"] = progress.value;
            }
            if (target.element != null) { row["selected"] = target.element.isSelected; row["hold_seconds"] = target.element.requiresHold ? target.element.holdDuration : 0; }
            var hold = target.component.GetComponent<IHoldClickable>();
            if (hold != null) row["hold_seconds"] = hold.ShouldRequireHold() ? hold.HoldDuration : 0;
            var worldSprite = target.component.GetComponent<SpriteRenderer>();
            if (worldSprite != null && worldSprite.sprite != null) row["sprite"] = worldSprite.sprite.name;
            controls.Add(row);
        }
        internal bool Within(string id, Transform scope) => id != null && targets.TryGetValue(id, out var target) && target.component != null && target.component.transform.IsChildOf(scope);
        // The texts and reachable controls of one panel, for a panel covering the rest of the screen.
        public JObject ObservePanel(Transform scope)
        {
            compactRows = true;
            compactByObject.Clear();
            revision++;
            targets.Clear();
            Canvas.ForceUpdateCanvases();
            var texts = new JArray();
            var controls = new JArray();
            // Reading order: top to bottom, then left to right (lines whose tops are within a few units share a row).
            var lines = new List<(float y, float x, string text)>();
            foreach (var graphic in scope.GetComponentsInChildren<Graphic>())
            {
                string value = graphic is TMP_Text tmp ? tmp.text : graphic is Text legacy ? legacy.text : null;
                if (value == null || !Active(graphic) || string.IsNullOrWhiteSpace(value) || !ScreenPoint(graphic, out _)) continue;
                graphic.rectTransform.GetWorldCorners(corners);
                lines.Add((corners[1].y, corners[1].x, value.Trim()));
            }
            lines.Sort((a, b) => Mathf.Abs(a.y - b.y) > 2f ? b.y.CompareTo(a.y) : a.x.CompareTo(b.x));
            foreach (var line in lines) texts.Add(line.text);
            foreach (var component in scope.GetComponentsInChildren<Selectable>())
                Add(controls, new Target { component = component, kind = component is Button ? "button" : component is Toggle ? "toggle" : "selectable", label = Label(component) });
            foreach (var component in scope.GetComponentsInChildren<MonoBehaviour>())
                if (!(component is Selectable) && (component is IClickable || component is IPointerClickHandler) && Labelled(component)) // Skips decorative markers.
                    Add(controls, new Target { component = component, kind = component is IClickable ? "game_button" : "pointer_button", label = Label(component) });
            var panel = new JObject { ["texts"] = texts, ["controls"] = controls };
            if (texts.Count == 0) panel["images"] = SurfaceObservation.Images(scope); // A panel of pictures: their sprite names (ui.capture shows them).
            return panel;
        }
        public JObject Observe(bool compact = false)
        {
            compactRows = compact;
            compactByObject.Clear();
            revision++;
            targets.Clear();
            Canvas.ForceUpdateCanvases();
            var controls = new JArray();
            var texts = new JArray();
            var visuals = new JArray();
            if (!compact)
            foreach (var image in UnityEngine.Object.FindObjectsOfType<Image>())
                if (Active(image) && image.color.a >= 0.01f && image.sprite != null && ScreenPoint(image, out var position))
                    visuals.Add(new JObject { ["path"] = PathOf(image.transform), ["sprite"] = image.sprite.name, ["color_rgba"] = new JArray(image.color.r, image.color.g, image.color.b, image.color.a), ["fill"] = image.fillAmount, ["rotation_degrees"] = image.transform.localEulerAngles.z, ["position_normalized"] = new JArray(position.x / Screen.width, position.y / Screen.height), ["size"] = new JArray(image.rectTransform.rect.width, image.rectTransform.rect.height) });
            var worldCamera = Camera.main;
            if (!compact)
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<SpriteRenderer>())
            {
                if (!Active(renderer) || !renderer.enabled || renderer.sprite == null || renderer.color.a < 0.01f || worldCamera == null || worldCamera.WorldToViewportPoint(renderer.bounds.center).z <= 0) continue;
                var geometry = ViewGeometry.Read(renderer);
                if (!ViewGeometry.OnScreen(geometry)) continue;
                visuals.Add(new JObject { ["kind"] = "world_sprite", ["path"] = PathOf(renderer.transform), ["sprite"] = renderer.sprite.name, ["color_rgba"] = new JArray(renderer.color.r, renderer.color.g, renderer.color.b, renderer.color.a), ["sorting_layer"] = renderer.sortingLayerName, ["sorting_order"] = renderer.sortingOrder, ["rotation_degrees"] = renderer.transform.eulerAngles.z, ["geometry"] = geometry });
            }
            if (!compact)
            foreach (var image in UnityEngine.Object.FindObjectsOfType<RawImage>())
                if (Active(image) && image.color.a >= 0.01f && ScreenPoint(image, out _))
                    visuals.Add(new JObject { ["kind"] = "raw_image", ["path"] = PathOf(image.transform), ["texture_size"] = image.texture == null ? null : new JArray(image.texture.width, image.texture.height), ["shader"] = image.material?.shader?.name, ["color_rgba"] = new JArray(image.color.r, image.color.g, image.color.b, image.color.a), ["geometry"] = ViewGeometry.Read(image) });
            foreach (var text in UnityEngine.Object.FindObjectsOfType<TMP_Text>())
                if (Active(text) && !string.IsNullOrWhiteSpace(text.text) && ScreenPoint(text, out _))
                    texts.Add(compact ? (JToken)text.text : new JObject { ["text"] = text.text, ["path"] = PathOf(text.transform) });
            foreach (var text in UnityEngine.Object.FindObjectsOfType<Text>())
                if (Active(text) && !string.IsNullOrWhiteSpace(text.text) && ScreenPoint(text, out _))
                    texts.Add(compact ? (JToken)text.text : new JObject { ["text"] = text.text, ["path"] = PathOf(text.transform) });
            foreach (var component in UnityEngine.Object.FindObjectsOfType<Selectable>())
            {
                string kind = component is Button ? "button" : component is Toggle ? "toggle" : component is TMP_InputField || component is InputField ? "input" : component is Slider || component is Scrollbar ? "slider" : component is TMP_Dropdown || component is Dropdown ? "dropdown" : "selectable";
                Add(controls, new Target { component = component, kind = kind, label = Label(component) });
            }
            foreach (var node in UnityEngine.Object.FindObjectsOfType<TreeNodeRender>())
            {
                if (node.element is SelectableElement element)
                    Add(controls, new Target { component = node, element = element, kind = "pixel_button", label = element is ButtonElement button ? button.text : Label(node) });
                else if (node.element is MachinerySlider || node.element is MachineryRangeSlider || node.element is MachineryProgressBar)
                    Add(controls, new Target { component = node, kind = node.element is MachinerySlider ? "machinery_slider" : node.element is MachineryRangeSlider ? "machinery_range" : "machinery_progress",
                        label = (node.element as MachinerySlider)?.description ?? (node.element as MachineryRangeSlider)?.description ?? Label(node) });
                else if (node.eventHandler != null && (node.eventHandler.mouseClickFunc.Count > 0 || node.eventHandler.mouseDragFunc.Count > 0 || node.eventHandler.mouseScrollFunc.Count > 0))
                    Add(controls, new Target { component = node, kind = "render_control", label = Label(node) });
            }
            foreach (var component in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (component is Selectable || component is TreeNodeRender) continue;
                if (component is IPointerClickHandler || component is IPointerDownHandler || component is IPointerUpHandler || component is IPointerEnterHandler || component is IPointerExitHandler || component is IDragHandler || component is IScrollHandler || component is IClickable || component is ScratchInput)
                    Add(controls, new Target { component = component, kind = component is ScratchInput ? "scratch_surface" : component is IClickable ? "game_button" : component is IDragHandler ? "drag_surface" : component is IScrollHandler ? "scroll_surface" : component is IPointerClickHandler ? "pointer_button" : component is IPointerDownHandler || component is IPointerUpHandler ? "pointer_surface" : "hover_surface", label = component is TooltipInterface tooltip ? tooltip.GetTooltip() : Label(component) });
            }
            return new JObject { ["revision"] = revision, ["viewport"] = new JObject { ["width"] = Screen.width, ["height"] = Screen.height }, ["texts"] = texts, ["visuals"] = visuals, ["controls"] = controls, ["hold"] = holdStatus.DeepClone() };
        }
        public JArray DescribeActions()
        {
            var schema = new JObject { ["type"] = "object", ["properties"] = new JObject { ["target"] = new JObject { ["type"] = "string", ["description"] = "Control ID from the most recent observe response, or a ui_path." },
                ["ui_path"] = new JObject { ["type"] = "string", ["description"] = "A control's ui_path, instead of target." } }, ["required"] = new JArray(), ["additionalProperties"] = false };
            JObject Definition(string name, string description, JObject args) => new JObject { ["name"] = name, ["description"] = description, ["arguments"] = args };
            var set = (JObject)schema.DeepClone();
            ((JObject)set["properties"])["value"] = new JObject { ["description"] = "Input text, toggle boolean, slider number, [low, high] for machinery_range, or zero-based dropdown index." };
            ((JArray)set["required"]).Add("value");
            var drag = (JObject)schema.DeepClone();
            ((JObject)drag["properties"])["points"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["items"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2 }, ["description"] = "Points normalized to the target rectangle; [0,0] is bottom-left, values outside 0..1 leave the rectangle." };
            ((JArray)drag["required"]).Add("points");
            var scroll = (JObject)schema.DeepClone();
            ((JObject)scroll["properties"])["delta"] = new JObject { ["type"] = "number" };
            ((JArray)scroll["required"]).Add("delta");
            return new JArray(Definition("ui.click", "Activate an observed interactable button through its normal game callback.", schema), Definition("ui.hold", "Begin a hold-to-confirm control; game time must pass before activation. Observe ui.hold.status for completion. ui.click activates hold controls instantly.", (JObject)schema.DeepClone()), Definition("ui.set", "Set an observed input, toggle, slider, or dropdown through its normal change events.", set), Definition("ui.drag", "Deliver a drag path to an observed UI drag surface.", drag), Definition("ui.scratch", "Scratch a ticket panel through the game's brush and reveal checks: along points (normalized path), or the whole panel when points are omitted.", Optional((JObject)drag.DeepClone(), "points")), Definition("ui.scroll", "Scroll an observed UI surface.", scroll));
        }
        public JObject Act(string action, JObject args)
        {
            string id = (string)args["target"] ?? (string)args["ui_path"];
            if (id == null) throw new BridgeException("invalid_arguments", "target (a control ID) or ui_path is required.");
            // A ui_path (as surfaces report them) addresses the control on that object directly.
            if (!targets.TryGetValue(id, out var target) && id.Contains("/") && FindByPath(id) is Transform found)
            {
                Component control = found.GetComponent<ScratchInput>() ?? (Component)found.GetComponent<Selectable>() ?? found.GetComponent<TreeNodeRender>()
                    ?? (Component)found.GetComponents<MonoBehaviour>().FirstOrDefault(c => c is IClickable || c is IPointerClickHandler || c is IDragHandler || c is IScrollHandler);
                if (control != null)
                {
                    target = new Target { component = control, element = (control as TreeNodeRender)?.element as SelectableElement, kind = "path", label = Label(control) };
                    if (!ScreenPoint(control, out target.point)) throw new BridgeException("not_visible", "The control at that path is not on screen.");
                    target.id = id; targets[id] = target;
                }
            }
            if (target == null) throw new BridgeException("stale_target", "Observe again and use a current control ID (or a ui_path).");
            if (target.component == null) throw new BridgeException("stale_target", "Control was destroyed. Observe again.");
            Reachable(target); // Refreshes the activation point and raycast hits; covered controls are still dispatched.
            var raycast = hits.Count > 0 ? hits[0] : new RaycastResult { gameObject = target.component.gameObject, screenPosition = target.point };
            var events = new PointerEventData(EventSystem.current) { position = target.point, button = PointerEventData.InputButton.Left, pointerCurrentRaycast = raycast, pointerPressRaycast = raycast, pointerPress = target.component.gameObject };
            if (action == "ui.click")
            {
                if (target.element != null)
                {
                    target.element.Press();
                    if (target.element.isSelected) target.element.Deselect(); else target.element.Select();
                    target.element.Defocus();
                }
                else if (target.component is Button button) button.onClick.Invoke();
                else if (target.component.GetComponent<Interactable>() != null)
                {
                    ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerEnterHandler);
                    ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerClickHandler);
                    ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerExitHandler);
                }
                else if (target.component is IClickable clickable) clickable.OnClick();
                else if (target.component is IPointerClickHandler || target.component is IPointerDownHandler || target.component is IPointerUpHandler || (target.component is TreeNodeRender renderNode && renderNode.eventHandler?.mouseClickFunc.Count > 0))
                {
                    ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerClickHandler);
                }
                else throw new BridgeException("wrong_control_kind", "This control requires ui.set, ui.drag, or ui.scroll.");
            }
            else if (action == "ui.hold")
            {
                if (holding != null) CancelHold();
                float duration;
                if (target.element != null && target.element.requiresHold)
                {
                    duration = target.element.holdDuration;
                    target.element.Press();
                }
                else
                {
                    var hold = target.component.GetComponent<IHoldClickable>();
                    var world = target.component.GetComponent<Interactable>();
                    if (hold == null || !hold.ShouldRequireHold() || world == null || world.isControlable) throw new BridgeException("wrong_control_kind", "Not a hold-to-confirm control.");
                    duration = hold.HoldDuration > 0 ? hold.HoldDuration : 1.25f;
                    ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerEnterHandler);
                    ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerDownHandler);
                    holdEvents = events;
                }
                holding = target;
                holdUntil = Time.unscaledTime + duration;
                holdStatus = new JObject { ["status"] = "pending", ["target"] = id, ["duration_seconds"] = duration };
                Invalidate();
                return (JObject)holdStatus.DeepClone();
            }
            else if (action == "ui.set")
            {
                var value = args["value"] ?? throw new BridgeException("invalid_arguments", "value is required.");
                if (target.component is TMP_InputField input && value.Type == JTokenType.String) { input.text = (string)value; input.onEndEdit.Invoke(input.text); }
                else if (target.component is InputField oldInput && value.Type == JTokenType.String) { oldInput.text = (string)value; oldInput.onEndEdit.Invoke(oldInput.text); }
                else if (target.component is Toggle toggle && value.Type == JTokenType.Boolean) toggle.isOn = (bool)value;
                else if (target.component is Slider slider && Number(value, out float number)) slider.value = number;
                else if (target.component is Scrollbar bar && Number(value, out float fraction)) bar.value = fraction;
                else if (target.component is TMP_Dropdown dropdown && value.Type == JTokenType.Integer) dropdown.value = (int)value;
                else if (target.component is Dropdown oldDropdown && value.Type == JTokenType.Integer) oldDropdown.value = (int)value;
                else if (target.component is TreeNodeRender sliderNode && sliderNode.element is MachinerySlider machinerySlider && Number(value, out float sliderValue))
                { machinerySlider.SetValue(sliderValue, notify: true); machinerySlider.onReleased?.Invoke(machinerySlider.value); }
                else if (target.component is TreeNodeRender rangeNode && rangeNode.element is MachineryRangeSlider range && value is JArray bounds && bounds.Count == 2 && Number(bounds[0], out float low) && Number(bounds[1], out float high))
                { range.SetValues(low, high, notify: true); range.onReleased?.Invoke(range.low, range.high); }
                else throw new BridgeException("invalid_value", "Value does not match this control's type.");
            }
            else if (action == "ui.scratch")
            {
                if (!(target.component is ScratchInput scratch)) throw new BridgeException("wrong_control_kind", "Not a scratch surface.");
                var stroke = ModReflect.Method<Action<int, int>>(scratch, "StrokeTo");
                scratch.OnAnyScratch();
                if (args["points"] is JArray points)
                    foreach (JArray pair in points) stroke(Mathf.Clamp((int)((float)pair[0] * 56), 0, 55), Mathf.Clamp((int)((1 - (float)pair[1]) * 56), 0, 55));
                else
                    // Without points: a back-and-forth stroke over the whole 56x56 panel, as scratching it all off by hand.
                    for (int y = 0; y < 56; y += 3) { stroke(y % 6 == 0 ? 0 : 55, y); stroke(y % 6 == 0 ? 55 : 0, y); }
            }
            else if (action == "ui.drag")
            {
                if (!(target.component is IDragHandler)) throw new BridgeException("wrong_control_kind", "Not a drag surface.");
                var points = (JArray)args["points"];
                var positions = new List<Vector2>(points.Count);
                var rect = (RectTransform)target.component.transform;
                var canvas = target.component.GetComponentInParent<Canvas>();
                var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                foreach (var point in points)
                {
                    if (!(point is JArray pair) || pair.Count != 2 || !Number(pair[0], out float x) || !Number(pair[1], out float y)) throw new BridgeException("invalid_arguments", "Drag points must be [x,y] number pairs.");
                    positions.Add(RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(new Vector3(rect.rect.xMin + x * rect.rect.width, rect.rect.yMin + y * rect.rect.height))));
                }
                events.position = positions[0]; events.pressPosition = positions[0]; events.pointerDrag = target.component.gameObject;
                ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.beginDragHandler);
                for (int i = 1; i < positions.Count; i++) { events.delta = positions[i] - events.position; events.position = positions[i]; ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.dragHandler); }
                ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.endDragHandler);
                ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.pointerUpHandler);
            }
            else if (action == "ui.scroll")
            {
                if (!(target.component is IScrollHandler) || !Number(args["delta"], out float delta)) throw new BridgeException("invalid_arguments", "Requires a scroll surface and a numeric delta.");
                events.scrollDelta = new Vector2(0, delta);
                ExecuteEvents.Execute(target.component.gameObject, events, ExecuteEvents.scrollHandler);
            }
            else throw new BridgeException("unknown_action", action);
            Invalidate(); // A callback may have destroyed controls; the rest keep their IDs.
            return new JObject { ["performed"] = action, ["target"] = id };
        }
        private static JObject Optional(JObject schema, string name) { ((JArray)schema["required"]).Remove(((JArray)schema["required"]).First(token => (string)token == name)); return schema; }
        private static bool Number(JToken value, out float number)
        {
            number = 0;
            if (value == null || (value.Type != JTokenType.Float && value.Type != JTokenType.Integer)) return false;
            number = (float)value;
            return !float.IsNaN(number) && !float.IsInfinity(number);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace ProbablyNotHuman
{
    // Only this component advances state, in LateUpdate, for the following Update frame.
    [DefaultExecutionOrder(32000)]
    public sealed class VirtualInput : MonoBehaviour
    {
        private const string Owner = "probably_not_human.input";
        private static VirtualInput current;
        private Harmony harmony;
        private VirtualBaseInput uiInput;
        private ModLog log;
        private bool initialized, virtualFrame, cancelRequested, releaseFrame;
        private long sequence;
        private string status = "idle";
        private Step[] steps;
        private int stepIndex = -1, remaining, elapsed;
        private Vector3 pointer;
        private Vector2 scroll;
        private readonly HashSet<string> axisNames = new HashSet<string>(StringComparer.Ordinal) { "Mouse X", "Mouse Y" };
        private readonly HashSet<string> buttonNames = new HashSet<string>(StringComparer.Ordinal);
        private Dictionary<string, float> axes = new Dictionary<string, float>(StringComparer.Ordinal);
        private HashSet<string> namedButtons = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> previousNamedButtons = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<KeyCode> held = new HashSet<KeyCode>();
        private readonly HashSet<KeyCode> previous = new HashSet<KeyCode>();
        private readonly Dictionary<StandaloneInputModule, BaseInput> modules = new Dictionary<StandaloneInputModule, BaseInput>();
        private readonly Dictionary<EventSystem, (bool focus, bool applicationFocus)> focusRestore = new Dictionary<EventSystem, (bool, bool)>();
        private bool originalBackground, changedBackground;
        private readonly JArray patched = new JArray(), failures = new JArray();
        private static readonly Dictionary<MethodInfo, MethodInfo> replacements = CreateReplacements();
        private sealed class Step { public int Frames; public Vector2? Pointer; public Vector2 Scroll; public HashSet<KeyCode> Keys; public Dictionary<string, float> Axes; public HashSet<string> NamedButtons; }
        public bool IsBusy => steps != null || virtualFrame;

        public void Initialize(ModLog logger)
        {
            if (initialized) return;
            if (current != null && current != this) throw new InvalidOperationException("Virtual input already initialized.");
            log = logger; current = this; initialized = true;
            pointer = new Vector3((Screen.width - 1) * 0.5f, (Screen.height - 1) * 0.5f, 0);
            originalBackground = Application.runInBackground;
            changedBackground = !originalBackground;
            if (changedBackground) Application.runInBackground = true;
            uiInput = gameObject.AddComponent<VirtualBaseInput>();
            harmony = new Harmony(Owner);
            PatchGame();
            BindModules();
            SceneManager.sceneLoaded += SceneLoaded;
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode) { BindModules(); }
        private void BindModules()
        {
            var dead = new List<StandaloneInputModule>();
            foreach (var pair in modules) if (pair.Key == null) dead.Add(pair.Key);
            foreach (var module in dead) modules.Remove(module);
            foreach (StandaloneInputModule module in UnityEngine.Object.FindObjectsOfType<StandaloneInputModule>(true))
            {
                if (!modules.ContainsKey(module)) modules.Add(module, module.inputOverride);
                else if (module.inputOverride != uiInput) modules[module] = module.inputOverride;
                axisNames.Add(module.horizontalAxis); axisNames.Add(module.verticalAxis);
                buttonNames.Add(module.submitButton); buttonNames.Add(module.cancelButton);
                module.inputOverride = virtualFrame ? uiInput : modules[module];
            }
            if (virtualFrame) ApplyUiOverride();
        }
        private void RestoreUiFocus()
        {
            foreach (var pair in focusRestore)
                if (pair.Key != null) ModReflect.Set(pair.Key, "m_HasFocus", Application.isFocused == pair.Value.applicationFocus ? pair.Value.focus : Application.isFocused);
            focusRestore.Clear();
        }
        internal void RefreshBindings() { BindModules(); }
        private void ApplyUiOverride()
        {
            foreach (var pair in modules)
                if (pair.Key != null)
                {
                    pair.Key.inputOverride = virtualFrame ? uiInput : pair.Value;
                    var events = pair.Key.GetComponent<EventSystem>();
                    if (virtualFrame && events != null)
                    {
                        if (!focusRestore.ContainsKey(events)) focusRestore.Add(events, (events.isFocused, Application.isFocused));
                        if (!events.isFocused) ModReflect.Set(events, "m_HasFocus", true);
                    }
                }
            if (!virtualFrame) RestoreUiFocus();
        }

        public JArray DescribeActions()
        {
            var coordinate = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 };
            var pair = new JObject { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2, ["items"] = coordinate };
            var keyNames = new JArray(Enum.GetNames(typeof(KeyCode)));
            var axisProperties = new JObject();
            foreach (string name in axisNames) if (!string.IsNullOrEmpty(name)) axisProperties[name] = new JObject { ["type"] = "number" };
            return new JArray {
                new JObject { ["name"] = "input.gesture", ["description"] = "Queue absolute key/button/axis states for exact Update-frame counts. Pointer uses normalized screen bottom-left origin and persists if omitted; it does not imply mouse-axis deltas or sensitivity. Axes are explicit frame values, omitted axes neutral zero. named_buttons use active UI submit/cancel bindings. Scroll is first-frame only. Final release frame automatic. Unity UI text/IME requires ui.set.",
                    ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("steps"), ["properties"] = new JObject {
                        ["steps"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["items"] = new JObject {
                            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("frames"), ["properties"] = new JObject {
                                ["frames"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                                ["pointer"] = pair,
                                ["axes"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = axisProperties },
                                ["named_buttons"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                                ["keys"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string", ["enum"] = keyNames } },
                                ["buttons"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 6 } },
                                ["scroll"] = new JObject { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2, ["items"] = new JObject { ["type"] = "number" } }
                            }
                        } }
                    } } },
                new JObject { ["name"] = "input.cancel", ["description"] = "Queue an outside-viewport release frame and reset normal game input handlers before restoring native input.", ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false } }
            };
        }

        public JObject Act(string action, JObject args)
        {
            if (!initialized) throw new BridgeException("input_unavailable", "Virtual input is stopped.");
            if (action == "input.cancel")
            {
                if (IsBusy) { cancelRequested = true; status = "cancel_queued"; }
                return new JObject { ["sequence"] = sequence, ["status"] = status };
            }
            if (action != "input.gesture") throw new BridgeException("unknown_action", action);
            if (IsBusy) throw new BridgeException("action_pending", "A virtual input gesture is pending.");
            BindModules();
            ActionSchema.Validate(args, (JObject)DescribeActions()[0]["arguments"]);
            JArray list = (JArray)args["steps"];
            var pending = new Step[list.Count];
            int total = 0;
            for (int i = 0; i < pending.Length; i++)
            {
                JObject spec = (JObject)list[i];
                var step = new Step { Frames = (int)spec["frames"], Keys = new HashSet<KeyCode>() };
                total += step.Frames;
                if (spec["pointer"] is JArray pos) step.Pointer = new Vector2((float)pos[0], (float)pos[1]);
                if (spec["scroll"] is JArray delta) step.Scroll = new Vector2((float)delta[0], (float)delta[1]);
                if (spec["keys"] is JArray keys) foreach (JToken key in keys) { KeyCode code = (KeyCode)Enum.Parse(typeof(KeyCode), (string)key); if (code != KeyCode.None) step.Keys.Add(code); }
                if (spec["buttons"] is JArray buttons) foreach (JToken button in buttons) step.Keys.Add(KeyCode.Mouse0 + (int)button);
                step.Axes = new Dictionary<string, float>(StringComparer.Ordinal);
                if (spec["axes"] is JObject values) foreach (JProperty value in values.Properties()) step.Axes.Add(value.Name, (float)value.Value);
                step.NamedButtons = new HashSet<string>(StringComparer.Ordinal);
                if (spec["named_buttons"] is JArray names) foreach (JToken name in names) { if (!buttonNames.Contains((string)name)) throw new BridgeException("invalid_arguments", "Unknown UI button binding: " + name); step.NamedButtons.Add((string)name); }
                pending[i] = step;
            }
            ClampPointer();
            steps = pending; stepIndex = -1; remaining = 0; elapsed = 0;
            sequence++; status = "queued";
            return new JObject { ["sequence"] = sequence, ["status"] = status, ["requested_frames"] = total, ["release_frames"] = 1 };
        }

        private void ClampPointer() { pointer.x = Mathf.Clamp(pointer.x, 0, Mathf.Max(0, Screen.width - 1)); pointer.y = Mathf.Clamp(pointer.y, 0, Mathf.Max(0, Screen.height - 1)); pointer.z = 0; }
        private void LateUpdate()
        {
            if (!initialized || !IsBusy) return;
            if (releaseFrame)
            {
                virtualFrame = false; releaseFrame = false; cancelRequested = false; steps = null;
                held.Clear(); previous.Clear(); scroll = Vector2.zero;
                axes.Clear(); namedButtons.Clear(); previousNamedButtons.Clear();
                status = status == "releasing_cancel" ? "cancelled" : "completed";
                ApplyUiOverride();
                return;
            }
            if (virtualFrame) { elapsed++; remaining--; }
            previous.Clear(); previous.UnionWith(held); scroll = Vector2.zero;
            previousNamedButtons.Clear(); previousNamedButtons.UnionWith(namedButtons);
            if (cancelRequested || (virtualFrame && remaining == 0 && stepIndex + 1 == steps.Length))
            {
                held.Clear(); virtualFrame = true; releaseFrame = true;
                axes.Clear(); namedButtons.Clear();
                if (cancelRequested) { pointer = new Vector3(-100, -100, 0); ResetGameHandlers(); }
                status = cancelRequested ? "releasing_cancel" : "releasing";
                ApplyUiOverride();
                return;
            }
            if (!virtualFrame || remaining == 0)
            {
                Step step = steps[++stepIndex]; remaining = step.Frames;
                held.Clear(); held.UnionWith(step.Keys);
                axes = step.Axes; namedButtons = step.NamedButtons;
                if (step.Pointer.HasValue) pointer = new Vector3(step.Pointer.Value.x * (Screen.width - 1), step.Pointer.Value.y * (Screen.height - 1), 0);
                scroll = step.Scroll;
            }
            ClampPointer(); virtualFrame = true; status = "running";
            ApplyUiOverride();
        }

        private void ResetGameHandlers()
        {
            if (InputActionManager.current != null) InputActionManager.current.ResetExcept(null);
        }
        public void Stop()
        {
            if (!initialized) return;
            try
            {
                // Shutdown cannot await a release frame; use the normal reset/cancellation paths.
                if (IsBusy)
                {
                    try { ResetGameHandlers(); }
                    finally
                    {
                        foreach (var pair in modules) if (pair.Key != null) { ReleaseUiPointers(pair.Key); pair.Key.DeactivateModule(); }
                        if (UIDragHandler.isDraggingAnyUI && EventSystem.current != null)
                        {
                            var eventData = new PointerEventData(EventSystem.current) { position = new Vector2(-100, -100), button = PointerEventData.InputButton.Left };
                            ModReflect.GetStatic<UIDragHandler>(typeof(UIDragHandler), "currentDragger")?.OnEndDrag(eventData);
                        }
                    }
                }
            }
            finally
            {
                SceneManager.sceneLoaded -= SceneLoaded;
                foreach (var pair in modules) if (pair.Key != null && pair.Key.inputOverride == uiInput) pair.Key.inputOverride = pair.Value;
                modules.Clear();
                RestoreUiFocus();
                if (changedBackground && Application.runInBackground) Application.runInBackground = originalBackground;
                try { harmony?.UnpatchAll(Owner); }
                finally
                {
                    held.Clear(); previous.Clear(); steps = null; virtualFrame = false; releaseFrame = false; cancelRequested = false;
                    axes.Clear(); namedButtons.Clear(); previousNamedButtons.Clear(); scroll = Vector2.zero;
                    status = "stopped"; initialized = false; if (current == this) current = null;
                    if (uiInput != null) UnityEngine.Object.Destroy(uiInput);
                }
            }
        }
        private static void ReleaseUiPointers(StandaloneInputModule module)
        {
            // Read only the UI module's existing pointer records; dispatch normal public cancellation events.
            if (!(ModReflect.Get(module, "m_PointerData") is Dictionary<int, PointerEventData> records)) return;
            foreach (PointerEventData data in records.Values)
            {
                data.position = new Vector2(-100, -100); data.eligibleForClick = false;
                foreach (var hovered in data.hovered) if (hovered != null) ExecuteEvents.Execute(hovered, data, ExecuteEvents.pointerExitHandler);
                data.hovered.Clear(); data.pointerEnter = null;
                if (data.pointerPress != null) ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerExitHandler);
                if (data.pointerPress != null) ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                if (data.dragging && data.pointerDrag != null) ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.endDragHandler);
                data.pointerPress = null; data.rawPointerPress = null; data.pointerDrag = null; data.dragging = false;
            }
        }
        private void OnDestroy() { Stop(); }
        private void OnApplicationQuit() { Stop(); }

        public JObject Observe()
        {
            var keys = new JArray(); var buttons = new JArray();
            foreach (KeyCode key in held) { if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6) buttons.Add((int)key - (int)KeyCode.Mouse0); else keys.Add(key.ToString()); }
            return new JObject {
                ["sequence"] = sequence, ["status"] = status, ["busy"] = IsBusy, ["virtual_frame"] = virtualFrame,
                ["step_index"] = stepIndex, ["step_frames_remaining"] = remaining, ["elapsed_frames"] = elapsed,
                ["pointer_pixels"] = new JArray(pointer.x, pointer.y), ["pointer_normalized"] = new JArray(pointer.x / Mathf.Max(1, Screen.width - 1), pointer.y / Mathf.Max(1, Screen.height - 1)),
                ["keys"] = keys, ["buttons"] = buttons, ["scroll"] = new JArray(scroll.x, scroll.y),
                ["axes"] = JObject.FromObject(axes), ["named_buttons"] = new JArray(namedButtons),
                ["application_focused"] = Application.isFocused, ["event_system_focused"] = EventSystem.current != null && EventSystem.current.isFocused, ["virtual_ui_focus"] = focusRestore.Count > 0,
                ["coverage"] = new JObject { ["assembly_limit"] = "Assembly-CSharp methods listed in InputReaders.g.cs (regenerate with tools/InputReaders after game updates); Unity UI uses BaseInput override.",
                    ["listed_count"] = InputReaders.Targets.Length, ["patched_count"] = patched.Count, ["patched_methods"] = patched.DeepClone(),
                    ["skipped_methods"] = new JArray(InputReaders.Skipped), ["unsupported_reads"] = new JArray(InputReaders.UnsupportedReads), ["failures"] = failures.DeepClone(),
                    ["ui_module_count"] = modules.Count, ["ui_text_limit"] = "BaseInput cannot synthesize Unity Event keyboard text, IME, or inputString. Use ui.set.",
                    ["supported_axis_names"] = new JArray(axisNames), ["ui_button_binding_names"] = new JArray(buttonNames),
                    ["excluded_types"] = "Mod loader, trust, Steam and Workshop types and [ModRestricted] members are never listed." }
            };
        }

        private static bool Active => current != null && current.virtualFrame;
        private static bool Key(KeyCode key, int edge)
        {
            bool now = current.held.Contains(key), before = current.previous.Contains(key);
            return edge == 0 ? now : edge == 1 ? now && !before : before && !now;
        }
        public static bool GetKey(KeyCode key) => Active ? Key(key, 0) : Input.GetKey(key);
        public static bool GetKeyDown(KeyCode key) => Active ? Key(key, 1) : Input.GetKeyDown(key);
        public static bool GetKeyUp(KeyCode key) => Active ? Key(key, 2) : Input.GetKeyUp(key);
        private static KeyCode ParseKey(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Unity key name is required.");
            if (name.Length == 1)
            {
                var character = (KeyCode)char.ToLowerInvariant(name[0]);
                if (Enum.IsDefined(typeof(KeyCode), character)) return character;
            }
            string normalized = name.Replace(" ", "");
            if (normalized.StartsWith("leftctrl", StringComparison.OrdinalIgnoreCase)) normalized = "LeftControl";
            else if (normalized.StartsWith("rightctrl", StringComparison.OrdinalIgnoreCase)) normalized = "RightControl";
            else if (normalized.Equals("up", StringComparison.OrdinalIgnoreCase)) normalized = "UpArrow";
            else if (normalized.Equals("down", StringComparison.OrdinalIgnoreCase)) normalized = "DownArrow";
            else if (normalized.Equals("left", StringComparison.OrdinalIgnoreCase)) normalized = "LeftArrow";
            else if (normalized.Equals("right", StringComparison.OrdinalIgnoreCase)) normalized = "RightArrow";
            if (normalized.Length == 3 && normalized[0] == '[' && normalized[2] == ']' && char.IsDigit(normalized[1])) normalized = "Keypad" + normalized[1];
            if (Enum.TryParse(normalized, true, out KeyCode key) && Enum.IsDefined(typeof(KeyCode), key)) return key;
            throw new ArgumentException("Unsupported Unity key name: " + name);
        }
        public static bool GetKey(string key) => Active ? Key(ParseKey(key), 0) : Input.GetKey(key);
        public static bool GetKeyDown(string key) => Active ? Key(ParseKey(key), 1) : Input.GetKeyDown(key);
        public static bool GetKeyUp(string key) => Active ? Key(ParseKey(key), 2) : Input.GetKeyUp(key);
        public static bool GetMouseButton(int button) => Active ? button >= 0 && button <= 6 && Key(KeyCode.Mouse0 + button, 0) : Input.GetMouseButton(button);
        public static bool GetMouseButtonDown(int button) => Active ? button >= 0 && button <= 6 && Key(KeyCode.Mouse0 + button, 1) : Input.GetMouseButtonDown(button);
        public static bool GetMouseButtonUp(int button) => Active ? button >= 0 && button <= 6 && Key(KeyCode.Mouse0 + button, 2) : Input.GetMouseButtonUp(button);
        public static Vector3 MousePosition() => Active ? current.pointer : Input.mousePosition;
        public static Vector2 MouseScrollDelta() => Active ? current.scroll : Input.mouseScrollDelta;
        public static bool AnyKey() => Active ? current.held.Count != 0 : Input.anyKey;
        public static bool AnyKeyDown() { if (!Active) return Input.anyKeyDown; foreach (KeyCode key in current.held) if (!current.previous.Contains(key)) return true; return false; }
        private static float Axis(string name) => name != null && current.axes.TryGetValue(name, out float value) ? value : 0;
        public static float GetAxis(string name) => Active ? Axis(name) : Input.GetAxis(name);
        public static float GetAxisRaw(string name) => Active ? Axis(name) : Input.GetAxisRaw(name);
        public static bool GetButton(string name) => Active ? current.namedButtons.Contains(name) : Input.GetButton(name);
        public static bool GetButtonDown(string name) => Active ? current.namedButtons.Contains(name) && !current.previousNamedButtons.Contains(name) : Input.GetButtonDown(name);
        public static bool GetButtonUp(string name) => Active ? !current.namedButtons.Contains(name) && current.previousNamedButtons.Contains(name) : Input.GetButtonUp(name);

        // Input members and their virtual answers, named directly so no reflection is needed to find them.
        private static Dictionary<MethodInfo, MethodInfo> CreateReplacements()
        {
            var map = new Dictionary<MethodInfo, MethodInfo>();
            void Map(Delegate original, Delegate wrapper) => map[original.Method] = wrapper.Method;
            Map((Func<KeyCode, bool>)Input.GetKey, (Func<KeyCode, bool>)GetKey);
            Map((Func<string, bool>)Input.GetKey, (Func<string, bool>)GetKey);
            Map((Func<KeyCode, bool>)Input.GetKeyDown, (Func<KeyCode, bool>)GetKeyDown);
            Map((Func<string, bool>)Input.GetKeyDown, (Func<string, bool>)GetKeyDown);
            Map((Func<KeyCode, bool>)Input.GetKeyUp, (Func<KeyCode, bool>)GetKeyUp);
            Map((Func<string, bool>)Input.GetKeyUp, (Func<string, bool>)GetKeyUp);
            Map((Func<int, bool>)Input.GetMouseButton, (Func<int, bool>)GetMouseButton);
            Map((Func<int, bool>)Input.GetMouseButtonDown, (Func<int, bool>)GetMouseButtonDown);
            Map((Func<int, bool>)Input.GetMouseButtonUp, (Func<int, bool>)GetMouseButtonUp);
            Map((Func<string, float>)Input.GetAxis, (Func<string, float>)GetAxis);
            Map((Func<string, float>)Input.GetAxisRaw, (Func<string, float>)GetAxisRaw);
            Map((Func<string, bool>)Input.GetButton, (Func<string, bool>)GetButton);
            Map((Func<string, bool>)Input.GetButtonDown, (Func<string, bool>)GetButtonDown);
            Map((Func<string, bool>)Input.GetButtonUp, (Func<string, bool>)GetButtonUp);
            map[typeof(Input).GetProperty("mousePosition").GetGetMethod()] = ((Func<Vector3>)MousePosition).Method;
            map[typeof(Input).GetProperty("mouseScrollDelta").GetGetMethod()] = ((Func<Vector2>)MouseScrollDelta).Method;
            map[typeof(Input).GetProperty("anyKey").GetGetMethod()] = ((Func<bool>)AnyKey).Method;
            map[typeof(Input).GetProperty("anyKeyDown").GetGetMethod()] = ((Func<bool>)AnyKeyDown).Method;
            return map;
        }
        // Patch the game methods listed by tools/InputReaders (mod/InputReaders.g.cs); ModReflect resolves them by name.
        private void PatchGame()
        {
            var transpiler = new HarmonyMethod(((Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>)Transpile).Method);
            foreach (var target in InputReaders.Targets)
            {
                string id = target.Type + "." + target.Method;
                try
                {
                    harmony.Patch(ModReflect.Target(ModReflect.FindType(target.Type), target.Method, target.Signature()), transpiler: transpiler);
                    patched.Add(id);
                }
                catch (Exception error) { failures.Add(id + ": " + error.Message); log?.Error("Input patch failure: " + id + ": " + error.Message); }
            }
        }
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method && replacements.TryGetValue(method, out MethodInfo wrapper)) instruction.operand = wrapper;
                yield return instruction;
            }
        }

        public sealed class VirtualBaseInput : BaseInput
        {
            public override bool mousePresent => Active || base.mousePresent;
            public override Vector2 mousePosition => Active ? (Vector2)MousePosition() : base.mousePosition;
            public override Vector2 mouseScrollDelta => Active ? MouseScrollDelta() : base.mouseScrollDelta;
            public override bool GetMouseButton(int button) => Active ? VirtualInput.GetMouseButton(button) : base.GetMouseButton(button);
            public override bool GetMouseButtonDown(int button) => Active ? VirtualInput.GetMouseButtonDown(button) : base.GetMouseButtonDown(button);
            public override bool GetMouseButtonUp(int button) => Active ? VirtualInput.GetMouseButtonUp(button) : base.GetMouseButtonUp(button);
            public override float GetAxisRaw(string name) => Active ? VirtualInput.GetAxisRaw(name) : base.GetAxisRaw(name);
            public override bool GetButtonDown(string name) => Active ? VirtualInput.GetButtonDown(name) : base.GetButtonDown(name);
            public override bool touchSupported => !Active && base.touchSupported;
            public override int touchCount => Active ? 0 : base.touchCount;
            public override string compositionString => Active ? "" : base.compositionString;
        }
    }
}

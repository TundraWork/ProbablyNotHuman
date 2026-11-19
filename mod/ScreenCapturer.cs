using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyNotHuman
{
    // Captures what is on screen, so image-only documents and scenes can be read like a player reads them.
    // Reads the back buffer at end of frame (not ScreenCapture, which counts as file access) and writes PNGs
    // through ModHelper's mod-data helpers. game.flow.captures_pending is 0 once every capture is written.
    public sealed class ScreenCapturer : MonoBehaviour
    {
        internal static int Pending;
        private readonly Queue<(string file, Rect area)> queue = new Queue<(string, Rect)>();

        public JArray DescribeActions() => new JArray(new JObject {
            ["name"] = "ui.capture",
            ["description"] = "Capture the screen, or the rectangle of the UI element at path, to ModData/probably_not_human/<file> as PNG. Wait for flow.captures_pending = 0 before reading it.",
            ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JObject {
                ["path"] = new JObject { ["type"] = "string", ["description"] = "UI path from an observation (ui.texts/ui.controls/ui.visuals); omit for the whole screen." } } } });

        public JObject Act(JObject args)
        {
            Rect area = new Rect(0, 0, Screen.width, Screen.height);
            if (args["path"]?.Type == JTokenType.String)
            {
                var target = UIAdapter.FindByPath((string)args["path"]) ?? throw new BridgeException("unknown_target", "No active UI element at that path.");
                var geometry = ViewGeometry.Read(target) ?? throw new BridgeException("unknown_target", "The element has no screen rectangle.");
                float minX = 1, minY = 1, maxX = 0, maxY = 0;
                foreach (JArray point in (JArray)geometry["quad"])
                {
                    minX = Mathf.Min(minX, (float)point[0]); maxX = Mathf.Max(maxX, (float)point[0]);
                    minY = Mathf.Min(minY, (float)point[1]); maxY = Mathf.Max(maxY, (float)point[1]);
                }
                minX = Mathf.Clamp01(minX); minY = Mathf.Clamp01(minY); maxX = Mathf.Clamp01(maxX); maxY = Mathf.Clamp01(maxY);
                area = Rect.MinMaxRect(minX * (Screen.width - 1), minY * (Screen.height - 1), maxX * (Screen.width - 1), maxY * (Screen.height - 1));
                if (area.width < 1 || area.height < 1) throw new BridgeException("unknown_target", "The element is off screen.");
            }
            string file = "captures/" + Guid.NewGuid().ToString("N") + ".png";
            queue.Enqueue((file, area));
            Pending++;
            if (queue.Count == 1) StartCoroutine(Flush());
            return new JObject { ["file"] = file, ["width"] = (int)area.width, ["height"] = (int)area.height };
        }

        private IEnumerator Flush()
        {
            while (queue.Count > 0)
            {
                yield return new WaitForEndOfFrame();
                var (file, area) = queue.Dequeue();
                var texture = new Texture2D((int)area.width, (int)area.height, TextureFormat.RGB24, false);
                try
                {
                    texture.ReadPixels(area, 0, 0);
                    texture.Apply();
                    ModHelper.WriteModDataFileBytes(FileMailbox.ModId, file, texture.EncodeToPNG());
                }
                finally { Destroy(texture); Pending--; }
            }
        }
    }
}

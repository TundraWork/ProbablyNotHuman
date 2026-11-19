using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyNotHuman
{
    internal static class ViewGeometry
    {
        // Every caller runs on Unity's main thread; reuse the native corner buffer.
        private static readonly Vector3[] corners = new Vector3[4];
        internal static Camera CameraFor(Canvas canvas)
        {
            var root = canvas.rootCanvas;
            return root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera ?? Camera.main;
        }
        private static JArray Normalize(Vector2 point) => new JArray(point.x / Mathf.Max(1, Screen.width - 1), point.y / Mathf.Max(1, Screen.height - 1));
        internal static JObject Read(Component component)
        {
            if (component == null) return null;
            var quad = new JArray();
            Vector2 center;
            if (component.transform is RectTransform rect)
            {
                var canvas = component.GetComponentInParent<Canvas>();
                if (canvas == null) return null;
                var camera = CameraFor(canvas);
                rect.GetWorldCorners(corners);
                center = RectTransformUtility.WorldToScreenPoint(camera, (corners[0] + corners[2]) * 0.5f);
                for (int i = 0; i < 4; i++) quad.Add(Normalize(RectTransformUtility.WorldToScreenPoint(camera, corners[i])));
            }
            else
            {
                var renderer = component as SpriteRenderer ?? component.GetComponentInChildren<SpriteRenderer>();
                var camera = Camera.main;
                if (camera == null) return null;
                var collider = component.GetComponent<Collider>();
                var collider2D = component.GetComponent<Collider2D>();
                Bounds bounds;
                if (!(component is SpriteRenderer) && collider != null && collider.enabled) bounds = collider.bounds;
                else if (!(component is SpriteRenderer) && collider2D != null && collider2D.enabled) bounds = collider2D.bounds;
                else if (renderer != null) bounds = renderer.bounds;
                else return null;
                float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
                for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                {
                    var point = camera.WorldToScreenPoint(new Vector3(x == 0 ? bounds.min.x : bounds.max.x, y == 0 ? bounds.min.y : bounds.max.y, z == 0 ? bounds.min.z : bounds.max.z));
                    minX = Mathf.Min(minX, point.x); maxX = Mathf.Max(maxX, point.x);
                    minY = Mathf.Min(minY, point.y); maxY = Mathf.Max(maxY, point.y);
                }
                center = camera.WorldToScreenPoint(bounds.center);
                quad.Add(Normalize(new Vector2(minX, minY))); quad.Add(Normalize(new Vector2(minX, maxY)));
                quad.Add(Normalize(new Vector2(maxX, maxY))); quad.Add(Normalize(new Vector2(maxX, minY)));
            }
            return new JObject { ["center"] = Normalize(center), ["quad"] = quad, ["coordinates"] = "Normalized viewport, bottom-left origin; quad order bottom-left/top-left/top-right/bottom-right. Values may extend off-screen." };
        }
        internal static bool OnScreen(JObject geometry)
        {
            if (geometry == null || !(geometry["quad"] is JArray quad)) return false;
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            foreach (JArray point in quad)
            {
                minX = Mathf.Min(minX, (float)point[0]); maxX = Mathf.Max(maxX, (float)point[0]);
                minY = Mathf.Min(minY, (float)point[1]); maxY = Mathf.Max(maxY, (float)point[1]);
            }
            return maxX >= 0 && maxY >= 0 && minX <= 1 && minY <= 1;
        }
    }
}

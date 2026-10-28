using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace VeteranLevels
{
    /// <summary>IMGUI overlay: names above veteran units and level-up announcements.</summary>
    internal static class VeterancyOverlay
    {
        private const float ToastSeconds = 6f;
        private const int MaxToasts = 5;
        private const float LabelHeightAboveUnit = 2.2f;
        private const float XpBarWidth = 60f;
        private const float XpBarHeight = 4f;

        private static readonly List<(string text, float until)> Toasts = new List<(string, float)>();
        private static GUIStyle _labelStyle;
        private static GUIStyle _toastStyle;

        public static void Announce(string text)
        {
            Plugin.Log.LogInfo(text);
            Toasts.Add((text, Time.unscaledTime + ToastSeconds));
            if (Toasts.Count > MaxToasts)
                Toasts.RemoveAt(0);
        }

        public static void Draw(World world)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            InitStyles();
            DrawToasts();

            if (!Plugin.ShowLabels.Value || world == null || !world.IsCreated)
                return;
            var system = world.GetExistingSystem<VeterancySystem>();
            var camera = Camera.main != null ? Camera.main : (Camera.allCamerasCount > 0 ? Camera.allCameras[0] : null);
            if (system == null || camera == null)
                return;

            foreach (var veteran in system.Veterans.Values)
            {
                if (!veteran.IsShown)
                    continue;
                float labelHeight = LabelHeightAboveUnit * veteran.SizeMultiplier;
                Vector3 world3 = new Vector3(veteran.Position.x, veteran.Position.y + labelHeight, veteran.Position.z);
                Vector3 screen = camera.WorldToScreenPoint(world3);
                if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
                    continue;

                float top = Screen.height - screen.y - 10f;
                if (veteran.Level > 0)
                {
                    string text = $"{veteran.DisplayName}  Lv{veteran.Level}";
                    DrawShadowed(new Rect(screen.x - 100f, top, 200f, 20f), text, _labelStyle, LevelColor(veteran.Level));
                }
                DrawXpBar(screen.x, top + 19f, veteran);
            }
        }

        private static void DrawXpBar(float centerX, float top, Veteran veteran)
        {
            var outline = new Rect(centerX - XpBarWidth / 2f - 1f, top - 1f, XpBarWidth + 2f, XpBarHeight + 2f);
            var fill = new Rect(centerX - XpBarWidth / 2f, top, XpBarWidth * veteran.XpProgress, XpBarHeight);

            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(outline, Texture2D.whiteTexture);
            GUI.color = veteran.IsMaxLevel ? new Color(1f, 0.8f, 0.2f) : new Color(0.3f, 0.75f, 1f);
            GUI.DrawTexture(fill, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static void DrawToasts()
        {
            Toasts.RemoveAll(t => t.until < Time.unscaledTime);
            float y = 90f;
            foreach (var toast in Toasts)
            {
                DrawShadowed(new Rect(0f, y, Screen.width, 26f), toast.text, _toastStyle, new Color(1f, 0.85f, 0.3f));
                y += 26f;
            }
        }

        private static void DrawShadowed(Rect rect, string text, GUIStyle style, Color color)
        {
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
            style.normal.textColor = color;
            GUI.Label(rect, text, style);
        }

        private static Color LevelColor(int level)
        {
            if (level >= 8) return new Color(1f, 0.55f, 0.1f);   // legendary orange
            if (level >= 5) return new Color(0.75f, 0.45f, 1f);  // epic purple
            if (level >= 3) return new Color(0.35f, 0.7f, 1f);   // rare blue
            return new Color(0.6f, 1f, 0.6f);                    // green
        }

        private static void InitStyles()
        {
            if (_labelStyle != null)
                return;
            _labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 13, fontStyle = FontStyle.Bold };
            _toastStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
        }
    }
}

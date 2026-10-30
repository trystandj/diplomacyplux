using System.Collections.Generic;
using Systems.GameStateSystems;
using Unity.Entities;
using UnityEngine;

namespace VeteranLevels
{
    /// <summary>IMGUI overlay: names above veteran units and enemy heroes, announcements, and the hero panel.</summary>
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
        private static GUIStyle _panelStyle;

        /// <summary>Toggled by the hero panel key.</summary>
        public static bool HeroPanelOpen;

        public static void Announce(string text)
        {
            Plugin.Log.LogInfo(text);
            Toasts.Add((text, Time.unscaledTime + ToastSeconds));
            if (Toasts.Count > MaxToasts)
                Toasts.RemoveAt(0);
        }

        public static void Draw(World world)
        {
            if (IsGameUiCovered())
                return;
            InitStyles();
            var system = world != null && world.IsCreated ? world.GetExistingSystem<VeterancySystem>() : null;

            // The panel has a button, so it handles every IMGUI event; the rest only draws.
            if (HeroPanelOpen && system != null)
                DrawHeroPanel(system);
            if (Event.current.type != EventType.Repaint)
                return;
            DrawToasts();

            if (!Plugin.ShowLabels.Value)
                return;
            var camera = Camera.main != null ? Camera.main : (Camera.allCamerasCount > 0 ? Camera.allCameras[0] : null);
            if (system == null || camera == null)
                return;

            foreach (var veteran in system.Veterans.Values)
            {
                if (!veteran.IsShown || veteran.Hidden)
                    continue;
                float labelHeight = LabelHeightAboveUnit * veteran.SizeMultiplier;
                Vector3 world3 = new Vector3(veteran.Position.x, veteran.Position.y + labelHeight, veteran.Position.z);
                Vector3 screen = camera.WorldToScreenPoint(world3);
                if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
                    continue;

                float top = Screen.height - screen.y - 10f;
                if (veteran.IsHero)
                {
                    string tier = veteran.Tier > 0 ? $"  T{veteran.Tier}" : "";
                    string text = veteran.IsEnemy ? $"Enemy {veteran.DisplayName}  Lv{veteran.Level}" : $"{veteran.DisplayName}  Lv{veteran.Level}{tier}";
                    var color = veteran.IsEnemy ? new Color(1f, 0.3f, 0.25f) : LevelColor(veteran.Level);
                    DrawShadowed(new Rect(screen.x - 120f, top, 240f, 20f), text, _labelStyle, color);
                }
                DrawXpBar(screen.x, top + 19f, veteran);
            }
        }

        private static void DrawHeroPanel(VeterancySystem system)
        {
            var area = new Rect(20f, 160f, 320f, 178f);
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 8f, area.width - 20f, area.height - 16f));
            GUILayout.Label("Heroes", _panelStyle);
            GUILayout.Label($"{system.CountHeroes()} / {system.HeroCap} heroes    {system.SoulCrystals} soul crystals");

            var cost = system.NextUpgradeCost;
            if (cost.HasValue)
            {
                int nextCap = system.HeroCap + Plugin.HeroCapPerUpgrade.Value;
                GUI.enabled = system.SoulCrystals >= cost.Value;
                if (GUILayout.Button($"Raise cap to {nextCap} ({cost.Value} soul crystals)"))
                    VeterancySystem.BuyCapUpgradeRequested = true;
                GUI.enabled = true;
            }
            else
            {
                GUILayout.Label("Hero cap fully upgraded.");
            }

            GUILayout.Label("Research", _panelStyle);
            if (system.BeastTamingResearched)
            {
                GUILayout.Label("Beast Taming: the Barracks can train creatures.");
            }
            else
            {
                int researchCost = Plugin.BeastTamingCost.Value;
                GUI.enabled = system.SoulCrystals >= researchCost;
                if (GUILayout.Button($"Beast Taming: wolves & Heffalumps ({researchCost} soul crystals)"))
                    VeterancySystem.BuyBeastTamingRequested = true;
                GUI.enabled = true;
            }
            GUILayout.Label($"{Plugin.HeroPanelKey.Value}: close");
            GUILayout.EndArea();
        }

        /// <summary>True while the pause menu or another full-screen game panel is open.</summary>
        private static bool IsGameUiCovered()
        {
            var ui = GameState.UIState;
            return ui.mainMenuOpened || ui.winLosePanelOpened || ui.researchWindowOpened || ui.marketWindowOpened
                || ui.missionPanelOpened || ui.statisticPanelOpened || ui.cutsceneIsPlaying || ui.photoModActive
                || ui.confirmOpened;
        }

        private static void DrawXpBar(float centerX, float top, Veteran veteran)
        {
            var outline = new Rect(centerX - XpBarWidth / 2f - 1f, top - 1f, XpBarWidth + 2f, XpBarHeight + 2f);
            var fill = new Rect(centerX - XpBarWidth / 2f, top, XpBarWidth * veteran.XpProgress, XpBarHeight);

            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(outline, Texture2D.whiteTexture);
            if (veteran.IsMaxLevel)
                GUI.color = new Color(1f, 0.8f, 0.2f);
            else
                GUI.color = veteran.IsEnemy ? new Color(0.85f, 0.15f, 0.1f) : new Color(0.3f, 0.75f, 1f);
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
            _panelStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
        }
    }
}

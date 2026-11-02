using System;
using System.Collections.Generic;
using System.Linq;
using Audio;
using Components.Structs;
using HarmonyLib;
using ScriptableObjectDefinitions;
using Systems.GameStateSystems;
using Systems.InputSystems.PlayerActionGroups;
using TMPro;
using UI;
using UI.HelpersStorage;
using UI.Research;
using UI.Utility;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace VeteranLevels
{
    /// <summary>
    /// The mod's research tree: a uGUI window in the style of the game's research screen, opened
    /// with the hero panel key or the "Veterans" button added to the game's research screen.
    ///
    /// The window is built from pieces of the game's own research panel: the images of a research
    /// item and of the owner icons are copied, borders swap between the
    /// panel's locked/open/complete sprites, icons are borrowed from the mission's research pool,
    /// and hovering shows the game's research hint with the soul crystal price. If a mission has
    /// no research panel to copy from, plain fallback graphics are used instead.
    /// </summary>
    internal static class ResearchTreeWindow
    {
        private const float RefreshInterval = 0.25f;
        private const float FallbackDelay = 5f;
        private const float ColumnSpacing = 240f;
        /// <summary>Top of the column headers, below the title and soul crystal count.</summary>
        private const float HeaderAreaTop = 112f;
        private const float HeaderTitleHeight = 30f;
        private const float HeroCountHeight = 24f;
        private const float HeaderGap = 22f;
        private const float NodeCaptionHeight = 34f;
        private const float WindowWidth = 760f;
        private const float WindowMinHeight = 520f;
        private static readonly Color LineColor = new Color(0.62f, 0.5f, 0.32f, 0.9f);
        private static readonly Color TextColor = new Color(0.93f, 0.86f, 0.7f);
        private static readonly Color DoneTextColor = new Color(0.55f, 0.9f, 0.45f);

        private enum NodeState { Locked, Available, Unaffordable, Completed }

        private class Node
        {
            public GameObject Root;
            public Image Icon;
            public Image Border;
            public Button Button;
            public HintableElement Hint;
            public TMP_Text Caption;
            public Sprite LockSprite, MonoSprite, ColorSprite;
            public NodeState? State;
        }

        private static ResearchPanel _nativePanel;
        private static GameObject _window;
        private static Canvas _canvas;
        private static RectTransform _content;
        private static TMP_FontAsset _font;
        private static Material _fontMaterial;
        private static TMP_Text _crystalsText;
        private static TMP_Text _heroesText;
        private static readonly List<Node> CapNodes = new List<Node>();
        private static Node _beastNode;
        private static bool _open;
        private static bool _lastNativeOpen;
        private static float _nextRefresh;
        private static bool _buildLogged;
        private static float _fallbackAfter = -1f;

        public static bool IsOpen => _open && _canvas != null && _canvas.enabled;

        public static void Toggle()
        {
            if (_open)
            {
                Close();
                return;
            }
            if (_window == null)
            {
                VeterancyOverlay.Announce("Veteran research isn't available yet; start a mission first.");
                return;
            }
            // Swap the game's research screen for ours rather than stacking them.
            if (GameState.UIState.researchWindowOpened)
                UIInputHandler.PlayerActionContainer.Add(PlayerActionType.ResearchPanel);
            AudioManager.PlayUISound(UIAudioEvent.Click);
            _open = true;
            _nextRefresh = 0f;
        }

        public static void Close()
        {
            if (!_open)
                return;
            AudioManager.PlayUISound(UIAudioEvent.CloseClick);
            _open = false;
            HideHints();
        }

        /// <summary>Per frame, from the runner: builds the window when a mission starts and keeps it current.</summary>
        public static void Tick(VeterancySystem system)
        {
            if (system == null || !EnsureBuilt())
            {
                _open = false;
                if (_canvas != null)
                    _canvas.enabled = false;
                return;
            }

            var ui = GameState.UIState;
            // The game's research screen opening (closed -> open) replaces ours; other full-screen
            // panels and the pause menu close it.
            bool nativeOpened = ui.researchWindowOpened && !_lastNativeOpen;
            _lastNativeOpen = ui.researchWindowOpened;
            if (_open && (nativeOpened || ui.mainMenuOpened || ui.winLosePanelOpened || ui.marketWindowOpened
                || ui.missionPanelOpened || ui.statisticPanelOpened || ui.cutsceneIsPlaying || ui.photoModActive))
            {
                _open = false;
                HideHints();
            }

            if (_canvas.enabled != _open)
                _canvas.enabled = _open;
            if (!_open || Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            Refresh(system);
        }

        private static void Refresh(VeterancySystem system)
        {
            int crystals = system.SoulCrystals;
            _crystalsText.text = $"Soul crystals: {crystals}";
            _heroesText.text = $"{system.CountHeroes()} / {system.HeroCap} heroes";

            for (int i = 0; i < CapNodes.Count; i++)
            {
                int cost = Plugin.CapUpgradeCosts[i];
                int from = Plugin.BaseHeroCap.Value + Plugin.HeroCapPerUpgrade.Value * i;
                int to = from + Plugin.HeroCapPerUpgrade.Value;
                var state = system.CapUpgrades > i ? NodeState.Completed
                    : system.CapUpgrades < i ? NodeState.Locked
                    : crystals >= cost ? NodeState.Available : NodeState.Unaffordable;
                string requirement = state == NodeState.Locked ? $"\nRequires Hero Cap {Roman(i)}." : "";
                SetNode(CapNodes[i], state, $"Hero Cap {Roman(i + 1)}",
                    $"Raise the hero cap from {from} to {to}. Units only gain XP while there is a free hero slot.{requirement}", cost);
            }

            if (_beastNode != null)
            {
                int cost = Plugin.BeastTamingCost.Value;
                var state = system.BeastTamingResearched ? NodeState.Completed
                    : crystals >= cost ? NodeState.Available : NodeState.Unaffordable;
                SetNode(_beastNode, state, "Beast Taming",
                    "The Barracks can train wolves and Heffalumps. They fight for you and gain levels like other units.", cost);
            }
        }

        private static void SetNode(Node node, NodeState state, string title, string description, int cost)
        {
            node.Caption.text = title;
            node.Caption.color = state == NodeState.Completed ? DoneTextColor : TextColor;

            var hint = new HintData
            {
                type = state == NodeState.Completed ? HintType.MouseResearchShortcut : HintType.MouseResearchFull,
                Tittle = title,
                Description = state == NodeState.Completed ? description + "\nResearched." : description,
                price = new PriceBaseData { soulsPrice = cost, totalWorkAmountMultiplier = 1f, totalRepairWorkAmountMultiplier = 1f },
            };
            node.Hint.Init(hint);
            if (node.State == state)
                return;
            node.State = state;

            switch (state)
            {
                case NodeState.Completed:
                    node.Icon.sprite = node.ColorSprite;
                    node.Button.transition = Selectable.Transition.None;
                    break;
                case NodeState.Locked:
                    node.Icon.sprite = node.LockSprite;
                    node.Button.transition = Selectable.Transition.ColorTint;
                    break;
                default:
                    node.Icon.sprite = node.MonoSprite;
                    node.Button.transition = Selectable.Transition.ColorTint;
                    break;
            }
            node.Icon.color = state == NodeState.Unaffordable && _nativePanel != null ? _nativePanel.noEnoughResourcesColor
                : state == NodeState.Locked && _nativePanel == null ? new Color(0.45f, 0.45f, 0.45f) : Color.white;
            if (_nativePanel != null)
            {
                node.Border.sprite = state == NodeState.Completed ? _nativePanel.researchCompleteBorder
                    : state == NodeState.Locked ? _nativePanel.researchCloseBorder : _nativePanel.researchOpenBorder;
            }
            else
            {
                node.Border.color = state == NodeState.Completed ? new Color(0.45f, 0.75f, 0.3f)
                    : state == NodeState.Locked ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.85f, 0.68f, 0.35f);
            }
        }

        private static void OnCapNodeClicked(int index)
        {
            var node = CapNodes[index];
            Plugin.Log.LogInfo($"Research tree: Hero Cap {Roman(index + 1)} clicked ({node.State}).");
            if (node.State == NodeState.Available)
            {
                AudioManager.PlayUISound(UIAudioEvent.Click);
                VeterancySystem.BuyCapUpgradeRequested = true;
            }
            else
            {
                AudioManager.PlayUISound(UIAudioEvent.DiscardClick);
                ExplainUnavailable(node, Plugin.CapUpgradeCosts[index], $"Research Hero Cap {Roman(index)} first.");
            }
            _nextRefresh = 0f;
        }

        private static void OnBeastNodeClicked()
        {
            Plugin.Log.LogInfo($"Research tree: Beast Taming clicked ({_beastNode.State}).");
            if (_beastNode.State == NodeState.Available)
            {
                AudioManager.PlayUISound(UIAudioEvent.Click);
                VeterancySystem.BuyBeastTamingRequested = true;
            }
            else
            {
                AudioManager.PlayUISound(UIAudioEvent.DiscardClick);
                ExplainUnavailable(_beastNode, Plugin.BeastTamingCost.Value, "");
            }
            _nextRefresh = 0f;
        }

        private static void ExplainUnavailable(Node node, int cost, string lockedReason)
        {
            if (node.State == NodeState.Completed)
                VeterancyOverlay.Announce("Already researched.");
            else if (node.State == NodeState.Locked)
                VeterancyOverlay.Announce(lockedReason);
            else if (node.State == NodeState.Unaffordable)
                VeterancyOverlay.Announce($"Not enough soul crystals: this costs {cost}.");
        }

        private static void OpenNativeResearch()
        {
            Close();
            if (!GameState.UIState.researchWindowOpened)
                UIInputHandler.PlayerActionContainer.Add(PlayerActionType.ResearchPanel);
        }

        private static void HideHints()
        {
            foreach (var node in CapNodes.Append(_beastNode))
                if (node != null && node.Hint != null)
                    node.Hint.HintCleanup();
        }

        // ---------------------------------------------------------------- building

        /// <summary>True when the window exists for the current mission, building it if needed.</summary>
        private static bool EnsureBuilt()
        {
            if (_window != null)
                return true;
            if (!UI.New.UIViewManager.Exist)
                return false;

            _nativePanel = Object.FindObjectOfType<ResearchPanel>();
            // The game creates its research panel as the mission starts; give it a moment before
            // falling back to plain graphics.
            if (_nativePanel == null)
            {
                if (_fallbackAfter < 0f)
                    _fallbackAfter = Time.unscaledTime + FallbackDelay;
                if (Time.unscaledTime < _fallbackAfter)
                    return false;
            }
            _fallbackAfter = -1f;
            try
            {
                Build();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Building the veteran research window failed: {e}");
                if (_window != null)
                    Object.Destroy(_window);
                _window = null;
                return false;
            }
        }

        private static void Build()
        {
            CapNodes.Clear();
            _beastNode = null;
            _open = false;

            var nativeTexts = _nativePanel != null ? _nativePanel.GetComponentsInChildren<TMP_Text>(true) : new TMP_Text[0];
            var fontSource = nativeTexts.FirstOrDefault(t => t.font != null);
            _font = fontSource != null ? fontSource.font : TMP_Settings.defaultFontAsset;
            _fontMaterial = fontSource != null ? fontSource.fontSharedMaterial : null;

            // Sit next to the game's research panel so the window gets the same canvas scaling;
            // without one, make our own screen canvas.
            Transform parent = _nativePanel != null ? _nativePanel.transform.parent : null;
            _window = new GameObject("VeteranResearchWindow", typeof(RectTransform));
            if (parent != null)
            {
                _window.transform.SetParent(parent, false);
                _window.transform.SetAsLastSibling();
                Stretch((RectTransform)_window.transform);
                // No sorting override: drawn right after the game's panel, below its hints.
                _canvas = _window.AddComponent<Canvas>();
            }
            else
            {
                _canvas = _window.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = 100;
                var scaler = _window.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
            }
            _window.AddComponent<GraphicRaycaster>();
            _canvas.enabled = false;

            // Dimmed backdrop: also stops clicks reaching the map behind the window.
            var backdrop = NewImage("Backdrop", _window.transform);
            Stretch(backdrop.rectTransform);
            backdrop.color = new Color(0f, 0f, 0f, 0.55f);

            // Columns, laid out like the game's tree: owner icon on top, researches below it.
            var owners = _nativePanel != null ? _nativePanel.GetComponentsInChildren<ResearchOwner>(true) : new ResearchOwner[0];
            var heroOwner = owners.FirstOrDefault(o => (bool)AccessTools.Field(typeof(ResearchOwner), "isTownHall").GetValue(o)) ?? owners.FirstOrDefault();
            var beastOwner = owners.FirstOrDefault(o => ((string)AccessTools.Field(typeof(ResearchOwner), "ownerName").GetValue(o) ?? "").IndexOf("barrack", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? owners.Skip(1).FirstOrDefault() ?? heroOwner;

            int rows = Math.Max(1, Plugin.CapUpgradeCosts.Count);
            var nodeTemplate = FindItemTemplate();
            float nodeSize = nodeTemplate != null ? Math.Max(60f, GraphicsBounds((RectTransform)nodeTemplate.transform, ItemImages(nodeTemplate)).height) : 80f;
            float rowSpacing = nodeSize + NodeCaptionHeight + 30f;

            // Stack header icon, column title and hero count, then start the nodes below them,
            // using the real size of the owner icons so nothing overlaps.
            float headerSize = new[] { heroOwner, beastOwner }.Where(o => o != null)
                .Select(o => GraphicsBounds((RectTransform)o.transform, OwnerImages(o)).height).DefaultIfEmpty(0f).Max();
            float headerCenter = HeaderAreaTop + headerSize / 2f;
            float titleTop = HeaderAreaTop + headerSize + 6f;
            float countTop = titleTop + HeaderTitleHeight;
            float firstNodeTop = countTop + HeroCountHeight + HeaderGap + nodeSize / 2f;
            float height = Math.Max(WindowMinHeight, firstNodeTop + nodeSize / 2f + (rows - 1) * rowSpacing + NodeCaptionHeight + 70f);

            var frame = NewImage("Frame", _window.transform);
            _content = frame.rectTransform;
            _content.anchorMin = _content.anchorMax = new Vector2(0.5f, 0.5f);
            _content.sizeDelta = new Vector2(WindowWidth, height);
            if (Screen.width * 1f / Screen.height < 1.7f)
                _content.localScale = Vector3.one * 0.75f;
            CopyBackground(frame);

            AddText("Title", "Veteran Research", 34f, new Vector2(0f, -28f), new Vector2(WindowWidth, 44f), TextAlignmentOptions.Center);
            _crystalsText = AddText("Crystals", "", 22f, new Vector2(0f, -70f), new Vector2(WindowWidth, 30f), TextAlignmentOptions.Center);
            AddButton("Close", "X", new Vector2(WindowWidth / 2f - 34f, -24f), new Vector2(44f, 44f), Close);
            if (_nativePanel != null)
                AddButton("ResearchTree", "Research tree", new Vector2(-WindowWidth / 2f + 110f, -24f), new Vector2(190f, 40f), OpenNativeResearch);
            AddText("Footer", $"{Plugin.HeroPanelKey.Value}: open / close", 18f, new Vector2(0f, -height + 34f), new Vector2(WindowWidth, 26f), TextAlignmentOptions.Center);

            bool beasts = Plugin.TrainableCreatures.Value.Trim().Length > 0;
            float heroX = beasts ? -ColumnSpacing / 2f : 0f;
            float beastX = ColumnSpacing / 2f;

            AddHeader("HeroesHeader", heroOwner, heroX, headerCenter, titleTop, "Heroes");
            _heroesText = AddText("HeroCount", "", 18f, new Vector2(heroX, -countTop), new Vector2(220f, HeroCountHeight), TextAlignmentOptions.Center);
            var heroIcons = FindIcons(Plugin.HeroCapIcon.Value, "hero", "veteran", "health", "armor", "barrack", "soldier", "sword");
            for (int i = 0; i < Plugin.CapUpgradeCosts.Count; i++)
            {
                float top = firstNodeTop + i * rowSpacing;
                if (i > 0)
                    AddLine(heroX, top - rowSpacing + nodeSize / 2f + NodeCaptionHeight, top - nodeSize / 2f);
                int index = i;
                CapNodes.Add(AddNode($"HeroCap{i + 1}", nodeTemplate, nodeSize, heroX, top, heroIcons, () => OnCapNodeClicked(index)));
            }

            if (beasts)
            {
                AddHeader("BeastsHeader", beastOwner, beastX, headerCenter, titleTop, "Beasts");
                var beastIcons = FindIcons(Plugin.BeastTamingIcon.Value, "hunt", "wolf", "beast", "animal", "tame", "cavalry", "horse");
                _beastNode = AddNode("BeastTaming", nodeTemplate, nodeSize, beastX, firstNodeTop, beastIcons, OnBeastNodeClicked);
            }

            AddShortcutToNativePanel(heroOwner);
            if (!_buildLogged)
            {
                _buildLogged = true;
                Plugin.Log.LogInfo(_nativePanel != null
                    ? $"Veteran research window built from the game's research panel ({owners.Length} owners, item template: {(nodeTemplate != null ? nodeTemplate.name : "none")})."
                    : "Veteran research window built with fallback graphics (no research panel in this mission).");
                LogResearchPool();
            }
        }

        private static ResearchItemBehaviour FindItemTemplate() =>
            _nativePanel != null ? _nativePanel.GetComponentsInChildren<ResearchItemBehaviour>(true).FirstOrDefault(b => b.button != null && b.stateBorder != null) : null;

        private static Node AddNode(string name, ResearchItemBehaviour template, float size, float x, float top, (Sprite color, Sprite mono, Sprite locked) icons, Action onClick)
        {
            var node = new Node { ColorSprite = icons.color, MonoSprite = icons.mono, LockSprite = icons.locked };
            RectTransform rect;
            if (template != null)
            {
                var copies = CopyGraphics(name, _content, (RectTransform)template.transform, ItemImages(template));
                node.Root = copies[0].transform.parent.gameObject;
                node.Border = copies[0];
                node.Icon = copies[1];
                // The border frame is drawn over the icon; let clicks through to the button.
                node.Border.raycastTarget = false;
                // A plain Button: an ExtendedButton added from code has null click events and throws.
                node.Button = node.Icon.gameObject.AddComponent<Button>();
                node.Button.targetGraphic = node.Icon;
                rect = (RectTransform)node.Root.transform;
            }
            else
            {
                var border = NewImage(name, _content);
                border.color = new Color(0.3f, 0.3f, 0.3f);
                rect = border.rectTransform;
                rect.sizeDelta = new Vector2(size, size);
                var icon = NewImage("Icon", rect);
                icon.rectTransform.anchorMin = Vector2.zero;
                icon.rectTransform.anchorMax = Vector2.one;
                icon.rectTransform.offsetMin = new Vector2(5f, 5f);
                icon.rectTransform.offsetMax = new Vector2(-5f, -5f);
                node.Root = border.gameObject;
                node.Border = border;
                node.Icon = icon;
                node.Button = icon.gameObject.AddComponent<Button>();
                node.Button.targetGraphic = icon;
            }
            node.Root.name = name;
            node.Root.SetActive(true);
            PlaceTop(rect, x, top);

            node.Button.onClick.AddListener(() => onClick());
            // On the root, like the game's item: pointer events bubble up from the border and icon.
            node.Hint = node.Root.AddComponent<HintableElement>();
            node.Caption = AddText(name + "Caption", "", 17f, new Vector2(x, -top - size / 2f - 4f), new Vector2(ColumnSpacing - 20f, NodeCaptionHeight), TextAlignmentOptions.Top);
            return node;
        }

        private static void AddHeader(string name, ResearchOwner owner, float x, float iconCenter, float titleTop, string title)
        {
            if (owner != null)
            {
                var copies = CopyOwnerGraphics(name, _content, owner);
                foreach (var image in copies)
                    image.raycastTarget = false;
                PlaceTop((RectTransform)copies[0].transform.parent, x, iconCenter);
            }
            AddText(name + "Title", title, 24f, new Vector2(x, -titleTop), new Vector2(220f, HeaderTitleHeight), TextAlignmentOptions.Center);
        }

        /// <summary>Adds a "Veterans" button to the game's research screen that opens this window.</summary>
        private static void AddShortcutToNativePanel(ResearchOwner owner)
        {
            if (_nativePanel == null || _nativePanel.transform.childCount < 2)
                return;
            // Child 1 is the panel's content (the one the game rescales for the screen's aspect).
            var content = _nativePanel.transform.GetChild(1);
            var old = content.Find("VeteranResearchShortcut");
            if (old != null)
                Object.Destroy(old.gameObject);

            GameObject shortcut;
            if (owner != null)
            {
                var copies = CopyOwnerGraphics("Shortcut", content, owner);
                foreach (var image in copies)
                    image.raycastTarget = false;
                shortcut = copies[0].transform.parent.gameObject;
            }
            else
            {
                shortcut = new GameObject("Shortcut", typeof(RectTransform));
                shortcut.transform.SetParent(content, false);
                ((RectTransform)shortcut.transform).sizeDelta = new Vector2(70f, 70f);
            }
            shortcut.name = "VeteranResearchShortcut";
            var rect = (RectTransform)shortcut.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-40f, -110f);
            rect.localScale = Vector3.one * 0.8f;

            // An invisible hit area over the whole button.
            var target = shortcut.AddComponent<Image>();
            target.color = new Color(0f, 0f, 0f, 0f);
            var button = shortcut.AddComponent<Button>();
            button.targetGraphic = target;
            button.onClick.AddListener(Toggle);
            var hint = shortcut.AddComponent<HintableElement>();
            hint.Init(new HintData { type = HintType.Mouse, Tittle = "Veteran Research", Description = $"Hero cap and beast research ({Plugin.HeroPanelKey.Value})." });

            var label = NewText("Label", rect, "Veterans", 22f, TextAlignmentOptions.Top);
            label.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            label.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            label.rectTransform.pivot = new Vector2(0.5f, 1f);
            label.rectTransform.anchoredPosition = new Vector2(0f, -4f);
            label.rectTransform.sizeDelta = new Vector2(160f, 30f);
        }

        /// <summary>
        /// Copies just the given images of a piece of the game's research UI into a new object under
        /// <paramref name="parent"/>, keeping their sprites, sizes, offsets and draw order. Copying
        /// images instead of cloning the object matters: the game nests the next researches in the
        /// tree (and an owner's whole column) inside the item, so a clone would bring them along.
        /// Returns the copies in the order the images were given.
        /// </summary>
        private static Image[] CopyGraphics(string name, Transform parent, RectTransform source, params Image[] images)
        {
            // The new object is sized to the copied images alone: the source's own rect can be much
            // bigger (an owner's spans its whole column), and centring on it would offset the icon.
            var bounds = GraphicsBounds(source, images);
            var root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var rootRect = (RectTransform)root.transform;
            rootRect.sizeDelta = bounds.size;

            var drawOrder = source.GetComponentsInChildren<Image>(true).ToList();
            var copies = new Image[images.Length];
            foreach (int i in Enumerable.Range(0, images.Length).OrderBy(i => drawOrder.IndexOf(images[i])))
            {
                var original = images[i];
                var copy = NewImage(original != null ? original.name : "Image", rootRect);
                copies[i] = copy;
                copy.rectTransform.anchorMin = copy.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                if (original == null)
                {
                    copy.color = new Color(0f, 0f, 0f, 0f);
                    copy.rectTransform.sizeDelta = bounds.size;
                    continue;
                }
                copy.sprite = original.sprite;
                copy.type = original.type;
                copy.preserveAspect = original.preserveAspect;
                copy.pixelsPerUnitMultiplier = original.pixelsPerUnitMultiplier;
                copy.color = original.color;
                copy.material = original.material;
                var local = LocalRect(source, original.rectTransform);
                copy.rectTransform.anchoredPosition = local.center - bounds.center;
                copy.rectTransform.sizeDelta = local.size;
            }
            return copies;
        }

        /// <summary>The area the given images cover, in the source's local units.</summary>
        private static Rect GraphicsBounds(RectTransform source, Image[] images)
        {
            Rect? bounds = null;
            foreach (var image in images)
            {
                if (image == null)
                    continue;
                var local = LocalRect(source, image.rectTransform);
                bounds = bounds.HasValue
                    ? Rect.MinMaxRect(Math.Min(bounds.Value.xMin, local.xMin), Math.Min(bounds.Value.yMin, local.yMin),
                        Math.Max(bounds.Value.xMax, local.xMax), Math.Max(bounds.Value.yMax, local.yMax))
                    : local;
            }
            return bounds ?? new Rect(-40f, -40f, 80f, 80f);
        }

        private static Rect LocalRect(RectTransform source, RectTransform rect)
        {
            float scale = source.lossyScale.x > 0f ? rect.lossyScale.x / source.lossyScale.x : 1f;
            Vector2 center = source.InverseTransformPoint(rect.TransformPoint(rect.rect.center));
            var size = rect.rect.size * scale;
            return new Rect(center - size / 2f, size);
        }

        private static Image[] ItemImages(ResearchItemBehaviour item) => new[] { item.stateBorder, item.button.GetComponent<Image>() };

        /// <summary>Copies an owner's border and full-colour icon (see <see cref="CopyGraphics"/>).</summary>
        private static Image[] OwnerImages(ResearchOwner owner) => new[]
        {
            (Image)AccessTools.Field(typeof(ResearchOwner), "stateBorder").GetValue(owner),
            (Image)AccessTools.Field(typeof(ResearchOwner), "icon").GetValue(owner),
        };

        private static Image[] CopyOwnerGraphics(string name, Transform parent, ResearchOwner owner)
        {
            bool townHall = (bool)AccessTools.Field(typeof(ResearchOwner), "isTownHall").GetValue(owner);
            var copies = CopyGraphics(name, parent, (RectTransform)owner.transform, OwnerImages(owner));
            copies[0].sprite = townHall ? _nativePanel.townHallCompleteBorder : _nativePanel.ownerCompleteBorder;
            copies[1].sprite = (Sprite)AccessTools.Field(typeof(ResearchOwner), "colorIcon").GetValue(owner);
            return copies;
        }

        /// <summary>
        /// Icons borrowed from the mission's research pool: the research named in the config, else
        /// the first whose name contains a keyword, else the panel's default icon.
        /// </summary>
        private static (Sprite color, Sprite mono, Sprite locked) FindIcons(string configured, params string[] keywords)
        {
            var pool = ResearchPool();
            ResearchItem match = null;
            if (!string.IsNullOrWhiteSpace(configured))
                match = pool.FirstOrDefault(r => string.Equals(r.researchName, configured.Trim(), StringComparison.OrdinalIgnoreCase));
            foreach (var keyword in keywords)
            {
                if (match != null)
                    break;
                match = pool.FirstOrDefault(r => r.icon != null && (r.researchName ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (match != null && match.icon != null)
                return (match.icon, match.monoIcon ?? match.icon, match.lockIcon ?? match.monoIcon ?? match.icon);
            var fallback = _nativePanel != null ? _nativePanel.defaultResearchIcon : null;
            return (fallback, fallback, fallback);
        }

        private static ResearchItem[] ResearchPool()
        {
            try
            {
                return GameState.Settings.missionDifficultyConfiguration.researchConfig.researchPool.researches ?? new ResearchItem[0];
            }
            catch (Exception)
            {
                return new ResearchItem[0];
            }
        }

        private static void LogResearchPool()
        {
            var names = ResearchPool().Where(r => r.icon != null).Select(r => r.researchName).ToArray();
            if (names.Length > 0)
                Plugin.Log.LogInfo($"Research icons available for HeroCapIcon / BeastTamingIcon: {string.Join(", ", names)}");
        }

        /// <summary>Uses the largest sprite in the game's research panel as the window background.</summary>
        private static void CopyBackground(Image frame)
        {
            Image best = null;
            float bestArea = 0f;
            if (_nativePanel != null && _nativePanel.transform.childCount >= 2)
            {
                float screenArea = Screen.width * Screen.height;
                foreach (var image in _nativePanel.transform.GetChild(1).GetComponentsInChildren<Image>(true))
                {
                    if (image.sprite == null)
                        continue;
                    var corners = new Vector3[4];
                    image.rectTransform.GetWorldCorners(corners);
                    float area = Mathf.Abs((corners[2].x - corners[0].x) * (corners[2].y - corners[0].y));
                    // Skip full-screen dimmers; we want the window frame.
                    if (area > bestArea && area < screenArea * 0.98f)
                    {
                        best = image;
                        bestArea = area;
                    }
                }
            }
            if (best != null)
            {
                frame.sprite = best.sprite;
                frame.type = best.type;
                frame.pixelsPerUnitMultiplier = best.pixelsPerUnitMultiplier;
                frame.color = best.color;
            }
            else
            {
                frame.color = new Color(0.16f, 0.12f, 0.09f, 0.97f);
            }
        }

        // ---------------------------------------------------------------- small helpers

        private static void AddLine(float x, float fromTop, float toTop)
        {
            var line = NewImage("Line", _content);
            line.color = LineColor;
            line.raycastTarget = false;
            line.rectTransform.anchorMin = line.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            line.rectTransform.pivot = new Vector2(0.5f, 1f);
            line.rectTransform.anchoredPosition = new Vector2(x, -fromTop);
            line.rectTransform.sizeDelta = new Vector2(4f, Math.Max(0f, toTop - fromTop));
            line.transform.SetAsFirstSibling();
        }

        private static void AddButton(string name, string text, Vector2 position, Vector2 size, Action onClick)
        {
            var image = NewImage(name, _content);
            image.color = new Color(0.32f, 0.24f, 0.15f, 0.95f);
            image.rectTransform.anchorMin = image.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            image.rectTransform.pivot = new Vector2(0.5f, 1f);
            image.rectTransform.anchoredPosition = position;
            image.rectTransform.sizeDelta = size;
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(1.3f, 1.2f, 1f);
            button.colors = colors;
            button.onClick.AddListener(() => onClick());
            var label = NewText("Label", image.rectTransform, text, 20f, TextAlignmentOptions.Center);
            Stretch(label.rectTransform);
        }

        private static TMP_Text AddText(string name, string text, float size, Vector2 position, Vector2 box, TextAlignmentOptions alignment)
        {
            var label = NewText(name, _content, text, size, alignment);
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            label.rectTransform.pivot = new Vector2(0.5f, 1f);
            label.rectTransform.anchoredPosition = position;
            label.rectTransform.sizeDelta = box;
            return label;
        }

        private static TMP_Text NewText(string name, Transform parent, string text, float size, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshProUGUI>();
            if (_font != null)
                label.font = _font;
            if (_fontMaterial != null)
                label.fontSharedMaterial = _fontMaterial;
            label.text = text;
            label.fontSize = size;
            label.color = TextColor;
            label.alignment = alignment;
            label.enableWordWrapping = true;
            label.raycastTarget = false;
            return label;
        }

        private static Image NewImage(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.AddComponent<Image>();
        }

        private static void PlaceTop(RectTransform rect, float x, float centerFromTop)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, -centerFromTop);
            rect.localScale = Vector3.one;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private static string Roman(int n) => n switch
        {
            1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", 10 => "X",
            _ => n.ToString(),
        };
    }
}

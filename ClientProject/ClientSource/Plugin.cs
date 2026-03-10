// Copyright (c) 2026 @Retype15. Licensed under SOS Custom Permissive License (SCPL).
// See LICENSE file in the project root for full license information.

using Barotrauma;
using System.Xml.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace SOS
{
    public partial class Plugin : IAssemblyPlugin
    {
        private SOSController? controller;

        public void InitClient()
        {
            controller = new SOSController();

            DebugConsole.commands.Add(new DebugConsole.Command(
                name: "sos",
                help: TextSOS.Get("sos.command.help", "Open/Close SOS.").Value,
                onExecute: _ => CrossThread.RequestExecutionOnMainThread(() => controller?.ToggleUI()),
                getValidArgs: null,
                isCheat: false
            )
            {
                RelayToServer = false, // Client only
                OnClientExecute = _ => CrossThread.RequestExecutionOnMainThread(() => controller?.ToggleUI())
            });

            GameMain.LuaCs.Hook.Add("keyupdate", "SOS_UpdateLoop", _ =>
            {
                controller?.Update();
                return null;
            });

            LuaCsLogger.LogMessage(TextSOS.Get("sos.client.init", "[SOS] Client: Initialized. Press 'J' to open.").Value);
        }

        public void DisposeClient()
        {
            controller?.SaveSettings();
            controller?.Destroy();
            controller = null;
        }
    }

    public class SOSController
    {
        private SOSWindow? mainWindow;

        public HashSet<string> FavoritedItems { get; } = new HashSet<string>();

        public TrackerManager Tracker { get; } = new TrackerManager();
        private readonly Keys toggleKey = Keys.J;
        private bool wasKeyDown = false;

        public string LastSearchQuery { get; set; } = "";
        public ItemPrefab? CurrentItem { get; private set; }

        public Stack<ItemPrefab> HistoryBack { get; } = new Stack<ItemPrefab>();
        public Stack<ItemPrefab> HistoryForward { get; } = new Stack<ItemPrefab>();

        private bool isDirty = false;
        private const int CurrentSaveVersion = 1;
        private readonly string configPath = "SOS_Settings.xml";

        public SOSController()
        {
            LoadSettings();
        }

        private void MarkDirty() => isDirty = true;

        public void SetTrackedItem(ItemPrefab? item, FabricationRecipe? recipe = null)
        {
            Tracker.SetTrackedItem(item, recipe);
            MarkDirty();
        }

        public void AddFavorite(string id) { if (FavoritedItems.Add(id)) MarkDirty(); }
        public void RemoveFavorite(string id) { if (FavoritedItems.Remove(id)) MarkDirty(); }

        public void ToggleUI()
        {
            if (mainWindow != null)
            {
                SaveSettings();
                this.Destroy();
            }
            else
            {
                if (Screen.Selected == null) return;

                mainWindow = new SOSWindow(this);

                if (CurrentItem != null)
                {
                    UpdateWindowDetails(CurrentItem);
                }
            }
        }

        public void Destroy()
        {
            if (mainWindow != null)
            {
                mainWindow.Destroy();
                mainWindow = null;
            }
        }

        public void OnItemSelected(ItemPrefab item, bool isHistoryNavigation = false)
        {
            if (item == null) return;

            if (!isHistoryNavigation && CurrentItem != null && CurrentItem != item)
            {
                HistoryBack.Push(CurrentItem);
                HistoryForward.Clear();
            }

            if (CurrentItem != item)
            {
                CurrentItem = item;
                MarkDirty();
            }
            UpdateWindowDetails(item);
        }

        public void SaveSettings()
        {
            if (!isDirty) return;

            try
            {
                XDocument doc = new XDocument(
                    new XElement("SOSSettings",
                        new XAttribute("version", CurrentSaveVersion),

                        new XElement("Favorites",
                            FavoritedItems.Select(f => new XElement("Item", new XAttribute("id", f)))
                        ),

                        new XElement("State",
                            new XAttribute("lastItem", CurrentItem?.Identifier.Value ?? ""),
                            new XAttribute("lastSearch", LastSearchQuery ?? "")
                        ),

                        new XElement("Tracker",
                            new XAttribute("targetId", Tracker.TrackedItem?.Identifier.Value ?? ""),
                            new XAttribute("recipeHash", Tracker.TrackedRecipe?.RecipeHash.ToString() ?? "0")
                        )
                    )
                );

                doc.Save(configPath);
                isDirty = false;
                LuaCsLogger.LogMessage(TextSOS.Get("sos.config.saved", "[SOS] Settings saved (v[version]).").Replace("[version]", CurrentSaveVersion.ToString()).Value);
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError(TextSOS.Get("sos.config.save_error", "[SOS] Failed to save settings: [error]").Replace("[error]", e.Message).Value);
            }
        }

        public void LoadSettings()
        {
            if (!File.Exists(configPath)) return;

            try
            {
                XDocument doc = XDocument.Load(configPath);
                XElement? root = doc.Element("SOSSettings");
                if (root == null) return;

                int fileVersion = int.Parse(root.Attribute("version")?.Value ?? "0");

                if (fileVersion >= 1)
                {
                    var favs = root.Element("Favorites")?.Elements("Item");
                    if (favs != null)
                    {
                        foreach (var f in favs) FavoritedItems.Add(f.Attribute("id")?.Value ?? "");
                    }

                    var state = root.Element("State");
                    if (state != null)
                    {
                        LastSearchQuery = state.Attribute("lastSearch")?.Value ?? "";
                        string lastItemId = state.Attribute("lastItem")?.Value ?? "";

                        if (!string.IsNullOrEmpty(lastItemId))
                        {
                            CurrentItem = ItemPrefab.Prefabs.FirstOrDefault(p => p.Identifier.Value == lastItemId);
                        }
                    }

                    var tracker = root.Element("Tracker");
                    if (tracker != null)
                    {
                        string targetId = tracker.Attribute("targetId")?.Value ?? "";
                        uint recipeHash = uint.Parse(tracker.Attribute("recipeHash")?.Value ?? "0");

                        var targetPrefab = ItemPrefab.Prefabs.FirstOrDefault(p => p.Identifier.Value == targetId);
                        if (targetPrefab != null)
                        {
                            var specificRecipe = targetPrefab.FabricationRecipes?.Values
                                .FirstOrDefault(r => r.RecipeHash == recipeHash);

                            Tracker.SetTrackedItem(targetPrefab, specificRecipe);
                        }
                    }
                }

                LuaCsLogger.LogMessage(TextSOS.Get("sos.config.loaded", "[SOS] Settings v[version] loaded successfully.").Replace("[version]", fileVersion.ToString()).Value);
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError(TextSOS.Get("sos.config.load_error", "[SOS] Error reading settings file: [error]").Replace("[error]", e.Message).Value);
            }
        }

        private void UpdateWindowDetails(ItemPrefab item)
        {
            if (mainWindow == null) return;

            var craftRecipes = RecipeAnalyzer.GetCraftingRecipes(item);
            var deconOutputs = RecipeAnalyzer.GetDeconstructionOutputs(item);
            var usesAsIngredient = RecipeAnalyzer.GetUsesAsIngredient(item);
            var obtainedFrom = RecipeAnalyzer.GetSourcesFromDeconstruction(item);

            mainWindow.UpdateDetailsPanel(item, craftRecipes, deconOutputs, usesAsIngredient, obtainedFrom);

            mainWindow.UpdateNavigationButtons();
        }

        public void NavigateBack()
        {
            if (HistoryBack.Count > 0)
            {
                if (CurrentItem != null) HistoryForward.Push(CurrentItem);
                CurrentItem = HistoryBack.Pop();
                UpdateWindowDetails(CurrentItem);
            }
        }

        public void NavigateForward()
        {
            if (HistoryForward.Count > 0)
            {
                if (CurrentItem != null) HistoryBack.Push(CurrentItem);
                CurrentItem = HistoryForward.Pop();
                UpdateWindowDetails(CurrentItem);
            }
        }

        public void OpenContextMenu(ItemPrefab item)
        {
            if (item == null) return;
            var options = new List<ContextMenuOption>();

            options.Add(new ContextMenuOption(TextSOS.Get("sos.context.track", "Track to HUD"), isEnabled: true, onSelected: () =>
            {
                Tracker.SetTrackedItem(item);
            }));

            options.Add(new ContextMenuOption(TextSOS.Get("sos.context.view_recipes", "View Recipes"), isEnabled: true, onSelected: () =>
            {
                OnItemSelected(item);
            }));

            string targetId = item.Identifier.Value;
            bool isFav = FavoritedItems.Contains(targetId);
            string favText = isFav ? TextSOS.Get("sos.context.remove_favorite", "Remove from Favorites").Value : TextSOS.Get("sos.context.add_favorite", "Add to Favorites").Value;

            options.Add(new ContextMenuOption(favText, isEnabled: true, onSelected: () =>
            {
                if (isFav) FavoritedItems.Remove(targetId);
                else FavoritedItems.Add(targetId);

                mainWindow?.RefreshSearch();
            }));

            GUIContextMenu.CreateContextMenu(PlayerInput.MousePosition, item.Name, null, options.ToArray());
        }

        public void OpenRecipeContextMenu(ItemPrefab item, FabricationRecipe recipe)
        {
            if (item == null || recipe == null) return;

            var options = new List<ContextMenuOption>();

            bool isCurrentlyTracked = Tracker.TrackedRecipe == recipe;

            if (isCurrentlyTracked)
            {
                options.Add(new ContextMenuOption(TextSOS.Get("sos.context.untrack", "Remove from HUD"), isEnabled: true, onSelected: () =>
                {
                    Tracker.SetTrackedItem(null);
                }));
            }
            else
            {
                options.Add(new ContextMenuOption(TextSOS.Get("sos.context.track_recipe", "Add to HUD"), isEnabled: true, onSelected: () =>
                {
                    Tracker.SetTrackedItem(item, recipe);
                }));
            }

            //options.Add(new ContextMenuOption("Ver más info (WIP)", isEnabled: false));

            GUIContextMenu.CreateContextMenu(PlayerInput.MousePosition, TextSOS.Get("sos.context.recipe_options", "Recipe Options"), null, options.ToArray());
        }

        public void OnRecipeSelected(ItemPrefab item, FabricationRecipe recipe)
        {
            Tracker.SetTrackedItem(item, recipe);
            OnItemSelected(item);
        }

        public void Update()
        {
            if (GUI.KeyboardDispatcher.Subscriber != null) return;

            var kb = Keyboard.GetState();
            bool isKeyDownNow = kb.IsKeyDown(toggleKey);

            if (isKeyDownNow && !wasKeyDown)
            {
                CrossThread.RequestExecutionOnMainThread(() => ToggleUI());
            }
            wasKeyDown = isKeyDownNow;

            if (mainWindow != null)
            {
                if (kb.IsKeyDown(Keys.Escape))
                {
                    CrossThread.RequestExecutionOnMainThread(() => ToggleUI());
                }

                if (PlayerInput.KeyHit(Keys.Back) || PlayerInput.Mouse4ButtonClicked())
                {
                    CrossThread.RequestExecutionOnMainThread(() => NavigateBack());
                }
                mainWindow.Update();
            }

            Tracker.UpdateHUD();
        }
    }

    public static class RecipeAnalyzer
    {
        public static List<FabricationRecipe> GetCraftingRecipes(ItemPrefab item)
            => item.FabricationRecipes?.Values.ToList() ?? new List<FabricationRecipe>();

        public static List<DeconstructItem> GetDeconstructionOutputs(ItemPrefab item)
            => item.DeconstructItems.IsDefaultOrEmpty ? new List<DeconstructItem>() : item.DeconstructItems.ToList();

        public static List<Tuple<ItemPrefab, FabricationRecipe>> GetUsesAsIngredient(ItemPrefab targetItem)
        {
            var results = new List<Tuple<ItemPrefab, FabricationRecipe>>();
            foreach (var prefab in ItemPrefab.Prefabs)
            {
                if (prefab.FabricationRecipes == null) continue;
                foreach (var recipe in prefab.FabricationRecipes.Values)
                {
                    if (recipe.RequiredItems.Length > 0 && recipe.RequiredItems.Any(req => req.ItemPrefabs != null && req.ItemPrefabs.Any(p => p != null && p.Identifier == targetItem.Identifier)))
                    {
                        results.Add(new Tuple<ItemPrefab, FabricationRecipe>(prefab, recipe));
                    }
                }
            }
            return results;
        }

        public static List<ItemPrefab> GetSourcesFromDeconstruction(ItemPrefab targetItem)
        {
            var results = new List<ItemPrefab>();
            foreach (var prefab in ItemPrefab.Prefabs)
            {
                if (!prefab.DeconstructItems.IsDefaultOrEmpty && prefab.DeconstructItems.Any(di => di.ItemIdentifier == targetItem.Identifier))
                {
                    results.Add(prefab);
                }
            }
            return results;
        }
    }

    public class SOSWindow
    {
        private readonly GUIFrame? mainFrame;
        private readonly GUIListBox? itemList;
        private readonly GUIFrame? detailsHeader;
        private readonly GUIListBox? colObtain;
        private readonly GUIListBox? colUsage;
        private readonly GUIListBox? metaPanel;
        private readonly SOSController controller;
        private readonly GUITextBox? searchBox;
        private GUIButton? btnBack;
        private GUIButton? btnForward;

        private List<ItemPrefab> allFilteredItems = new List<ItemPrefab>();
        private int itemsLoaded = 0;
        private const int ChunkSize = 50;
        private bool isUpdating = false;

        public SOSWindow(SOSController controller)
        {
            this.controller = controller;
            var parentComponent = Screen.Selected?.Frame;
            if (parentComponent == null) return;

            mainFrame = new GUIFrame(new RectTransform(new Vector2(0.95f, 0.9f), parentComponent.RectTransform, Anchor.Center), "InnerFrame") { CanBeFocused = true };

            var topBar = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.05f), mainFrame.RectTransform, Anchor.TopCenter), "GUIFrameBottom");
            _ = new GUITextBlock(new RectTransform(Vector2.One, topBar.RectTransform), TextSOS.Get("sos.window.title", "SOS - Recipe Browser"), textAlignment: Alignment.Center, font: GUIStyle.LargeFont);

            var historyLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.12f, 0.8f), topBar.RectTransform, Anchor.CenterLeft) { AbsoluteOffset = new Point(10, 0) }, isHorizontal: true) { RelativeSpacing = 0.05f };
            btnBack = new GUIButton(new RectTransform(new Vector2(0.45f, 1f), historyLayout.RectTransform), "", style: "GUIButtonToggleLeft") { OnClicked = (_, _) => { controller.NavigateBack(); return true; } };
            if (btnBack.Children.FirstOrDefault() is GUIImage imgBack) imgBack.SpriteEffects = Microsoft.Xna.Framework.Graphics.SpriteEffects.FlipHorizontally;
            btnForward = new GUIButton(new RectTransform(new Vector2(0.45f, 1f), historyLayout.RectTransform), "", style: "GUIButtonToggleRight") { OnClicked = (_, _) => { controller.NavigateForward(); return true; } };

            var topButtons = new GUILayoutGroup(new RectTransform(new Vector2(0.2f, 0.8f), topBar.RectTransform, Anchor.CenterRight) { AbsoluteOffset = new Point(10, 0) }, isHorizontal: true) { Stretch = false, RelativeSpacing = 0.05f, ChildAnchor = Anchor.CenterRight };
            _ = new GUIButton(new RectTransform(new Vector2(0.2f, 1f), topButtons.RectTransform), "", style: "GUICancelButton") { OnClicked = (_, _) => { controller.ToggleUI(); return true; } };
            _ = new GUIButton(new RectTransform(new Vector2(0.65f, 1f), topButtons.RectTransform), TextSOS.Get("sos.window.clear_hud", "Clear HUD"), style: "DeviceButton") { OnClicked = (_, _) => { controller.Tracker.SetTrackedItem(null); return true; } };

            var contentArea = new GUILayoutGroup(new RectTransform(new Vector2(0.98f, 0.94f), mainFrame.RectTransform, Anchor.BottomCenter) { AbsoluteOffset = new Point(0, 5) }) { IsHorizontal = true, Stretch = true, RelativeSpacing = 0.01f };

            var leftPanel = new GUILayoutGroup(new RectTransform(new Vector2(0.22f, 1f), contentArea.RectTransform)) { Stretch = true, RelativeSpacing = 0.01f };
            var searchContainer = new GUIFrame(new RectTransform(new Vector2(1f, 0.04f), leftPanel.RectTransform), style: "InnerFrame");
            searchBox = GUI.CreateTextBoxWithPlaceholder(searchContainer.RectTransform, controller.LastSearchQuery, TextSOS.Get("sos.window.search_placeholder", "Search item..."));
            searchBox.OnTextChanged += (_, text) => { controller.LastSearchQuery = text; UpdateSearch(text); return true; };
            itemList = new GUIListBox(new RectTransform(new Vector2(1f, 0.95f), leftPanel.RectTransform), style: "PowerButtonFrame") { Padding= new Vector4(8,5,5,5) };

            var centerPanel = new GUILayoutGroup(new RectTransform(new Vector2(0.53f, 1f), contentArea.RectTransform)) { Stretch = true, RelativeSpacing = 0.005f };
            detailsHeader = new GUIFrame(new RectTransform(new Vector2(1f, 0.1f), centerPanel.RectTransform), style: "CircuitBoxFrame") { Color = Color.Black * 0.4f };
            var recipeSplit = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.89f), centerPanel.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.01f };

            var leftRecipeCol = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1f), recipeSplit.RectTransform)) { Stretch = true };
            _ = new GUITextBlock(new RectTransform(new Vector2(1f, 0.05f), leftRecipeCol.RectTransform), TextSOS.Get("sos.window.obtain", "OBTAIN"), font: GUIStyle.SubHeadingFont, textColor: Color.LightGreen, textAlignment: Alignment.Center);
            colObtain = new GUIListBox(new RectTransform(new Vector2(1f, 0.95f), leftRecipeCol.RectTransform), style: "GUIBackgroundBlocker") { Spacing = 5 }; //PowerButtonFrame

            var rightRecipeCol = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1f), recipeSplit.RectTransform)) { Stretch = true };
            _ = new GUITextBlock(new RectTransform(new Vector2(1f, 0.05f), rightRecipeCol.RectTransform), TextSOS.Get("sos.window.usage", "USAGE"), font: GUIStyle.SubHeadingFont, textColor: Color.Cyan, textAlignment: Alignment.Center);
            colUsage = new GUIListBox(new RectTransform(new Vector2(1f, 0.95f), rightRecipeCol.RectTransform), style: "GUIBackgroundBlocker") { Spacing = 5 };

            var rightPanel = new GUILayoutGroup(new RectTransform(new Vector2(0.25f, 1f), contentArea.RectTransform)) { Stretch = true };
            metaPanel = new GUIListBox(new RectTransform(new Vector2(1f, 1f), rightPanel.RectTransform), style: "CircuitBoxFrame")
            {
                Spacing = 10,
                Padding = new Vector4(12),
                CanBeFocused = false,
                Color = Color.Black * 0.4f
            };

            UpdateSearch(controller.LastSearchQuery);
            UpdateNavigationButtons();
        }

        public void UpdateNavigationButtons()
        {
            if (btnBack != null) btnBack.Enabled = controller.HistoryBack.Count > 0;
            if (btnForward != null) btnForward.Enabled = controller.HistoryForward.Count > 0;
        }

        public void RefreshSearch() => UpdateSearch(searchBox?.Text ?? "");

        public void Destroy()
        {
            if (mainFrame?.RectTransform != null)
            {
                mainFrame.RectTransform.Parent = null;
            }
        }

        private void UpdateSearch(string query)
        {
            if (itemList == null) return;

            allFilteredItems = ItemPrefab.Prefabs.Where(p =>
                string.IsNullOrWhiteSpace(query) ||
                p.Name.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Identifier.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Tags.Any(t => t.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            )
            .OrderByDescending(p => controller.FavoritedItems.Contains(p.Identifier.Value))
            .ThenBy(p => p.Name.Value)
            .ToList();

            itemsLoaded = 0;
            itemList.Content.ClearChildren();

            itemList.ScrollBar.BarScroll = 0;

            LoadNextChunk();
        }

        private void LoadNextChunk()
        {
            if (itemList == null || itemsLoaded >= allFilteredItems.Count || isUpdating) return;

            isUpdating = true;

            float totalScrollableHeightBefore = itemList.Content.Rect.Height - itemList.Rect.Height;
            float currentScrollPixels = itemList.ScrollBar.BarScroll * totalScrollableHeightBefore;

            int nextBatch = Math.Min(itemsLoaded + ChunkSize, allFilteredItems.Count);
            for (int i = itemsLoaded; i < nextBatch; i++)
            {
                var prefab = allFilteredItems[i];
                bool isFav = controller.FavoritedItems.Contains(prefab.Identifier.Value);

                var btn = new GUIButton(new RectTransform(new Point(itemList.Content.Rect.Width, 35), itemList.Content.RectTransform), style: "ListBoxElement")
                {
                    OnClicked = (_, _) => { controller.OnItemSelected(prefab); return true; },
                    OnSecondaryClicked = (_, _) => { controller.OpenContextMenu(prefab); return true; }
                };

                string prefix = isFav ? "* " : "";
                CardBuilder.DrawCompactItemRow(btn, prefab, 1, false, extraText: prefix, color: isFav ? Color.Gold : Color.White);
            }
            itemsLoaded = nextBatch;

            itemList.RecalculateChildren();
            itemList.UpdateScrollBarSize();

            float totalScrollableHeightAfter = itemList.Content.Rect.Height - itemList.Rect.Height;

            if (totalScrollableHeightAfter > 0)
            {
                // TODO: This not work properly at now...
                float newBarScroll = currentScrollPixels / totalScrollableHeightAfter;
                newBarScroll = MathHelper.Clamp(newBarScroll, 0, 0.80f);
                itemList.ScrollBar.BarScroll = newBarScroll;
            }

            isUpdating = false;
        }

        public void Update()
        {
            if (itemList == null || itemsLoaded >= allFilteredItems.Count || isUpdating) return;

            int total = allFilteredItems.Count;

            int currentIndex = (int)(itemList.ScrollBar.BarScroll * (total - 1));

            int threshold = total - 5;

            if (currentIndex >= threshold)
            {
                LoadNextChunk();
            }
        }

        public void UpdateDetailsPanel(ItemPrefab targetItem, List<FabricationRecipe> craft, List<DeconstructItem> decon, List<Tuple<ItemPrefab, FabricationRecipe>> uses, List<ItemPrefab> sources)
        {
            if (detailsHeader == null || colObtain == null || colUsage == null || metaPanel == null) return;

            detailsHeader.ClearChildren();

            var headerLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.85f, 1f), detailsHeader.RectTransform, Anchor.CenterRight), isHorizontal: true) { AbsoluteSpacing = 15 };

            Sprite? icon = targetItem.InventoryIcon ?? targetItem.Sprite;
            if (icon != null)
            {
                var imgFrame = new GUIFrame(new RectTransform(new Vector2(0.15f, 0.9f), headerLayout.RectTransform, Anchor.CenterLeft), style: null);
                _ = new GUIImage(new RectTransform(new Vector2(0.8f, 0.8f), imgFrame.RectTransform, Anchor.Center), icon, scaleToFit: true) { Color = targetItem.InventoryIconColor };
            }
            _ = new GUITextBlock(new RectTransform(new Vector2(0.8f, 1f), headerLayout.RectTransform), targetItem.Name.Value, font: GUIStyle.LargeFont, textColor: Color.White, textAlignment: Alignment.CenterLeft);

            Action<ItemPrefab> onPrimary = (clickedItem) => controller.OnItemSelected(clickedItem);
            Action<ItemPrefab> onSecondary = (clickedItem) => controller.OpenContextMenu(clickedItem);

            colObtain.Content.ClearChildren();
            foreach (var recipe in craft) CardBuilder.DrawCraftCard(colObtain, recipe, targetItem, controller, onPrimary, onSecondary); ;
            foreach (var source in sources) CardBuilder.DrawSourceCard(colObtain, source, onPrimary, onSecondary);

            colUsage.Content.ClearChildren();
            if (decon.Count > 0) CardBuilder.DrawDeconCard(colUsage, targetItem, decon, onPrimary, onSecondary);
            foreach (var use in uses) CardBuilder.DrawUseCard(colUsage, use, onPrimary, onSecondary);

            metaPanel.Content.ClearChildren();

            _ = new GUITextBlock(new RectTransform(new Point(metaPanel.Content.Rect.Width, 25), metaPanel.Content.RectTransform), TextSOS.Get("sos.window.additional_info", "ADDITIONAL INFO"), font: GUIStyle.SubHeadingFont, textColor: Color.Gold, textAlignment: Alignment.Center);

            void AddStat(string label, string? value, Color valColor)
            {
                if (string.IsNullOrEmpty(value)) return;

                var row = new GUILayoutGroup(new RectTransform(new Point(metaPanel.Content.Rect.Width, 25), metaPanel.Content.RectTransform), isHorizontal: true);
                _ = new GUITextBlock(new RectTransform(new Vector2(0.4f, 1f), row.RectTransform), label, font: GUIStyle.SmallFont, textColor: Color.Gray);
                _ = new GUITextBlock(new RectTransform(new Vector2(0.6f, 1f), row.RectTransform), value, font: GUIStyle.SmallFont, textColor: valColor);
            }

            string safeId = targetItem.Identifier.IsEmpty ? "unknown" : targetItem.Identifier.Value;
            AddStat(TextSOS.Get("sos.item.id", "ID:").Value, safeId, Color.White);

            int basePrice = targetItem.DefaultPrice?.Price ?? 0;
            AddStat(TextSOS.Get("sos.item.price", "Price:").Value, $"{basePrice} mk", Color.Yellow);

            AddStat(TextSOS.Get("sos.item.max_stack", "Max Stack:").Value, targetItem.MaxStackSize.ToString(), Color.White);

            if (!targetItem.Description.IsNullOrEmpty())
            {
                _ = new GUITextBlock(new RectTransform(new Point(metaPanel.Content.Rect.Width, 20), metaPanel.Content.RectTransform), TextSOS.Get("sos.item.description", "DESCRIPTION:"), font: GUIStyle.SmallFont, textColor: Color.Gold);

                var descBlock = new GUITextBlock(new RectTransform(new Vector2(1f, 0f), metaPanel.Content.RectTransform), targetItem.Description.Value, font: GUIStyle.SmallFont, textColor: Color.LightGray)
                {
                    Wrap = true,
                    CanBeFocused = false
                };

                int h = Math.Max((int)descBlock.TextSize.Y + 10, 30);
                descBlock.RectTransform.NonScaledSize = new Point(descBlock.Rect.Width, h);
            }

            if (targetItem.Tags != null && targetItem.Tags.Count() > 0)
            {
                _ = new GUITextBlock(new RectTransform(new Point(metaPanel.Content.Rect.Width, 20), metaPanel.Content.RectTransform), TextSOS.Get("sos.item.tags", "TAGS:"), font: GUIStyle.SmallFont, textColor: Color.Gold);

                var tagsContainer = new GUIListBox(new RectTransform(new Point(metaPanel.Content.Rect.Width, 45), metaPanel.Content.RectTransform), isHorizontal: true, style: null)
                {
                    Spacing = 1,
                    ScrollBarVisible = false,
                    CanBeFocused = false,
                    ScrollBar = { BarSize = 10, Color = Color.LightSkyBlue * 0.5f }
                };

                foreach (var tag in targetItem.Tags)
                {
                    if (tag.IsEmpty) continue;

                    var tagBadge = new GUIButton(new RectTransform(new Point(10, 24), tagsContainer.Content.RectTransform), style: "OuterGlow")
                    {
                        Color = Color.LightSkyBlue * 0.2f,
                        OnClicked = (_, _) => {
                            if (searchBox != null) searchBox.Text = tag.Value;
                            UpdateSearch(tag.Value);
                            return true;
                        }
                    };

                    var tagText = new GUITextBlock(new RectTransform(Vector2.One, tagBadge.RectTransform), tag.Value.ToLower(), font: GUIStyle.SmallFont, textAlignment: Alignment.Center)
                    {
                        Padding = new Vector4(12, 0, 12, 0),
                        TextColor = Color.LightSkyBlue,
                        CanBeFocused = false
                    };
                    tagBadge.RectTransform.NonScaledSize = new Point((int)tagText.TextSize.X + 24, 24);
                }
            }
        }
    }

    public static class CardBuilder
    {
        private const int RowHeight = 22;
        private const int HeaderHeight = 20;
        private const int CardPadding = 10;

        public static void DrawHeader(GUIFrame parent, ItemPrefab item, SOSController controller)
        {
            var layout = new GUILayoutGroup(new RectTransform(Vector2.One, parent.RectTransform), isHorizontal: true) { AbsoluteSpacing = 10 };
            Sprite? icon = item.InventoryIcon ?? item.Sprite;
            if (icon != null)
            {
                var imgFrame = new GUIFrame(new RectTransform(new Vector2(0.1f, 0.9f), layout.RectTransform, Anchor.CenterLeft) { AbsoluteOffset = new Point(10, 0) }, style: "InnerFrame");
                _ = new GUIImage(new RectTransform(new Vector2(0.8f, 0.8f), imgFrame.RectTransform, Anchor.Center), icon, scaleToFit: true) { Color = item.InventoryIconColor };
            }
            var textLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.8f, 1f), layout.RectTransform)) { RelativeSpacing = 0.02f };
            _ = new GUITextBlock(new RectTransform(new Vector2(1f, 0.6f), textLayout.RectTransform), item.Name.Value, font: GUIStyle.LargeFont, textColor: Color.Cyan);
            _ = new GUITextBlock(new RectTransform(new Vector2(1f, 0.4f), textLayout.RectTransform), TextSOS.Get("sos.item.header_info", "ID: [id] | Price: [price] mk").Replace("[id]", item.Identifier.Value).Replace("[price]", (item.DefaultPrice?.Price ?? 0).ToString()), font: GUIStyle.SmallFont, textColor: Color.Gray);
        }

        public static void BuildColumn<T>(GUIListBox container, string title, List<T> items, Action<GUIListBox, T> drawCard)
        {
            var colFrame = new GUIFrame(new RectTransform(new Point(280, container.Content.Rect.Height), container.Content.RectTransform), style: null);
            _ = new GUITextBlock(new RectTransform(new Point(colFrame.Rect.Width, 25), colFrame.RectTransform), title, font: GUIStyle.SubHeadingFont, textColor: Color.Gold, textAlignment: Alignment.Center);

            var list = new GUIListBox(new RectTransform(new Point(colFrame.Rect.Width, colFrame.Rect.Height - 30), colFrame.RectTransform, Anchor.BottomCenter)) { Spacing = 5 };
            foreach (var item in items) drawCard(list, item);
        }

        public static void DrawCraftCard(GUIListBox col, FabricationRecipe recipe, ItemPrefab targetItem, SOSController controller, Action<ItemPrefab> onPrimary, Action<ItemPrefab> onSecondary)
        {
            int extraRows = recipe.RequiredSkills.Length + (recipe.RequiresRecipe ? 1 : 0);
            int calculatedHeight = HeaderHeight + ((recipe.RequiredItems.Length + extraRows) * RowHeight) + CardPadding;

            bool isTracked = controller.Tracker.TrackedRecipe == recipe;

            var card = new GUIButton(new RectTransform(new Point(col.Content.Rect.Width, calculatedHeight), col.Content.RectTransform), style: "InnerFrame")
            {
                Color = Color.Black * 0.4f,
                OutlineColor = isTracked ? Color.Gold : Color.Transparent,
                OnClicked = (_, _) => { controller.OnRecipeSelected(targetItem, recipe); return true; },
                OnSecondaryClicked = (_, _) => { controller.OpenRecipeContextMenu(targetItem, recipe); return true; }
            };

            var layout = new GUILayoutGroup(new RectTransform(new Point(card.Rect.Width - 10, card.Rect.Height - 10), card.RectTransform, Anchor.Center))
            {
                AbsoluteSpacing = 2,
                IsHorizontal = false,
                CanBeFocused = false
            };

            string machine;
            if (recipe.SuitableFabricatorIdentifiers.Length > 0)
            {
                machine = string.Join(", ", recipe.SuitableFabricatorIdentifiers.Select(id =>
                    TextManager.Get("EntityName." + id).Fallback(id.Value).Value));
            }
            else { machine = TextSOS.Get("sos.recipe.hand", "Hand").Value; }

            var header = new GUILayoutGroup(new RectTransform(new Point(layout.Rect.Width, HeaderHeight), layout.RectTransform), isHorizontal: true) { CanBeFocused = false };
            new GUITextBlock(new RectTransform(new Vector2(0.7f, 1f), header.RectTransform), machine.ToUpper(), font: GUIStyle.SmallFont, textColor: isTracked ? Color.Gold : Color.Yellow) { CanBeFocused = false };
            new GUITextBlock(new RectTransform(new Vector2(0.3f, 1f), header.RectTransform), $"{recipe.RequiredTime}s", font: GUIStyle.SmallFont, textAlignment: Alignment.Right) { CanBeFocused = false };

            if (recipe.RequiresRecipe)
            {
                bool hasUnlocked = Character.Controlled != null && Character.Controlled.HasRecipeForItem(targetItem.Identifier);

                Color recipeColor = hasUnlocked ? Color.LightGreen : Color.Salmon;
                string recipeText = hasUnlocked ? TextSOS.Get("sos.recipe.unlocked", "Recipe Unlocked").Value : TextSOS.Get("sos.recipe.locked", "Requires Recipe to Unlock").Value;

                new GUITextBlock(new RectTransform(new Point(layout.Rect.Width, RowHeight), layout.RectTransform), recipeText, font: GUIStyle.SmallFont, textColor: recipeColor) { CanBeFocused = false };
            }

            foreach (var skill in recipe.RequiredSkills)
            {
                int playerLevel = 0;
                if (Character.Controlled != null)
                {
                    playerLevel = (int)Character.Controlled.GetSkillLevel(skill.Identifier);
                }

                Color skillColor;

                if (playerLevel >= skill.Level)
                {
                    skillColor = Color.LightGreen;
                }
                else if (playerLevel >= skill.Level - 10)
                {
                    skillColor = Color.Yellow;
                }
                else
                {
                    skillColor = Color.Salmon;
                }

                string localizedSkillName = TextManager.Get("SkillName." + skill.Identifier).Fallback(skill.Identifier.Value).Value;

                new GUITextBlock(new RectTransform(new Point(layout.Rect.Width, RowHeight), layout.RectTransform),
                    $"{localizedSkillName}: {playerLevel}/{skill.Level}",
                    font: GUIStyle.SmallFont, textColor: skillColor)
                { CanBeFocused = false };
            }

            foreach (var req in recipe.RequiredItems)
            {
                DrawCompactItemRow(layout, req.FirstMatchingPrefab, req.Amount, true, "", null, onPrimary, onSecondary);
            }
        }

        public static void DrawDeconCard(GUIListBox col, ItemPrefab item, List<DeconstructItem> deconList, Action<ItemPrefab> onPrimary, Action<ItemPrefab> onSecondary)
        {
            var groupedOutputs = deconList
                .GroupBy(di => new { di.ItemIdentifier, di.Commonness, di.MinCondition })
                .Select(g => new
                {
                    ItemIdentifier = g.Key.ItemIdentifier,
                    Commonness = g.Key.Commonness,
                    MinCondition = g.Key.MinCondition,
                    TotalAmount = g.Sum(di => di.Amount)
                })
                .ToList();

            int extraRows = item.RandomDeconstructionOutput ? 1 : 0;
            int calculatedHeight = HeaderHeight + ((groupedOutputs.Count + extraRows) * RowHeight) + CardPadding;

            var card = new GUIFrame(new RectTransform(new Point(col.Content.Rect.Width, calculatedHeight), col.Content.RectTransform), "InnerFrame") 
            { 
                Color = Color.Black * 0.4f,
            };
            var layout = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.95f), card.RectTransform, Anchor.Center)) { AbsoluteSpacing = 2 };

            var header = new GUILayoutGroup(new RectTransform(new Point(layout.Rect.Width, HeaderHeight), layout.RectTransform), isHorizontal: true);
            _ = new GUITextBlock(new RectTransform(new Vector2(0.7f, 1f), header.RectTransform), TextSOS.Get("sos.recipe.deconstructor", "DECONSTRUCTOR"), font: GUIStyle.SmallFont, textColor: Color.Yellow);
            _ = new GUITextBlock(new RectTransform(new Vector2(0.3f, 1f), header.RectTransform), $"{item.DeconstructTime}s", font: GUIStyle.SmallFont, textAlignment: Alignment.Right);

            if (item.RandomDeconstructionOutput)
                _ = new GUITextBlock(new RectTransform(new Point(layout.Rect.Width, RowHeight), layout.RectTransform), TextSOS.Get("sos.recipe.random_outputs", "Gives [amount] at random:").Replace("[amount]", item.RandomDeconstructionOutputAmount.ToString()), font: GUIStyle.SmallFont, textColor: Color.Orange);

            foreach (var output in groupedOutputs)
            {
                var prefab = ItemPrefab.Prefabs.FirstOrDefault(p => p.Identifier == output.ItemIdentifier);
                string extras = output.Commonness < 1f ? $" ({output.Commonness * 100}%)" : "";

                DrawCompactItemRow(layout, prefab, output.TotalAmount, true, extras, output.Commonness < 1f ? Color.Orange : Color.White, onPrimary, onSecondary);
            }
        }

        public static void DrawUseCard(GUIListBox col, Tuple<ItemPrefab, FabricationRecipe> data, Action<ItemPrefab> onPrimary, Action<ItemPrefab> onSecondary)
        {
            int calculatedHeight = HeaderHeight + RowHeight + CardPadding;
            var card = new GUIFrame(new RectTransform(new Point(col.Content.Rect.Width, calculatedHeight), col.Content.RectTransform), "InnerFrame") 
            { 
                Color = Color.Black * 0.4f,
            };
            var layout = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.95f), card.RectTransform, Anchor.Center)) { AbsoluteSpacing = 2 };

            var recipe = data.Item2;
            string machine;
            if (recipe.SuitableFabricatorIdentifiers.Length > 0)
            {
                machine = string.Join(", ", recipe.SuitableFabricatorIdentifiers.Select(id =>
                    TextManager.Get("EntityName." + id).Fallback(id.Value).Value));
            }
            else { machine = TextSOS.Get("sos.recipe.hand", "Hand").Value; }

            new GUITextBlock(new RectTransform(new Point(layout.Rect.Width, HeaderHeight), layout.RectTransform), machine.ToUpper(), font: GUIStyle.SmallFont, textColor: Color.Yellow);

            DrawCompactItemRow(layout, data.Item1, data.Item2.Amount, true, "", null, onPrimary, onSecondary);
        }

        public static void DrawSourceCard(GUIListBox col, ItemPrefab sourceItem, Action<ItemPrefab> onPrimary, Action<ItemPrefab> onSecondary)
        {
            int calculatedHeight = HeaderHeight + RowHeight + CardPadding;
            var card = new GUIFrame(new RectTransform(new Point(col.Content.Rect.Width, calculatedHeight), col.Content.RectTransform), "InnerFrame") 
            { 
                Color = Color.Black * 0.4f,
            };
            var layout = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.95f), card.RectTransform, Anchor.Center)) { AbsoluteSpacing = 2 };

            new GUITextBlock(new RectTransform(new Point(layout.Rect.Width, HeaderHeight), layout.RectTransform), TextSOS.Get("sos.recipe.deconstructing", "DECONSTRUCTING:"), font: GUIStyle.SmallFont, textColor: Color.LightGreen);
            DrawCompactItemRow(layout, sourceItem, 1, true, "", null, onPrimary, onSecondary);
        }

        public static void DrawCompactItemRow(GUIComponent parent, ItemPrefab? prefab, float amount, bool isCardInside, string extraText = "", Color? color = null, Action<ItemPrefab>? onPrimaryClick = null, Action<ItemPrefab>? onSecondaryClick = null)
        {
            var rowRect = new RectTransform(new Point(parent.Rect.Width, RowHeight), parent.RectTransform);

            GUIComponent container;

            if (prefab != null && (onPrimaryClick != null || onSecondaryClick != null))
            {
                var btn = new GUIButton(rowRect, style: "ListBoxElement")
                {
                    OnClicked = (_, _) =>
                    {
                        onPrimaryClick?.Invoke(prefab);
                        return true;
                    },
                    OnSecondaryClicked = (_, _) =>
                    {
                        onSecondaryClick?.Invoke(prefab);
                        return true;
                    }
                };
                container = btn;
            }
            else
            {
                container = new GUILayoutGroup(rowRect, isHorizontal: true) { AbsoluteSpacing = 5, CanBeFocused = false };
            }

            var contentLayout = new GUILayoutGroup(new RectTransform(Vector2.One, container.RectTransform), isHorizontal: true)
            {
                AbsoluteSpacing = 5,
                CanBeFocused = false
            };

            Sprite? icon = prefab?.InventoryIcon ?? prefab?.Sprite;
            if (icon != null)
            {
                var imgFrame = new GUIFrame(new RectTransform(new Point(20, 20), contentLayout.RectTransform, Anchor.CenterLeft) { AbsoluteOffset = new Point(isCardInside ? 0 : 5, 0) }, style: null) { CanBeFocused = false };
                _ = new GUIImage(new RectTransform(Vector2.One, imgFrame.RectTransform), icon, scaleToFit: true) { Color = prefab?.InventoryIconColor ?? Color.White, CanBeFocused = false };
            }

            string name = prefab?.Name.Value ?? "???";
            //string amtStr = amount > 1 || isCardInside ? $" x{amount}" : "";
            string amtStr = (amount > 1) ? $" x{amount}" : "";
            _ = new GUITextBlock(new RectTransform(new Vector2(0.8f, 1f), contentLayout.RectTransform), $"{name}{amtStr}{extraText}", font: GUIStyle.SmallFont, textColor: color ?? Color.White, textAlignment: Alignment.CenterLeft) { CanBeFocused = false };
        }
    }

    public class TrackerManager
    {
        public ItemPrefab? TrackedItem { get; private set; }
        public FabricationRecipe? TrackedRecipe { get; private set; }
        private GUIFrame? hudFrame;

        public void SetTrackedItem(ItemPrefab? item, FabricationRecipe? recipe = null)
        {
            TrackedItem = item;
            TrackedRecipe = recipe ?? item?.FabricationRecipes?.Values.FirstOrDefault();

            if (item == null) { hudFrame = null; }
        }

        public void UpdateHUD()
        {
            if (TrackedItem == null || TrackedRecipe == null) return;
            if (Screen.Selected is not GameScreen) return;

            if (hudFrame == null)
            {
                hudFrame = new GUIFrame(new RectTransform(new Point(250, 150), GUI.Canvas, Anchor.TopRight) { AbsoluteOffset = new Point(20, 150) }, style: "InnerFrame")
                {
                    CanBeFocused = false,
                    Color = Color.Black * 0.5f
                };
            }

            hudFrame.ClearChildren();
            var layout = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.9f), hudFrame.RectTransform, Anchor.Center)) { AbsoluteSpacing = 2, CanBeFocused = false };

            new GUITextBlock(new RectTransform(new Vector2(1f, 0.2f), layout.RectTransform), TextSOS.Get("sos.hud.tracking", "TRACKING:"), font: GUIStyle.SubHeadingFont, textColor: Color.Gold) { CanBeFocused = false };
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.2f), layout.RectTransform), TrackedItem.Name.Value, font: GUIStyle.SmallFont, textColor: Color.Cyan) { CanBeFocused = false };

            foreach (var req in TrackedRecipe.RequiredItems)
            {
                int owned = GetPlayerCount(req);
                bool hasEnough = owned >= req.Amount;

                var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.15f), layout.RectTransform), isHorizontal: true) { CanBeFocused = false };
                new GUITextBlock(new RectTransform(Vector2.One, row.RectTransform),
                    $"{(hasEnough ? "(OK)" : "(  )")} {req.FirstMatchingPrefab?.Name.Value}: {owned}/{req.Amount}",
                    font: GUIStyle.SmallFont,
                    textColor: hasEnough ? Color.LightGreen : Color.LightPink)
                { CanBeFocused = false };
            }

            hudFrame.AddToGUIUpdateList();
        }

        private int GetPlayerCount(FabricationRecipe.RequiredItem req)
        {
            if (Character.Controlled == null) return 0;
            int count = 0;

            foreach (var item in Character.Controlled.Inventory.AllItems)
            {
                if (req.ItemPrefabs.Any(p => p.Identifier == item.Prefab.Identifier))
                {
                    count += 1;
                }
            }
            return count;
        }
    }
}
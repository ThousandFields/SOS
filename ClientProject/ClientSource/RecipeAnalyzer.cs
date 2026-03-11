// Copyright (c) 2026 Retype15
// This file is licensed under the GNU GPLv3.
// See the LICENSE file in the project root for details.

using Barotrauma;

namespace SOS
{
    public static class RecipeAnalyzer
    {
        private static readonly Dictionary<Identifier, List<Tuple<ItemPrefab, FabricationRecipe>>> usesCache = new Dictionary<Identifier, List<Tuple<ItemPrefab, FabricationRecipe>>>();
        private static readonly Dictionary<Identifier, List<ItemPrefab>> sourcesCache = new Dictionary<Identifier, List<ItemPrefab>>();

        public static void ClearSessionCache()
        {
            usesCache.Clear();
            sourcesCache.Clear();
        }

        public static List<FabricationRecipe> GetCraftingRecipes(ItemPrefab item)
            => item.FabricationRecipes?.Values.ToList() ?? new List<FabricationRecipe>();

        public static List<DeconstructItem> GetDeconstructionOutputs(ItemPrefab item)
            => item.DeconstructItems.IsDefaultOrEmpty ? new List<DeconstructItem>() : item.DeconstructItems.ToList();

        public static List<Tuple<ItemPrefab, FabricationRecipe>> GetUsesAsIngredient(ItemPrefab targetItem)
        {
            if (targetItem == null) return new List<Tuple<ItemPrefab, FabricationRecipe>>();

            if (usesCache.TryGetValue(targetItem.Identifier, out var cachedResult))
            {
                return cachedResult;
            }

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

            usesCache[targetItem.Identifier] = results;
            return results;
        }

        public static List<ItemPrefab> GetSourcesFromDeconstruction(ItemPrefab targetItem)
        {
            if (targetItem == null) return new List<ItemPrefab>();

            if (sourcesCache.TryGetValue(targetItem.Identifier, out var cachedResult))
            {
                return cachedResult;
            }

            var results = new List<ItemPrefab>();
            foreach (var prefab in ItemPrefab.Prefabs)
            {
                if (!prefab.DeconstructItems.IsDefaultOrEmpty && prefab.DeconstructItems.Any(di => di.ItemIdentifier == targetItem.Identifier))
                {
                    results.Add(prefab);
                }
            }

            sourcesCache[targetItem.Identifier] = results;
            return results;
        }
    }
}

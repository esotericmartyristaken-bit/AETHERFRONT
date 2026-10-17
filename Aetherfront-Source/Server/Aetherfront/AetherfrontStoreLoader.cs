using Aetherfront.Core;
using Server.Log;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Aetherfront
{
    internal static class AetherfrontStoreLoader
    {
        private const string RelativePath = "Config\\AetherfrontStore.json";

        public static void Load(AetherfrontServerStore store, string baseDirectory)
        {
            var path = Path.Combine(baseDirectory, RelativePath);
            if (!File.Exists(path))
            {
                LunaLog.Normal("Aetherfront store: no store config found; using persisted catalog");
                return;
            }

            List<StoreItem> items;
            try
            {
                items = AetherfrontJson.Deserialize<List<StoreItem>>(File.ReadAllText(path)) ?? new List<StoreItem>();
            }
            catch (Exception e)
            {
                throw new InvalidDataException("Aetherfront store config could not be parsed: " + e.Message, e);
            }

            Validate(items, path);
            store.MergeConfiguredStoreItems(items);
            LunaLog.Normal($"Aetherfront store loaded: {items.Count} from {path}");
        }

        private static void Validate(List<StoreItem> items, string path)
        {
            var duplicate = items.GroupBy(i => i?.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => string.IsNullOrWhiteSpace(g.Key) || g.Count() > 1);
            if (duplicate != null) throw new InvalidDataException($"Aetherfront store item ids must be unique and non-empty: '{duplicate.Key}' in {path}");
            foreach (var item in items)
            {
                if (item.Title == null || item.Title.Trim().Length == 0 || item.Title.Length > 160)
                    throw new InvalidDataException($"Aetherfront store item '{item.Id}' has an invalid title");
                if ((item.Description ?? string.Empty).Length > 1000)
                    throw new InvalidDataException($"Aetherfront store item '{item.Id}' description is too long");
                if (item.PriceFundsMicro < 0 || item.PriceScience < 0 || item.Stock < -1)
                    throw new InvalidDataException($"Aetherfront store item '{item.Id}' has an invalid price or stock");
            }
        }
    }
}

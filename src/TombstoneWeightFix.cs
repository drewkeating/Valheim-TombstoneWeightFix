using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TombstoneWeightFix
{
    // Tombstone Weight Fix.
    //
    // Part 1 (the auto-loot check): ExtraSlots replaces the game's tombstone auto-loot check with a simulation, but it
    //  (1) compares your weight + the tombstone's RAW weight (the lightened-row discount only applies to your own inventory),
    //  (2) takes a belt's carry bonus from the item's template buff, so OtMegingjord's level 2+ bonus is missed, and
    //  (3) includes the temporary Corpse Run buff in the max, so a tombstone can auto-loot and leave you encumbered.
    // This patch redoes the weight comparison with the lightened-row discount, the real belt bonus and the
    // settled max (no Corpse Run). It never turns a "no" caused by slots or ExtraSlots' deferred items into a "yes".
    //
    // Part 2 (smart pickup): when your own tombstone opens as a chest, plan a sorted pickup (gear, slot items, hotbar,
    // then heaviest items first, low-priority items last) and, unless DryRun is on, carry it out with the chest
    // window's own move call, then pack the main grid heaviest-first into the lightened rows.
    //
    // ExtraSlots and OtMegingjord are reached by reflection, so a change in their internals makes this mod log a
    // warning and do nothing instead of breaking the game.
    [BepInPlugin(PluginGuid, "Tombstone Weight Fix", PluginVersion)]
    [BepInDependency("shudnal.ExtraSlots", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("tlisonbee.valheim.otmegingjord", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "scooby_drew902.valheim.tombstoneweightfix";
        public const string PluginVersion = "1.0.0";
        private const string BeltName = "$item_beltstrength";

        private static Plugin _inst;
        private static ConfigEntry<bool> Enabled, LogDetails, SmartPickup, DryRun;
        private static ConfigEntry<string> LowPriorityItems;
        private Harmony _harmony;
        private static bool _running;

        private static MethodInfo _canFit, _recoveryMax, _hasPayload, _isRowAffected, _carryBonusAt, _isOwner, _invChanged, _invUpdateWeight, _clearCached, _autoEquip, _tsOnTakeAll;
        private static PropertyInfo _weightFactor, _heightPlayer, _width;
        private static FieldInfo _placementItem, _tsContainer, _tsLootEffect, _fEquip, _fQuick, _fFood, _fAmmo, _fMisc;

        private void Awake()
        {
            _inst = this;
            Enabled = Config.Bind("General", "Enabled", true,
                "Count the lightened-row weight discount, the real belt bonus and the settled max (without the Corpse Run buff) when deciding if a tombstone can be auto-looted. A tombstone that is too heavy then opens as a chest.");
            LogDetails = Config.Bind("General", "LogDetails", false,
                "Log the numbers used each time a tombstone is checked (for testing).");
            SmartPickup = Config.Bind("Smart pickup", "Enabled", true,
                "When your own tombstone opens as a chest, plan a sorted pickup (gear, hotbar, then heaviest first, low-priority items last).");
            DryRun = Config.Bind("Smart pickup", "DryRun", false,
                "true = only log what the sorted pickup would move. false = actually move the items.");
            LowPriorityItems = Config.Bind("Smart pickup", "Low priority items", "Stone, Wood, FineWood, CoreWood",
                "Comma-separated prefab names or item names ($item_stone). These are taken last, into whatever carry weight is left.");

            var tpio = AccessTools.TypeByName("ExtraSlots.PlayerInventoryOperations");
            var tsi = AccessTools.TypeByName("ExtraSlots.TombStoneInteraction");
            var light = AccessTools.TypeByName("ExtraSlots.LightenedSlots");
            var deferred = AccessTools.TypeByName("ExtraSlots.DeferredTombstone");
            var placement = AccessTools.TypeByName("ExtraSlots.PlayerInventoryOperations+SimulatedEquipmentPlacement");
            var slots = AccessTools.TypeByName("ExtraSlots.Slots");
            if (tpio == null || tsi == null || light == null || deferred == null || placement == null || slots == null)
            {
                Logger.LogWarning("ExtraSlots internals not found (version changed?). Tombstone Weight Fix not applied.");
                return;
            }
            foreach (var m in tpio.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                if (m.Name == "CanFitItems" && m.GetParameters().Length == 2) _canFit = m;
            _recoveryMax = AccessTools.Method(tsi, "GetRecoveryMaxCarryWeight");
            _autoEquip = AccessTools.Method(tsi, "AutoEquipItemsOnTombstoneTakeAll");
            _hasPayload = AccessTools.Method(deferred, "HasPayload");
            _isRowAffected = AccessTools.Method(light, "IsRowAffected");
            _weightFactor = AccessTools.Property(light, "WeightFactor");
            _heightPlayer = AccessTools.Property(slots, "InventoryHeightPlayer");
            _width = AccessTools.Property(slots, "InventoryWidth");
            _clearCached = AccessTools.Method(slots, "ClearCachedItems");
            _placementItem = AccessTools.Field(placement, "Item");
            _tsContainer = AccessTools.Field(typeof(TombStone), "m_container");
            _tsLootEffect = AccessTools.Field(typeof(TombStone), "m_lootStatusEffect");
            _isOwner = AccessTools.Method(typeof(TombStone), "IsOwner");
            _tsOnTakeAll = AccessTools.Method(typeof(TombStone), "OnTakeAllSuccess");
            _invChanged = AccessTools.Method(typeof(Inventory), "Changed");
            _invUpdateWeight = AccessTools.Method(typeof(Inventory), "UpdateTotalWeight");
            var esMain = AccessTools.TypeByName("ExtraSlots.ExtraSlots");
            if (esMain != null)
            {
                _fEquip = AccessTools.Field(esMain, "itemWeightFactorEquipmentSlots");
                _fQuick = AccessTools.Field(esMain, "itemWeightFactorQuickSlots");
                _fFood = AccessTools.Field(esMain, "itemWeightFactorFoodSlots");
                _fAmmo = AccessTools.Field(esMain, "itemWeightFactorAmmoSlots");
                _fMisc = AccessTools.Field(esMain, "itemWeightFactorMiscSlots");
            }
            var otPlugin = AccessTools.TypeByName("OtMegingjord.Plugin");
            if (otPlugin != null) _carryBonusAt = AccessTools.Method(otPlugin, "CarryBonusAt");

            if (_canFit == null || _recoveryMax == null || _hasPayload == null || _isRowAffected == null || _weightFactor == null
                || _heightPlayer == null || _width == null || _placementItem == null || _tsContainer == null || _tsLootEffect == null
                || _isOwner == null || _invChanged == null || _invUpdateWeight == null)
            {
                Logger.LogWarning("ExtraSlots/game members not found (version changed?). Tombstone Weight Fix not applied.");
                return;
            }

            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(AccessTools.Method(typeof(TombStone), "EasyFitInInventory"),
                postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(EasyFitPostfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            _harmony.Patch(AccessTools.Method(typeof(InventoryGui), "Show", new[] { typeof(Container), typeof(int) }),
                postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ShowPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            Logger.LogInfo("Tombstone Weight Fix " + PluginVersion + " loaded.");
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }

        // ---------- helpers ----------

        private sealed class Ctx
        {
            public bool SlotsFit;
            public IList Placements;
            public float CurMax, RecMax, ExtraBelt, LootBonus, SettledMax, Factor;
            public Inventory Tomb, Inv;
            public int Height, Width;
        }

        private static Ctx BuildCtx(TombStone ts, Player player)
        {
            var container = _tsContainer.GetValue(ts) as Container;
            var tomb = container == null ? null : container.GetInventory();
            var inv = player == null ? null : player.GetInventory();
            if (tomb == null || inv == null) return null;
            var c = new Ctx { Tomb = tomb, Inv = inv };
            var args = new object[] { tomb.GetAllItems(), null };
            c.SlotsFit = (bool)_canFit.Invoke(null, args);
            if (!c.SlotsFit) return c;
            c.Placements = (IList)args[1];
            var lootSE = _tsLootEffect.GetValue(ts);
            c.CurMax = player.GetMaxCarryWeight();
            c.RecMax = (float)_recoveryMax.Invoke(null, new object[] { player, lootSE, args[1] });

            float gained = c.RecMax - c.CurMax;
            if (_carryBonusAt != null)
            {
                foreach (var p in c.Placements)
                {
                    var item = _placementItem.GetValue(p) as ItemDrop.ItemData;
                    if (item == null || item.m_shared.m_name != BeltName) continue;
                    var se = item.m_shared.m_equipStatusEffect as SE_Stats;
                    float vanilla = se != null ? se.m_addMaxCarryWeight : 0f;
                    if (vanilla > 0f && gained >= vanilla - 0.01f && item.m_quality > 1)
                        c.ExtraBelt += (float)_carryBonusAt.Invoke(null, new object[] { item.m_quality, 0f });
                    break;
                }
            }
            var loot = lootSE as StatusEffect;
            if (loot != null) loot.ModifyMaxCarryWeight(0f, ref c.LootBonus);
            c.SettledMax = c.RecMax + c.ExtraBelt - c.LootBonus;
            c.Factor = (float)_weightFactor.GetValue(null, null);
            c.Height = (int)_heightPlayer.GetValue(null, null);
            c.Width = (int)_width.GetValue(null, null);
            return c;
        }

        private static float FactorOf(FieldInfo f)
        {
            if (f == null) return 1f;
            var entry = f.GetValue(null) as ConfigEntryBase;
            return entry == null ? 1f : Convert.ToSingle(entry.BoxedValue);
        }

        // ExtraSlots special slot index: <6 quick, 6-7 misc, 8-10 ammo, 11-13 food, >13 equipment.
        private static float SlotFactor(int index)
        {
            if (index < 6) return FactorOf(_fQuick);
            if (index <= 7) return FactorOf(_fMisc);
            if (index <= 10) return FactorOf(_fAmmo);
            if (index <= 13) return FactorOf(_fFood);
            return FactorOf(_fEquip);
        }

        private static bool RowAffected(int row)
        {
            return (bool)_isRowAffected.Invoke(null, new object[] { row });
        }

        private static void InvChanged(Inventory inv)
        {
            _invChanged.Invoke(inv, new object[] { false, false });
            _invUpdateWeight.Invoke(inv, null);
            if (_clearCached != null) _clearCached.Invoke(null, null);
        }

        // ---------- stage 1: the auto-loot check ----------

        private static void EasyFitPostfix(TombStone __instance, Player player, ref bool __result)
        {
            if (!Enabled.Value) return;
            try
            {
                if ((bool)_hasPayload.Invoke(null, new object[] { __instance })) return;
                var c = BuildCtx(__instance, player);
                if (c == null) return;
                if (!c.SlotsFit)
                {
                    if (LogDetails.Value) _inst.Logger.LogInfo("Tombstone check: items do not fit the slots, leaving it as a chest.");
                    return;
                }
                float tombRaw = c.Tomb.GetTotalWeight();
                float tombAdj = 0f;
                foreach (var it in c.Tomb.GetAllItems())
                {
                    float w = it.GetWeight();
                    if (RowAffected(it.m_gridPos.y)) w *= c.Factor;
                    tombAdj += w;
                }
                float have = c.Inv.GetTotalWeight();
                bool ok = have + tombAdj <= c.SettledMax;
                if (LogDetails.Value)
                    _inst.Logger.LogInfo(string.Format(
                        "Tombstone check: own {0:F1} + tombstone raw {1:F1} (adjusted {2:F1}) vs settled max {3:F1} (ExtraSlots {4:F1} + belt upgrade {5:F1} - Corpse Run {6:F1}) -> {7}",
                        have, tombRaw, tombAdj, c.SettledMax, c.RecMax, c.ExtraBelt, c.LootBonus, ok ? "AUTO-LOOT" : "opens as a chest"));
                __result = ok;
            }
            catch (Exception e)
            {
                _inst.Logger.LogWarning("Tombstone Weight Fix error (ignored): " + e.Message);
            }
        }

        // ---------- stage 2: plan and (optionally) carry out a sorted pickup ----------

        private static void ShowPostfix(Container container)
        {
            if (!Enabled.Value || !SmartPickup.Value || container == null || _running) return;
            try
            {
                var ts = container.GetComponentInParent<TombStone>();
                var player = Player.m_localPlayer;
                if (ts == null || player == null || !(bool)_isOwner.Invoke(ts, null)) return;
                var plan = BuildPlan(ts, player);
                if (plan == null) return;
                LogPlan(plan);
                if (!DryRun.Value) _inst.StartCoroutine(Execute(plan, ts, player, container));
            }
            catch (Exception e)
            {
                _inst.Logger.LogWarning("Smart pickup planning error (ignored): " + e.Message);
            }
        }

        private sealed class Cand
        {
            public ItemDrop.ItemData It;
            public float W;
            public float F = 1f;
            public string Name;
            public string Kind;
            public bool Belt;
            public bool Low;
            public ItemDrop.ItemData Occupant;       // hotbar swap: the player's item currently in the slot
            public bool OccupantToInventory;
        }

        private sealed class Plan
        {
            public Ctx C;
            public int H, W, FeatherSlots;
            public float Cap, FinalSim;
            public string Header, Calib;
            public List<Cand> Gear = new List<Cand>(), SlotItems = new List<Cand>(), Hotbar = new List<Cand>(), Main = new List<Cand>();
            public List<string> Taken = new List<string>(), Left = new List<string>();
        }

        private static string NameOf(ItemDrop.ItemData it)
        {
            return Localization.instance.Localize(it.m_shared.m_name) + (it.m_stack > 1 ? " x" + it.m_stack : "");
        }

        private static HashSet<string> LowSet()
        {
            return new HashSet<string>(LowPriorityItems.Value.Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0));
        }

        private static bool IsLow(ItemDrop.ItemData it, HashSet<string> low)
        {
            if (low.Count == 0) return false;
            if (low.Contains(it.m_shared.m_name.ToLowerInvariant())) return true;
            var prefab = it.m_dropPrefab == null ? null : it.m_dropPrefab.name;
            return prefab != null && low.Contains(prefab.ToLowerInvariant());
        }

        // Weight of the rearrangeable main-grid stacks when the heaviest K sit in lightened slots.
        private static float RearrangedWeight(List<float> stacks, int featherSlots, float factor)
        {
            var sorted = stacks.OrderByDescending(x => x).ToList();
            float total = 0f;
            for (int i = 0; i < sorted.Count; i++) total += i < featherSlots ? sorted[i] * factor : sorted[i];
            return total;
        }

        // Main-grid positions in fill order: lightened rows first, then bottom-up, left to right.
        private static List<Vector2i> GridPositions(int H, int W)
        {
            var list = new List<Vector2i>();
            for (int y = H - 1; y >= 1; y--)
                for (int x = 0; x < W; x++) list.Add(new Vector2i(x, y));
            return list.OrderByDescending(p => RowAffected(p.y) ? 1 : 0).ToList();
        }

        private static Plan BuildPlan(TombStone ts, Player player)
        {
            var c = BuildCtx(ts, player);
            if (c == null) return null;
            if (!c.SlotsFit) { _inst.Logger.LogInfo("Smart pickup: items do not fit the slots, nothing planned."); return null; }
            var p = new Plan { C = c, H = c.Height, W = c.Width, Cap = c.SettledMax };
            int H = p.H, W = p.W;
            int featherRows = 0;
            for (int r = 1; r < H; r++) if (RowAffected(r)) featherRows++;
            p.FeatherSlots = featherRows * W;
            int gridSlots = Math.Max(0, (H - 1) * W);
            bool row0Feather = RowAffected(0);
            var low = LowSet();

            float fixedW = 0f;
            var rear = new List<float>();
            foreach (var it in c.Inv.GetAllItems())
            {
                float w = it.GetWeight();
                if (it.m_gridPos.y >= H) fixedW += w * SlotFactor((it.m_gridPos.y - H) * W + it.m_gridPos.x);
                else if (it.m_gridPos.y == 0) fixedW += row0Feather ? w * c.Factor : w;
                else rear.Add(w);
            }

            var gear = new List<Cand>(); var slotItems = new List<Cand>(); var hotbar = new List<Cand>();
            var normal = new List<Cand>(); var lows = new List<Cand>();
            foreach (var it in c.Tomb.GetAllItems())
            {
                var cand = new Cand { It = it, W = it.GetWeight(), Name = NameOf(it), Low = IsLow(it, low) };
                var se = it.m_shared.m_equipStatusEffect as SE_Stats;
                cand.Belt = se != null && se.m_addMaxCarryWeight > 0f;
                int y = it.m_gridPos.y;
                if (y >= H)
                {
                    int index = (y - H) * W + it.m_gridPos.x;
                    cand.F = SlotFactor(index);
                    if (index > 13) { cand.Kind = "gear"; gear.Add(cand); }
                    else { cand.Kind = "slot (quick/misc/ammo/food)"; slotItems.Add(cand); }
                }
                else if (y == 0) { cand.Kind = "hotbar"; cand.F = row0Feather ? c.Factor : 1f; hotbar.Add(cand); }
                else { cand.Kind = cand.Low ? "low priority" : "normal"; (cand.Low ? lows : normal).Add(cand); }
            }

            p.Header = string.Format(
                "Smart pickup ({0}): rows {1}, width {2}, feather rows (main grid) {3} = {4} slots, factor {5}, settled max {6:F1}, own weight now {7:F1}, tombstone {8} items",
                DryRun.Value ? "dry run" : "LIVE", H, W, featherRows, p.FeatherSlots, c.Factor, p.Cap, c.Inv.GetTotalWeight(), c.Tomb.GetAllItems().Count);
            p.Calib = string.Format("Calibration: simulated own weight {0:F1} vs the game's {1:F1} (slot factors: equipment {2}, quick {3}, food {4}, ammo {5}, misc {6})",
                fixedW + RearrangedWeight(rear, p.FeatherSlots, c.Factor), c.Inv.GetTotalWeight(), FactorOf(_fEquip), FactorOf(_fQuick), FactorOf(_fFood), FactorOf(_fAmmo), FactorOf(_fMisc));

            int step = 0;
            Func<float> total = () => fixedW + RearrangedWeight(rear, p.FeatherSlots, c.Factor);

            // Special-slot groups: take only into an EMPTY slot (never displace worn gear or quick-slot items).
            var groups = new List<KeyValuePair<List<Cand>, List<Cand>>>
            {
                new KeyValuePair<List<Cand>, List<Cand>>(gear.OrderByDescending(x => x.Belt).ThenByDescending(x => x.W).ToList(), p.Gear),
                new KeyValuePair<List<Cand>, List<Cand>>(slotItems.OrderByDescending(x => x.W).ToList(), p.SlotItems)
            };
            foreach (var pair in groups)
            {
                foreach (var cand in pair.Key)
                {
                    float w = cand.W * cand.F;
                    var occ = c.Inv.GetItemAt(cand.It.m_gridPos.x, cand.It.m_gridPos.y);
                    bool merge = occ != null && occ.IsSameType(cand.It) && cand.It.m_shared.m_maxStackSize > 1;
                    if (occ != null && !merge) { p.Left.Add(string.Format("{0} [{1}] {2:F1}: that slot is occupied by {3}", cand.Name, cand.Kind, w, NameOf(occ))); continue; }
                    if (total() + w <= p.Cap)
                    {
                        fixedW += w;
                        pair.Value.Add(cand);
                        p.Taken.Add(string.Format("{0,3}. {1} [{2}] {3:F1} (raw {4:F1}) -> running {5:F1}", ++step, cand.Name, cand.Kind, w, cand.W, total()));
                    }
                    else p.Left.Add(string.Format("{0} [{1}] {2:F1}: over the limit", cand.Name, cand.Kind, w));
                }
            }

            // Hotbar: back to the original slot; a different item there is swapped out.
            foreach (var cand in hotbar.OrderBy(x => x.It.m_gridPos.x))
            {
                float w = cand.W * cand.F;
                var occ = c.Inv.GetItemAt(cand.It.m_gridPos.x, 0);
                bool merge = occ != null && occ.IsSameType(cand.It) && cand.It.m_shared.m_maxStackSize > 1;
                float savedFixed = fixedW; int savedRear = rear.Count;
                string swapNote = "";
                if (occ != null && !merge)
                {
                    float ow = occ.GetWeight();
                    fixedW -= row0Feather ? ow * c.Factor : ow;
                    cand.Occupant = occ;
                    if (rear.Count < gridSlots) { rear.Add(ow); cand.OccupantToInventory = true; swapNote = "  (swaps out " + NameOf(occ) + " to a free inventory slot)"; }
                    else { swapNote = "  (swaps out " + NameOf(occ) + " into the tombstone)"; }
                }
                if (total() + w <= p.Cap)
                {
                    fixedW += w;
                    p.Hotbar.Add(cand);
                    p.Taken.Add(string.Format("{0,3}. {1} [hotbar] {2:F1} (raw {3:F1}) -> running {4:F1}{5}", ++step, cand.Name, w, cand.W, total(), swapNote));
                }
                else
                {
                    fixedW = savedFixed; while (rear.Count > savedRear) rear.RemoveAt(rear.Count - 1);
                    cand.Occupant = null;
                    p.Left.Add(string.Format("{0} [hotbar] {1:F1}: over the limit", cand.Name, w));
                }
            }

            // Main grid: normal items heaviest first, then low-priority items into the room that is left.
            var mainGroups = new List<List<Cand>> { normal.OrderByDescending(x => x.W).ToList(), lows.OrderByDescending(x => x.W).ToList() };
            foreach (var group in mainGroups)
            {
                foreach (var cand in group)
                {
                    if (rear.Count >= gridSlots) { p.Left.Add(string.Format("{0} [{1}] {2:F1}: no free slot", cand.Name, cand.Kind, cand.W)); continue; }
                    rear.Add(cand.W);
                    float t = total();
                    if (t <= p.Cap)
                    {
                        p.Main.Add(cand);
                        p.Taken.Add(string.Format("{0,3}. {1} [{2}] {3:F1} -> running {4:F1}", ++step, cand.Name, cand.Kind, cand.W, t));
                    }
                    else
                    {
                        rear.RemoveAt(rear.Count - 1);
                        p.Left.Add(string.Format("{0} [{1}] {2:F1}: would reach {3:F1} (> {4:F1})", cand.Name, cand.Kind, cand.W, t, p.Cap));
                    }
                }
            }
            p.FinalSim = total();
            return p;
        }

        private static void LogPlan(Plan p)
        {
            var sb = new StringBuilder();
            sb.AppendLine(p.Header);
            sb.AppendLine("  " + p.Calib);
            sb.AppendLine("  WOULD TAKE (in order):");
            foreach (var l in p.Taken) sb.AppendLine("    " + l);
            sb.AppendLine("  WOULD LEAVE IN THE TOMBSTONE:");
            if (p.Left.Count == 0) sb.AppendLine("    (nothing)");
            foreach (var l in p.Left) sb.AppendLine("    " + l);
            sb.Append(string.Format("  Simulated weight after pickup (heaviest stacks in the {0} feather slots): {1:F1} / {2:F1}", p.FeatherSlots, p.FinalSim, p.Cap));
            foreach (var line in sb.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                _inst.Logger.LogInfo(line);
        }

        // ---------- live execution ----------

        private static void Log(string s) { _inst.Logger.LogInfo("LIVE: " + s); }

        // Pack every item in the main grid (rows 1..H-1) heaviest first into the lightened rows, closing gaps.
        private static void Compact(Inventory inv, int H, int W)
        {
            var items = inv.GetAllItems().Where(i => i.m_gridPos.y >= 1 && i.m_gridPos.y < H).OrderByDescending(i => i.GetWeight()).ToList();
            var pos = GridPositions(H, W);
            for (int i = 0; i < items.Count && i < pos.Count; i++) items[i].m_gridPos = pos[i];
            InvChanged(inv);
        }

        private static bool FindFreeMainSlot(Inventory inv, int H, int W, out Vector2i free)
        {
            foreach (var p in GridPositions(H, W))
                if (inv.GetItemAt(p.x, p.y) == null) { free = p; return true; }
            free = new Vector2i(-1, -1);
            return false;
        }

        private static IEnumerator Execute(Plan plan, TombStone ts, Player player, Container container)
        {
            _running = true;
            float t0 = Time.realtimeSinceStartup;
            try
            {
                var nview = container.GetComponent<ZNetView>();
                float until = Time.time + 4f;
                while (Time.time < until && !(nview != null && nview.IsValid() && nview.IsOwner())) yield return null;
                if (nview == null || !nview.IsValid() || !nview.IsOwner())
                {
                    Log("the tombstone was not owned by this client within 4 s; nothing moved.");
                    yield break;
                }
                float waited = Time.realtimeSinceStartup - t0;
                var inv = plan.C.Inv; var tomb = plan.C.Tomb; int H = plan.H, W = plan.W;
                float before = inv.GetTotalWeight();
                int taken = 0;
                var movedClones = new List<KeyValuePair<ItemDrop.ItemData, bool>>(); // clone in the player's inventory, is low priority

                // Gear and slot items: into their own (empty) slots.
                foreach (var cand in plan.Gear.Concat(plan.SlotItems))
                {
                    var it = cand.It;
                    if (!tomb.ContainsItem(it)) continue;
                    var pos = it.m_gridPos; int stack = it.m_stack;
                    bool ok = inv.MoveItemToThis(tomb, it, stack, pos.x, pos.y);
                    Log(string.Format("{0} [{1}] -> slot {2},{3}: {4}", cand.Name, cand.Kind, pos.x, pos.y, ok ? "moved" : "FAILED"));
                    if (ok) taken++;
                }

                // Hotbar: swap out any different item, then move back.
                foreach (var cand in plan.Hotbar)
                {
                    var it = cand.It;
                    if (!tomb.ContainsItem(it)) continue;
                    var pos = it.m_gridPos; int stack = it.m_stack;
                    var occ = inv.GetItemAt(pos.x, 0);
                    if (occ != null && !(occ.IsSameType(it) && it.m_shared.m_maxStackSize > 1))
                    {
                        Vector2i free;
                        if (cand.OccupantToInventory && FindFreeMainSlot(inv, H, W, out free))
                        {
                            occ.m_gridPos = free; InvChanged(inv);
                            Log(string.Format("hotbar {0}: {1} moved to inventory slot {2},{3}", pos.x + 1, NameOf(occ), free.x, free.y));
                        }
                        else
                        {
                            tomb.MoveItemToThis(inv, occ); InvChanged(inv);
                            Log(string.Format("hotbar {0}: {1} moved into the tombstone", pos.x + 1, NameOf(occ)));
                        }
                    }
                    bool ok = inv.MoveItemToThis(tomb, it, stack, pos.x, 0);
                    Log(string.Format("{0} [hotbar] -> slot {1}: {2}", cand.Name, pos.x + 1, ok ? "moved" : "FAILED"));
                    if (ok) taken++;
                }

                // Main grid: lay out existing + incoming items heaviest first, then move the incoming ones in.
                var incoming = plan.Main.Where(cd => tomb.ContainsItem(cd.It)).ToList();
                var existing = inv.GetAllItems().Where(i => i.m_gridPos.y >= 1 && i.m_gridPos.y < H).ToList();
                var positions = GridPositions(H, W);
                var combined = new List<KeyValuePair<ItemDrop.ItemData, Cand>>();
                foreach (var e in existing) combined.Add(new KeyValuePair<ItemDrop.ItemData, Cand>(e, null));
                foreach (var cd in incoming) combined.Add(new KeyValuePair<ItemDrop.ItemData, Cand>(cd.It, cd));
                combined = combined.OrderByDescending(k => k.Key.GetWeight()).ToList();
                var assign = new Dictionary<ItemDrop.ItemData, Vector2i>();
                for (int i = 0; i < combined.Count && i < positions.Count; i++) assign[combined[i].Key] = positions[i];
                foreach (var e in existing) if (assign.ContainsKey(e)) e.m_gridPos = assign[e];
                InvChanged(inv);
                foreach (var cd in incoming)
                {
                    if (!assign.ContainsKey(cd.It)) { Log(cd.Name + ": no layout slot, left in the tombstone"); continue; }
                    var pos = assign[cd.It]; int stack = cd.It.m_stack;
                    bool ok = inv.MoveItemToThis(tomb, cd.It, stack, pos.x, pos.y);
                    var clone = inv.GetItemAt(pos.x, pos.y);
                    Log(string.Format("{0} [{1}] -> grid {2},{3}: {4}", cd.Name, cd.Kind, pos.x, pos.y, ok ? "moved" : "FAILED"));
                    if (ok && clone != null) { taken++; movedClones.Add(new KeyValuePair<ItemDrop.ItemData, bool>(clone, cd.Low)); }
                }
                Compact(inv, H, W);

                // Verify against the real max; undo the last items (low priority first) if over.
                float real = inv.GetTotalWeight();
                var undo = movedClones.Where(k => k.Value).OrderByDescending(k => k.Key.GetWeight())
                    .Concat(movedClones.Where(k => !k.Value).Reverse()).Select(k => k.Key).ToList();
                int undone = 0;
                while (real > plan.Cap + 0.05f && undo.Count > 0)
                {
                    var it = undo[0]; undo.RemoveAt(0);
                    if (!inv.ContainsItem(it)) continue;
                    Log(string.Format("real weight {0:F1} is over {1:F1}: moving {2} back to the tombstone", real, plan.Cap, NameOf(it)));
                    tomb.MoveItemToThis(inv, it);
                    undone++; taken--;
                    Compact(inv, H, W);
                    real = inv.GetTotalWeight();
                }

                int leftCount = tomb.GetAllItems().Count;
                Log(string.Format("done. Moved {0} items ({1} undone), {2} left in the tombstone. Weight {3:F1} -> {4:F1} / {5:F1} (planned {6:F1}). Took {7:F2} s in total ({8:F2} s waiting for ownership, {9:F2} s moving).",
                    taken, undone, leftCount, before, inv.GetTotalWeight(), plan.Cap, plan.FinalSim, Time.realtimeSinceStartup - t0, waited, Time.realtimeSinceStartup - t0 - waited));

                if (leftCount == 0 && _tsOnTakeAll != null)
                {
                    // Everything was taken: finish like a vanilla full recovery (pickup effect, "recovered" message and
                    // ExtraSlots' auto-equip, which hooks this call), then close the window.
                    _tsOnTakeAll.Invoke(ts, null);
                    if (InventoryGui.instance != null) InventoryGui.instance.Hide();
                    Log("everything recovered: window closed.");
                }
                else
                {
                    // Equip with ExtraSlots' own routine (follows its auto-equip settings); the window stays open.
                    if (_autoEquip != null)
                    {
                        var routine = _autoEquip.Invoke(null, null) as IEnumerator;
                        if (routine != null) _inst.StartCoroutine(routine);
                    }
                    player.Message(MessageHud.MessageType.Center, string.Format("Picked up what fits: {0} items taken, {1} left in the grave", taken, leftCount));
                }
            }
            finally
            {
                _running = false;
            }
        }
    }
}

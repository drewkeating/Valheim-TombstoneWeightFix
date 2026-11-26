# TombstoneWeightFix

A fix and an upgrade for recovering your tombstone when you run **ExtraSlots** with its lightened (weight-reducing) inventory rows.

## The problem
When you walk up to your own tombstone, the game checks whether everything fits and, if so, loots it all at once. Otherwise the tombstone opens like a chest and you have to move everything by hand. With ExtraSlots that check can say "no" when it shouldn't:

1. **The lightened-row discount is ignored for the tombstone.** ExtraSlots reduces the weight of items in your lightened rows, but only inside your own inventory. The tombstone's items are counted at full weight, so heavy loot that was cheap to carry looks too heavy to recover.
2. **Belt upgrades are undercounted.** The check adds the carry bonus of a belt it is about to equip by reading the item's own buff, which has no wearer. Mods that add to a belt's bonus based on its level (such as OtMegingjord) are not counted.
3. **The temporary Corpse Run buff is counted as part of your max.** A tombstone can pass the check thanks to the buff, then leave you over your real limit once the buff ends.

## What this mod does
### 1. A correct auto-loot check
It redoes the weight comparison with the lightened-row discount applied to the tombstone's items, the real belt upgrade bonus (OtMegingjord, if installed), and your **settled** max carry weight, which is your max without the Corpse Run buff. A tombstone that fits is auto-looted as before. One that is too heavy opens as a chest. It never turns a "no" caused by slots not fitting into a "yes".

### 2. A sorted pickup for tombstones that don't fit
When your own tombstone opens as a chest, the mod plans and carries out a pickup that takes what fits under your carry weight and leaves the rest in the tombstone:

1. **Gear first:** your belt first (so your max rises), then armor, cape, circlet and other equipment, then quick, food, ammo and misc slot items. Items go only into empty slots; gear you are wearing is never replaced. ExtraSlots' own auto-equip then equips them.
2. **Hotbar:** items go back to their original slots. If a different item is in a slot now, it is moved to a free inventory slot (or into the tombstone if there is no room).
3. **Everything else, heaviest first,** so the heaviest stacks land in your lightened rows and cost the least.
4. **Low-priority items last,** into whatever room is left (see `Low priority items`). Heavy, easily replaced items such as stone and wood won't push out the things you care about.
5. It checks your real weight afterwards. If it is over your max, items go back into the tombstone, low-priority ones first.
6. Your main inventory is packed so the heaviest stacks sit in the lightened rows.

If everything is taken, the window closes and you get the usual "recovered" message. If something is left, the window stays open so you can see what.

The whole transfer happens at once. The weight and the lightened rows are read from ExtraSlots' live values, so progression (rows unlocking by boss, lightened slots activating) is respected.

## Config (`scooby_drew902.valheim.tombstoneweightfix.cfg`)
- `[General] Enabled` (true): turns the corrected auto-loot check on or off.
- `[General] LogDetails` (false): logs the numbers behind each tombstone check.
- `[Smart pickup] Enabled` (true): turns the sorted pickup on or off.
- `[Smart pickup] DryRun` (false): true = only write the plan to the log and move nothing. Useful to see what it would do.
- `[Smart pickup] Low priority items` (`Stone, Wood, FineWood, CoreWood`): comma-separated prefab names or item names (for example `$item_stone`) that are taken last.

## Requirements and notes
- **Client side only.** The server needs nothing from this mod. Install it for each player who wants it.
- Requires **ExtraSlots**. Tested with ExtraSlots 1.2.24 and Valheim 1.0.30.
- The mod reaches into ExtraSlots (and OtMegingjord, if present) by reflection. If a future version changes the parts it uses, the mod writes a warning to the BepInEx log and does nothing, instead of breaking the game.
- Only your own tombstone is touched.
- Not modelled: items joining stacks you already carry (they take their own slot), and items that ExtraSlots holds back for mods that are not loaded.
- Look for `Tombstone Weight Fix 1.0.0 loaded.` in `BepInEx/LogOutput.log` to see that it is running. Each smart pickup writes a plan and `LIVE:` lines to the log.

## Credits
ExtraSlots is by shudnal, OtMegingjord by OtMods. This mod contains none of their code.

## License
GPL-3.0. Source code is linked from this mod's website.

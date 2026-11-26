# Changelog

## 1.0.0
- First release.
- Corrected tombstone auto-loot check for ExtraSlots: applies the lightened-row discount to the tombstone's items, counts a belt's real upgrade bonus (OtMegingjord, if installed) and compares against your settled max carry weight (without the Corpse Run buff).
- Sorted pickup for tombstones that are too heavy: gear first (belt first), hotbar back in place (swapping out whatever is there now), then the heaviest items first, low-priority items last, up to your real carry weight. The rest stays in the tombstone. The window closes if everything was taken.
- Config: `Enabled`, `LogDetails`, `Smart pickup / Enabled`, `Smart pickup / DryRun`, `Smart pickup / Low priority items`.

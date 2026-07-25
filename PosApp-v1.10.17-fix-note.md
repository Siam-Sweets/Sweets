# PosApp v1.10.17 fix note

## Fixed: missing Items Sold after cloud restore

Cloud restore previously downloaded the latest full snapshot and stopped at the snapshot cursor. Any sale-line records synchronized after that baseline were left for a later background sync, which could make restored Sales History rows show `0` in **Items Sold** while the sale header and totals were already visible.

v1.10.17 now replays all incremental cloud changes after every restored snapshot cursor before restore is reported as complete. This restores sale items and other post-snapshot dependent records together with their parent sales.

The existing pre-restore SQLite safety backup and foreign-key validation remain enabled.

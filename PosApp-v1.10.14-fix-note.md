# PosApp v1.10.14 Fix Note

## Fixed: synchronized records still counted as conflicts

The reported status showed `Pending: 0` and `Conflicts: 22`. Those values identify stale conflict diagnostics, not 22 unsent edits.

An earlier partial Worker push could create cloud revisions before the request failed. On retry, PosApp recorded conflicts. The following pull then recognized the revisions as originating from the same Windows device, updated the local cloud versions, and removed the pending rows—but the old conflict rows were not marked resolved.

v1.10.14 closes a conflict when:

- The related push is accepted.
- This device pulls its own accepted revision.
- A previously quarantined cloud revision is later applied successfully.
- An existing conflict has no queued local edit and the local record already contains the same or a newer cloud revision.

This is deliberately not a blanket “use local” or “use cloud” operation. A conflict with a pending edit or a lower local revision remains unresolved in Sync Center.

## Recover the existing 22 records

Install the v1.10.14 Windows build, then refresh **Settings → Cloud** or press **Sync Now** once. The background coordinator also runs automatically after startup.

Keep the existing local `posapp.db` and Turso database. No SQLite or Turso schema migration is required.

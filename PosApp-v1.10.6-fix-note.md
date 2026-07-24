# PosApp v1.10.6 Fix Note

## Fixed: Store Details dialog did not scroll

The Store Details editor used a fixed-height form with no `ScrollViewer`. At Windows display scaling or on a smaller screen, the Phone field and lower content could be clipped with no way to reach them.

### Changes

- Added vertical scrolling to the form body.
- Made the dialog resizable.
- Added safe minimum width and height.
- Kept Save and Cancel fixed at the bottom.
- Added an internal vertical scrollbar to the Address field.

## Upgrade

Install v1.10.6 over the existing installation. Keep `posapp.db`; no database migration or reset is required. Cloud Worker redeployment is optional because the sync protocol did not change.

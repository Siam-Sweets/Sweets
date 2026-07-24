# PosApp v1.10.4 Fix Note

## Fixed: category windows did not scroll reliably

The Category Management grid now has explicit vertical and horizontal scrolling. The category editor is resizable and uses a vertical scroll viewer, so the color field, preview, and Save/Cancel buttons remain reachable on smaller displays and high Windows scaling.

## Fixed: category colors were not visible

The category editor now shows a live preview of `#RRGGBB` values. Category Management displays a color swatch beside the hexadecimal value. The POS category filter and product cards also display the saved category color. Invalid values show a neutral/error preview until corrected and remain rejected by the existing save validation.

## Upgrade

Install v1.10.4 over the existing installation. Keep `posapp.db`; no database migration or reset is required. Worker redeployment is optional because the cloud protocol did not change.

# PosApp v1.10.5 Fix Note

## Fixed: dark mode windows still showed white surfaces

Dark mode now reapplies PosApp theme brushes to every WPF window, including programmatically created dialogs such as **Close Register**, **Add Supplier**, **New Purchase**, **Category dialogs**, and similar management popups. Their client areas now use the same dark surfaces as the rest of the app.

## Fixed: Windows title bars stayed bright in dark mode

Main windows and modal dialogs now request immersive dark title bars on supported Windows versions. This removes the bright caption-bar strip that remained visible even when the application theme was set to Dark.

## Upgrade

Install v1.10.5 over the existing installation. Keep `posapp.db`; no database migration or reset is required. Worker redeployment is optional because the cloud protocol did not change.

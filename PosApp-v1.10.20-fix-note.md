# PosApp v1.10.20 Fix Note

## Fixed

- Added a mouse-draggable divider between the main receipt area and the right-side POS command panel.
- The right-side panel can now be resized by dragging the vertical splitter.
- The product-search overlay was updated to span the new splitter layout correctly.

## Validation

- Checked the POS XAML layout statically after the column change.
- Local compilation was not run in this workspace because the .NET SDK is not installed.

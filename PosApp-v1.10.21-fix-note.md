# PosApp v1.10.21 Fix Note

## Fixed

- Cost Price is now optional when adding or editing a product.
- Leaving Cost Price blank saves it as `0`.
- Negative Cost Price values are still blocked.
- The Cost Price label now shows that the field is optional.

## Validation

- Checked the edited source paths statically.
- Local compilation was not run in this workspace because the .NET SDK is not installed.

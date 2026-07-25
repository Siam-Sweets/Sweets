namespace PosApp.Wpf.Helpers;

/// <summary>
/// Lightweight display/value pair used by store filter ComboBoxes.
/// Kept independent from inventory-transfer UI so Dashboard and Reports
/// continue to work when Stock Transfers is not part of the application.
/// </summary>
public sealed record StoreFilterOption(int Id, string Name);

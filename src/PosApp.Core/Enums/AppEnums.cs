namespace PosApp.Core.Enums;

/// <summary>App-wide UI language codes.</summary>
public enum AppLanguage
{
    English = 0,
    Bengali = 1
}

/// <summary>Theme mode for the WPF shell.</summary>
public enum AppTheme
{
    Light = 0,
    Dark = 1
}

/// <summary>Field used when filtering the POS product catalog.</summary>
public enum ProductSearchField
{
    All = 0,
    Name = 1,
    Code = 2,
    Barcode = 3
}

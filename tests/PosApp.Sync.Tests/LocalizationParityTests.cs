using System.Xml.Linq;

namespace PosApp.Sync.Tests;

public sealed class LocalizationParityTests
{
    [Fact]
    public void EnglishAndBengaliResourceKeysHaveExactParity()
    {
        var english = Keys(Path.Combine(AppContext.BaseDirectory, "Localization", "Strings.en.xaml"));
        var bengali = Keys(Path.Combine(AppContext.BaseDirectory, "Localization", "Strings.bn.xaml"));
        Assert.Empty(english.Except(bengali));
        Assert.Empty(bengali.Except(english));
        Assert.Equal(english.Count, bengali.Count);
    }

    private static HashSet<string> Keys(string path)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Descendants()
            .Select(value => (string?)value.Attribute(x + "Key"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>().ToHashSet(StringComparer.Ordinal);
    }
}

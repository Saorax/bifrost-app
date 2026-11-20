using Avalonia.Controls;

namespace BifrostStudio;

public sealed record PageInfo(string Key, string Title, string Icon, Func<Control> Make);

public sealed class PageRegistry
{
    readonly List<PageInfo> pages = [];

    public IReadOnlyList<PageInfo> All => pages;

    public PageRegistry Add(string key, string title, string icon, Func<Control> make)
    {
        if (pages.Any(p => p.Key == key)) throw new InvalidOperationException($"Page {key} is registered twice.");
        pages.Add(new PageInfo(key, title, icon, make));
        return this;
    }

    public PageInfo Find(string key) => pages.FirstOrDefault(p => p.Key == key) ?? pages[0];
}
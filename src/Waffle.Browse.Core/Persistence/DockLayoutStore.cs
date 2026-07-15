using System.Text.Json;
using Waffle.Browse.Core.Docking;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.Core.Persistence;

public sealed class DockLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string filePath;

    public DockLayoutStore(string filePath)
    {
        this.filePath = filePath;
    }

    public void Save(DockLayoutState state)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    public DockLayoutState LoadOrDefault(string fallbackPath, Func<string, bool>? isPathAvailable = null)
    {
        var service = new DockLayoutService();
        if (!File.Exists(filePath))
        {
            return service.CreateDefault(fallbackPath);
        }

        try
        {
            var state = JsonSerializer.Deserialize<DockLayoutState>(File.ReadAllText(filePath), JsonOptions);
            return state is null
                ? service.CreateDefault(fallbackPath)
                : NormalizeForRestore(state, fallbackPath, isPathAvailable ?? Directory.Exists);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return service.CreateDefault(fallbackPath);
        }
    }

    public static DockLayoutState NormalizeForRestore(
        DockLayoutState state,
        string fallbackPath,
        Func<string, bool>? isPathAvailable = null)
    {
        var availability = isPathAvailable ?? Directory.Exists;
        var panels = state.Panels.Select(panel =>
        {
            var tabs = panel.Tabs.Select(tab =>
            {
                return tab.LocationKind == TabLocationKind.Search
                    ? NormalizeSearchTab(tab, fallbackPath, availability)
                    : NormalizeFolderTab(tab, fallbackPath, availability);
            }).ToList();

            return panel with { Tabs = tabs };
        }).ToList();

        var normalized = new DockLayoutService().Normalize(state with { Panels = panels }, fallbackPath);
        if (normalized.VisiblePanels.Count == 0)
        {
            return normalized with { Grid = null };
        }

        return normalized.Grid is null
            ? normalized with { Grid = CreateGridFromVisiblePanels(normalized) }
            : normalized;
    }

    private static TabState NormalizeFolderTab(TabState tab, string fallbackPath, Func<string, bool> availability)
    {
        var path = IsAvailable(tab.CurrentPath, availability) ? tab.CurrentPath : fallbackPath;
        return tab with
        {
            CurrentPath = path,
            Title = DockLayoutService.CreateTitle(path),
            LocationKind = TabLocationKind.Folder,
            SearchQuery = null,
            SearchOriginPath = null,
            SearchScope = SearchScope.GlobalIndex,
            SearchRoots = [],
            BackStack = tab.BackStack.Where(pathInHistory => IsHistoryAvailable(pathInHistory, availability)).ToList(),
            ForwardStack = tab.ForwardStack.Where(pathInHistory => IsHistoryAvailable(pathInHistory, availability)).ToList()
        };
    }

    private static TabState NormalizeSearchTab(TabState tab, string fallbackPath, Func<string, bool> availability)
    {
        var query = tab.SearchQuery?.Trim();
        var roots = tab.SearchRoots
            .Where(root => IsAvailable(root, availability))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var originPath = !string.IsNullOrWhiteSpace(tab.SearchOriginPath) && IsAvailable(tab.SearchOriginPath, availability)
            ? tab.SearchOriginPath
            : roots.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(originPath))
        {
            return NormalizeFolderTab(tab, fallbackPath, availability);
        }

        var scope = tab.CurrentPath.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase)
            ? SearchScope.CurrentFolder
            : tab.SearchScope;
        var rootPath = scope == SearchScope.CurrentFolder ? roots.FirstOrDefault() ?? originPath : null;

        return tab with
        {
            CurrentPath = EverythingSearchLocation.Build(query, scope, rootPath),
            Title = DockLayoutService.CreateSearchTitle(query),
            LocationKind = TabLocationKind.Search,
            SearchQuery = query,
            SearchOriginPath = originPath,
            SearchScope = scope,
            SearchRoots = rootPath is null ? [] : [rootPath],
            BackStack = tab.BackStack.Where(pathInHistory => IsHistoryAvailable(pathInHistory, availability)).ToList(),
            ForwardStack = tab.ForwardStack.Where(pathInHistory => IsHistoryAvailable(pathInHistory, availability)).ToList()
        };
    }

    private static DockGridState CreateGridFromVisiblePanels(DockLayoutState state)
    {
        var visiblePanels = state.Panels.Where(panel => panel.IsVisible).ToList();
        if (visiblePanels.Count == 0)
        {
            var firstPanel = state.Panels.First();
            return DockGridState.Single(firstPanel.Id);
        }

        var service = new DockGridService();
        var grid = DockGridState.Single(visiblePanels[0].Id);

        if (visiblePanels.Count >= 2)
        {
            var direction = state.LayoutKind == DockLayoutKind.TwoByOne
                ? DockDirection.Bottom
                : DockDirection.Right;
            grid = service.Split(grid, visiblePanels[0].Id, visiblePanels[1].Id, direction);
        }

        if (visiblePanels.Count >= 3)
        {
            grid = service.Split(grid, visiblePanels[1].Id, visiblePanels[2].Id, DockDirection.Bottom);
        }

        if (visiblePanels.Count >= 4)
        {
            grid = service.Split(grid, visiblePanels[2].Id, visiblePanels[3].Id, DockDirection.Right);
        }

        return grid;
    }

    private static bool IsAvailable(string path, Func<string, bool> isPathAvailable)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return isPathAvailable(path);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsHistoryAvailable(string value, Func<string, bool> isPathAvailable)
    {
        return EverythingSearchLocation.TryParse(value, out _) || IsAvailable(value, isPathAvailable);
    }
}

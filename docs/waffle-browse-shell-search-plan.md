# Waffle Browse Shell Search Plan

## Goal

Replace the current WPF bottom search-result list with Windows Explorer-style search inside the active `IExplorerBrowser` tab.

The intended behavior should match Windows File Explorer closely:

- Running search changes the current tab from a folder view to a Shell search results view.
- Back returns to the folder that was searched.
- Clearing the search text while the tab is showing search results returns to the original folder, like Back.
- Search results are displayed by the hosted Shell view, not by a WPF `DataGrid`.

## Previous State

Before this plan, the app searched by recursively enumerating files with `FileSearchService`.

Previous flow:

```text
QuickSearchBox
  -> FileSearchService.Search(...)
  -> SearchResultsPanel.UpdateResults(...)
  -> WPF DataGrid at the bottom of MainWindow
```

This gives Waffle Browse full control over the result list, but it does not behave like Windows File Explorer. Icons, Shell columns, context menus, search result navigation, and Explorer search semantics must be recreated or approximated.

## Target State

The target flow uses a Shell search location:

```text
QuickSearchBox
  -> Resolve search roots
  -> Build search metadata
  -> DockLayoutService.NavigateToSearch(...)
  -> ShellExplorerHost.NavigateToSearch(...)
  -> ISearchFolderItemFactory creates a Shell search folder object
  -> IExplorerBrowser.BrowseToObject(...)
  -> IExplorerBrowser displays the Shell search results view
```

`SearchResultsPanel` and its splitter become unnecessary for the primary search path.

## Search Location Strategy

Do not activate `search-ms:` as a Shell protocol target from the hosted browser. Directly browsing a `search-ms:` PIDL can delegate the search to Windows File Explorer and open a separate Explorer search window.

Use two separate representations:

- Persist a `search-ms:` URI in tab state as a stable search target string for history, restore, and debugging.
- Render the actual in-app view by creating a Shell search folder object through `ISearchFolderItemFactory` and passing that object to `IExplorerBrowser.BrowseToObject`.

Example shape:

```text
search-ms:query=report&crumb=location:C%3A%5CWork,include,recursive
```

For multiple roots, append one location crumb per root.

Do not navigate the app to generated `.search-ms` saved search files. On Windows this can spawn a separate File Explorer search window, which violates the requirement that search runs only inside Waffle Browse.

If the Shell search folder object cannot be created or `IExplorerBrowser.BrowseToObject` rejects it, keep the current tab in place and show a status message instead of opening an external Explorer window.

Implementation details:

- Build the scope with `SHCreateShellItemArrayFromIDLists`.
- Build the condition with `IConditionFactory.MakeLeaf`.
- Use `System.ItemNameDisplay` for the name condition.
- Use `COP_DOSWILDCARDS` for queries containing `*` or `?`; otherwise use `COP_VALUE_CONTAINS`.
- Keep COM interface IIDs and method order aligned with the Windows SDK headers. A wrong vtable order can create a Shell search folder that navigates but does not show the intended results.

## Tab State

Add explicit location kind metadata to tabs.

```csharp
public enum TabLocationKind
{
    Folder,
    Search
}

public sealed record TabState
{
    public Guid Id { get; init; }
    public string Title { get; init; }
    public string CurrentPath { get; init; }
    public TabLocationKind LocationKind { get; init; }
    public string? SearchQuery { get; init; }
    public string? SearchOriginPath { get; init; }
    public List<string> SearchRoots { get; init; }
    public List<string> BackStack { get; init; }
    public List<string> ForwardStack { get; init; }
}
```

Rules:

- Folder tabs use `LocationKind.Folder`.
- Search tabs use `LocationKind.Search`.
- `CurrentPath` stores the actual Shell navigation target.
- For folder tabs, `CurrentPath` is a folder path such as `C:\Work`.
- For search tabs, `CurrentPath` is the persisted `search-ms:` URI. It is not directly browsed by `IExplorerBrowser`.
- `SearchOriginPath` stores the folder that should be restored when the search text is cleared.
- `SearchRoots` stores the folder scopes used to build the Shell search target.

## Search Execution Behavior

When the user runs a search:

1. Trim the query.
2. If the query is empty, apply the clear-search behavior below.
3. Resolve search roots from the selected scope.
4. Determine the origin path:
   - If the active tab is a folder tab, use its current folder path.
   - If the active tab is already a search tab, reuse `SearchOriginPath`.
5. Build the Shell search target from the query and roots.
6. Update the active tab to `LocationKind.Search`.
7. Set `Title` to `검색: {query}`.
8. Push the origin folder into `BackStack` so Back returns to it.
9. Navigate the Shell host with `NavigateToSearch(searchTarget, query, roots)`.
10. The Shell host creates a SearchFolder object and calls `BrowseToObject`, so the results stay inside Waffle Browse.

## Clear Search Behavior

When the search box becomes empty and the active tab is a search tab, restore immediately without requiring Enter or the search button:

1. Read `SearchOriginPath`.
2. If it is a valid folder path, navigate the active tab back to that folder.
3. Change `LocationKind` back to `Folder`.
4. Clear `SearchQuery`, `SearchOriginPath`, and `SearchRoots`.
5. Keep normal Back/Forward history coherent.

This mirrors the observed Windows File Explorer behavior: clearing the search text behaves like returning to the folder that was searched.

If the active tab is not a search tab, clearing the search box should only clear the input.

## Back And Forward Behavior

Back from a search tab should return to `SearchOriginPath`.

Implementation options:

- Store the origin path in `BackStack` exactly as a normal folder navigation entry.
- Also keep `SearchOriginPath` so clearing the search box can restore without depending on stack shape.

Forward after returning from a search can restore the search target if the existing history model supports it cleanly. If that adds complexity, initial implementation may clear the search forward entry on explicit clear-search while preserving normal Back behavior.

## Search Result Navigation

When the user double-clicks or opens a folder from the Shell search results:

- `IExplorerBrowser` navigation events should update the tab.
- If the Shell reports a real folder path, the tab becomes `LocationKind.Folder`.
- Search metadata is cleared.

When the Shell reports another search location, the tab remains `LocationKind.Search`.

## Restore Behavior

`DockLayoutStore.NormalizeForRestore` must not treat `search-ms:` as an unavailable folder.

Restore rules:

- Folder tabs still validate with `Directory.Exists`.
- Search tabs rebuild their Shell search target from `SearchQuery` and `SearchRoots`.
- If no search root is available, convert the tab to a folder tab at the fallback path.
- Never restore by opening a generated `.search-ms` file.
- Search tab restore must call `ShellExplorerHost.NavigateToSearch`, not `ShellExplorerHost.Navigate`.

## UI Changes

Remove or hide the bottom search results panel for the Shell search path:

- `SearchResultsPanel.xaml`
- `SearchResultsPanel.xaml.cs`
- bottom `GridSplitter`
- `SearchResultsRow`
- `FileSearchService` usage from quick search execution

Keep the top search box and scope selector.

Search box behavior:

- Enter or the search button runs Shell search.
- Empty text on a search tab returns to the search origin folder immediately.
- Empty text on a folder tab does not navigate.

## Error Handling

Handle these cases without crashing:

- Empty search query.
- No active or visible panel.
- No valid search roots.
- `search-ms:` parsing fails.
- `BrowseToIDList` rejects the Shell search target.
- A search root no longer exists on restore.

Fallback order:

1. Try to create the Shell search folder object.
2. Stay on the current folder and show a status message.
3. Do not generate or navigate to `.search-ms` fallback files from the app search flow.
4. Do not shell-execute `search-ms:` or `.search-ms` from Waffle Browse.

## Test Plan

Core tests:

- `WindowsSearchLocationBuilder` encodes query text.
- `WindowsSearchLocationBuilder` creates a single-root location crumb.
- `WindowsSearchLocationBuilder` creates multi-root location crumbs.
- `DockLayoutService.NavigateToSearch` changes the active tab to `LocationKind.Search`.
- `NavigateToSearch` stores `SearchOriginPath`.
- `NavigateToSearch` pushes the origin folder into `BackStack`.
- Clearing search restores `SearchOriginPath`.
- Restore normalization preserves valid search tabs.
- Restore normalization converts invalid search tabs to fallback folders.

App-level verification:

- `dotnet run --project tests/Waffle.Browse.Core.Tests/Waffle.Browse.Core.Tests.csproj --no-restore`
- `dotnet run --project tests/Waffle.Browse.App.Tests/Waffle.Browse.App.Tests.csproj --no-restore`
- `dotnet build waffle-browse.slnx --no-restore`
- Windows UI smoke against the hosted `IExplorerBrowser`:
  - `SHELL_SEARCH_RESULT_RENDERED Waffle.Browse.App.csproj UIItem`
  - `BACK_RESTORED_ORIGIN E:\Project\waffle-browse\src\Waffle.Browse.App`
  - `CLEAR_SEARCH_RESTORED_ORIGIN E:\Project\waffle-browse\src\Waffle.Browse.App`
  - `CONTEXT_MENU_CONFIRMED_NATIVE_32768`
  - `FOLDER_RESULT_OPENED_AS_FOLDER ...\Controls`
  - No additional File Explorer search window is opened when starting a search.
- Regression smoke for the external-window bug:
  - Restoring a saved search tab does not create a new Explorer top-level window.
  - Running a new search does not create a new Explorer top-level window.
  - A known result appears as a UI item under the Waffle Browse window.
- Windows hosted-Shell verification checklist:
  - Search in current panel shows results inside the Shell view.
  - Back returns to the folder.
  - Clearing the search box returns to the folder.
  - Shell context menu works on search results.
  - Opening a folder from search results turns the tab back into a normal folder tab.

## Implementation Status

- [x] Add tab location metadata.
- [x] Add Shell search target builder.
- [x] Add `DockLayoutService.NavigateToSearch` and clear-search restore behavior.
- [x] Update restore normalization for search tabs.
- [x] Change quick search execution to navigate the active Shell tab.
- [x] Remove the WPF bottom search results panel from the primary UI.
- [x] Add tests for builder, tab state transitions, and restore behavior.
- [x] Build and run automated tests.
- [x] Disable generated `.search-ms` fallback so search stays inside Waffle Browse and does not spawn File Explorer.
- [x] Route search tabs through `ShellExplorerHost.NavigateToSearch` and `ISearchFolderItemFactory`.
- [x] Verify Shell search result rendering inside the hosted `IExplorerBrowser` on Windows.
- [x] Verify saved search restore and new search do not spawn File Explorer windows.

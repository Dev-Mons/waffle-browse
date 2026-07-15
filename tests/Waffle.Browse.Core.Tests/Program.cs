using Waffle.Browse.Core.Tests.Docking;
using Waffle.Browse.Core.Tests.Navigation;
using Waffle.Browse.Core.Tests.Persistence;
using Waffle.Browse.Core.Tests.Search;

var tests = new (string Name, Action Run)[]
{
    ("Preset switching creates the expected visible panels", DockLayoutServiceTests.PresetSwitchingCreatesExpectedVisiblePanels),
    ("Panel paths stay separated across layout changes", DockLayoutServiceTests.PanelPathsStaySeparatedAcrossLayoutChanges),
    ("Four panel preset creates a true 2x2 grid", DockLayoutServiceTests.FourPanelPresetCreatesTrueTwoByTwoGrid),
    ("Docking tabs creates layouts and refuses a fifth panel", DockLayoutServiceTests.DockingTabsCreatesLayoutsAndRefusesFifthPanel),
    ("Edge docking from one panel creates horizontal and vertical splits", DockLayoutServiceTests.EdgeDockingFromOnePanelCreatesHorizontalAndVerticalSplits),
    ("Moving tabs to center preserves target panel state", DockLayoutServiceTests.MovingTabsToCenterPreservesTargetPanelState),
    ("Center drop of the last source tab removes the empty panel", DockLayoutServiceTests.CenterDropOfLastSourceTabRemovesEmptyPanel),
    ("Center drops can collapse from 2x2 back to 1x1", DockLayoutServiceTests.CenterDropsCanCollapseFromTwoByTwoBackToOneByOne),
    ("Moving tabs within a panel reorders tab state", DockLayoutServiceTests.MovingTabsWithinPanelReordersTabState),
    ("Panel model does not expose rename surface", DockLayoutServiceTests.PanelModelDoesNotExposeRenameSurface),
    ("Activating panel updates active panel without changing tabs", DockLayoutServiceTests.ActivatingPanelUpdatesActivePanelWithoutChangingTabs),
    ("Navigate to search turns active tab into search location", DockLayoutServiceTests.NavigateToSearchTurnsActiveTabIntoSearchLocation),
    ("Clearing search restores origin folder", DockLayoutServiceTests.ClearingSearchRestoresOriginFolder),
    ("Search navigation supports back and forward", DockLayoutServiceTests.SearchNavigationSupportsBackAndForward),
    ("Closing the last tab removes its panel from layout", DockLayoutServiceTests.ClosingLastTabRemovesPanelFromLayout),
    ("Closing every panel creates empty layout", DockLayoutServiceTests.ClosingEveryPanelCreatesEmptyLayout),
    ("Navigation history supports back forward and parent", DockLayoutServiceTests.NavigationHistorySupportsBackForwardAndParent),
    ("Navigate up from drive root opens This PC", DockLayoutServiceTests.NavigateUpFromDriveRootOpensThisPc),
    ("Dock grid derives layout kind from leaf tree", DockGridStateTests.DockGridDerivesLayoutKindFromLeafTree),
    ("Dock grid preserves leaf order for rendering", DockGridStateTests.DockGridPreservesLeafOrderForRendering),
    ("Dock grid rejects fifth leaf", DockGridStateTests.DockGridRejectsFifthLeaf),
    ("Dock grid projects normalized leaf bounds", DockGridStateTests.DockGridProjectsNormalizedLeafBounds),
    ("Navigation changes do not require layout render", DockLayoutRenderInvalidationTests.NavigationChangesDoNotRequireLayoutRender),
    ("Visible panel changes require layout render", DockLayoutRenderInvalidationTests.VisiblePanelChangesRequireLayoutRender),
    ("Active panel changes do not require layout render", DockLayoutRenderInvalidationTests.ActivePanelChangesDoNotRequireLayoutRender),
    ("Navigation shortcuts map mouse buttons to history actions", FolderNavigationShortcutTests.MouseButtonsMapToHistoryActions),
    ("Navigation shortcuts map keyboard shortcuts to history actions", FolderNavigationShortcutTests.KeyboardShortcutsMapToHistoryActions),
    ("Folder open target uses Explorer with path argument", FolderOpenTargetTests.FolderOpenTargetUsesExplorerWithPathArgument),
    ("Committing split preview updates grid and panel state", DockLayoutCommitTests.CommittingSplitPreviewUpdatesGridAndPanelState),
    ("Committing split preview rejects last tab split into same panel", DockLayoutCommitTests.CommittingSplitPreviewRejectsLastTabSplitIntoSamePanel),
    ("Committing move preview collapses empty source panel through grid", DockLayoutCommitTests.CommittingMovePreviewCollapsesEmptySourcePanelThroughGrid),
    ("Rejected preview does not change layout state", DockLayoutCommitTests.RejectedPreviewDoesNotChangeLayoutState),
    ("Hit tester returns center move preview outside edge threshold", DockDropHitTesterTests.CenterAreaReturnsMoveIntoPanel),
    ("Hit tester returns split preview at each edge", DockDropHitTesterTests.EdgeAreasReturnSplitPreview),
    ("Hit tester rejects edge split when max panels is reached", DockDropHitTesterTests.EdgeSplitRejectedWhenMaxPanelsReached),
    ("Hit tester treats edge as center when split drag is disabled", DockDropHitTesterTests.EdgeAreaReturnsMoveWhenSplitIsDisabled),
    ("Drop target resolver picks panel containing workspace pointer", DockDropTargetResolverTests.PicksPanelContainingWorkspacePointer),
    ("Drop target resolver returns null outside all panels", DockDropTargetResolverTests.ReturnsNullOutsideAllPanels),
    ("Dock layout store round-trips state", DockLayoutStoreTests.DockLayoutStoreRoundTripsState),
    ("Dock layout store preserves empty layout", DockLayoutStoreTests.DockLayoutStorePreservesEmptyLayout),
    ("Restore normalization falls back for unavailable paths", DockLayoutStoreTests.RestoreNormalizationFallsBackForUnavailablePaths),
    ("Restore creates grid for legacy visible-panel state", DockLayoutStoreTests.RestoreCreatesGridWhenSavedStateHasOnlyVisiblePanels),
    ("Restore normalization rebuilds search tabs", DockLayoutStoreTests.RestoreNormalizationRebuildsSearchTabs),
    ("Restore normalization converts invalid search tabs to fallback folders", DockLayoutStoreTests.RestoreNormalizationConvertsInvalidSearchTabsToFallbackFolders),
    ("Waffle global search location round-trips", WaffleSearchLocationTests.GlobalLocationRoundTrips),
    ("Waffle current-folder location round-trips", WaffleSearchLocationTests.CurrentFolderLocationRoundTrips),
    ("Waffle current-folder location requires a root", WaffleSearchLocationTests.CurrentFolderLocationRequiresRoot),
    ("File index searches names and paths within scope", FileSearchIndexTests.SearchesNameAndPathWithScopeAndLimit),
    ("File index applies create delete and rename changes", FileSearchIndexTests.AppliesCreateDeleteAndDirectoryRename),
    ("File index sorts folders first and caps results", FileSearchIndexTests.SortsFoldersFirstAndCapsResults),
    ("File index publishes replacement and buffered changes atomically", FileSearchIndexTests.ReplaceAndApplyPublishesOnlyTheCompletedGeneration),
    ("File index metadata updates preserve native identity", FileSearchIndexTests.MetadataUpdatesPreserveNativeIdentityButCreatesReplaceIt),
    ("JSON index store round-trips version 2 native identifiers", JsonFileIndexStoreTests.RoundTripsVersionTwoNativeIdentifiersAndNullMetadata),
    ("JSON index store rejects version 1 snapshots", JsonFileIndexStoreTests.RejectsVersionOneSnapshotAsCorrupt),
    ("JSON index store rejects semantically invalid version 2 snapshots", JsonFileIndexStoreTests.RejectsSemanticallyInvalidVersionTwoSnapshots),
    ("Waffle provider builds persists and tracks file changes", WaffleFileSearchProviderTests.BuildsPersistsAndTracksFileChanges),
    ("Waffle provider safely rebuilds corrupt persistence", WaffleFileSearchProviderTests.CorruptPersistenceTriggersSafeRebuild),
    ("Waffle provider summarizes build warnings without changing skipped counts", WaffleFileSearchProviderTests.CompletedBuildSummarizesWarningsWithoutChangingSkippedCount),
    ("Canceled rebuild restores the previous ready generation", WaffleFileSearchProviderTests.CanceledRebuildRestoresPreviousReadyGeneration),
    ("Canceled initial rebuild restores the empty state", WaffleFileSearchProviderTests.CanceledInitialRebuildRestoresEmptyState),
    ("Failed rebuild applies buffered changes to the previous generation", WaffleFileSearchProviderTests.FailedRebuildAppliesBufferedChangesToPreviousGeneration),
    ("Failed initial build does not publish buffered partial changes", WaffleFileSearchProviderTests.FailedInitialBuildDoesNotPublishBufferedPartialChanges),
    ("Recursive index build honors cancellation", WaffleFileSearchProviderTests.RecursiveBuildHonorsCancellation),
};

var failed = 0;

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex);
    }
}

if (failed > 0)
{
    Console.WriteLine($"{failed} test(s) failed.");
    Environment.Exit(1);
}

Console.WriteLine($"{tests.Length} test(s) passed.");

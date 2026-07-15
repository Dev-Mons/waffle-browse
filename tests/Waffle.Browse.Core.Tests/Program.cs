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
    ("NTFS parser reads continuation and v2 records", NtfsUsnRecordParserTests.ParsesContinuationAndV2Records),
    ("NTFS parser preserves Unicode and v3 file IDs", NtfsUsnRecordParserTests.ParsesUnicodeAndV3FileIds),
    ("NTFS parser rejects malformed record buffers", NtfsUsnRecordParserTests.RejectsMalformedRecordBuffers),
    ("NTFS path resolver handles child-before-parent records", NtfsPathResolverTests.ResolvesPathsWithoutDependingOnRecordOrder),
    ("NTFS path resolver quarantines orphan and cycle components", NtfsPathResolverTests.QuarantinesOnlyOrphanAndCycleComponents),
    ("NTFS path resolver rejects duplicate file IDs", NtfsPathResolverTests.RejectsDuplicateFileReferenceIds),
    ("NTFS path resolver handles deep graphs iteratively", NtfsPathResolverTests.ResolvesDeepGraphsIteratively),
    ("NTFS path resolver caps warnings without losing skip counts", NtfsPathResolverTests.CapsWarningsWithoutLosingSkippedCount),
    ("NTFS source publishes complete metadata and checkpoints", NtfsMftIndexSourceTests.BuildsCompleteVolumeWithMetadataAndCheckpoint),
    ("NTFS source keeps entries with unavailable metadata", NtfsMftIndexSourceTests.KeepsEntriesWhenMetadataCannotBeRead),
    ("NTFS source catches up changes during MFT scans", NtfsMftIndexSourceTests.BuildReplaysChangesThatOccurDuringMftScan),
    ("NTFS source rejects builds without a journal start", NtfsMftIndexSourceTests.MissingJournalStateDoesNotPublishMftGeneration),
    ("NTFS source retains last good root when journal is missing", NtfsMftIndexSourceTests.MissingJournalStateRetainsLastGoodRoot),
    ("NTFS journal-state errors preserve fallback taxonomy", NtfsMftIndexSourceTests.JournalStateErrorsUseFallbackOrRetentionTaxonomy),
    ("NTFS source rejects an MFT cursor without progress", NtfsMftIndexSourceTests.RejectsEnumerationWithoutForwardProgress),
    ("NTFS source cancellation discards partial generations", NtfsMftIndexSourceTests.CancellationDoesNotReturnPartialGeneration),
    ("NTFS source replays journal changes from checkpoints", NtfsMftIndexSourceTests.ReplaysJournalChangesFromPersistedCheckpoint),
    ("NTFS source rebuilds invalid journal checkpoints", NtfsMftIndexSourceTests.InvalidJournalCheckpointTriggersFullMftRebuild),
    ("NTFS source preserves hard-link paths during replay", NtfsMftIndexSourceTests.JournalReplayPreservesEveryHardLinkPath),
    ("NTFS source falls back only for unsupported or denied access", NtfsMftIndexSourceTests.FallsBackOnlyForUnsupportedOrDeniedNativeAccess),
    ("Fallback refresh slices baselines per root", NtfsMftIndexSourceTests.FallbackRefreshSlicesBaselinePerRoot),
    ("Fallback build isolates unavailable roots", NtfsMftIndexSourceTests.FallbackBuildIsolatesUnavailableRoots),
    ("Fallback rejects warning-only empty generations", NtfsMftIndexSourceTests.FallbackRejectsWarningOnlyEmptyGeneration),
    ("Fallback refresh slices same-volume roots by path", NtfsMftIndexSourceTests.FallbackRefreshSlicesSameVolumeRootsByPath),
    ("Waffle provider builds persists and tracks file changes", WaffleFileSearchProviderTests.BuildsPersistsAndTracksFileChanges),
    ("Waffle provider safely rebuilds corrupt persistence", WaffleFileSearchProviderTests.CorruptPersistenceTriggersSafeRebuild),
    ("Recursive index build honors cancellation", WaffleFileSearchProviderTests.RecursiveBuildHonorsCancellation),
    ("Loaded file index uses incremental refresh", WaffleFileSearchProviderTests.LoadedSnapshotUsesIncrementalRefresh),
    ("Failed incremental refresh keeps last good snapshot", WaffleFileSearchProviderTests.FailedIncrementalRefreshKeepsLastGoodSnapshot),
    ("JSON index preserves file ID width", WaffleFileSearchProviderTests.JsonPersistencePreservesFileIdWidth),
    ("Native watcher changes use checkpoint refresh", WaffleFileSearchProviderTests.NativeWatcherChangesUseCheckpointRefresh),
    ("Native watcher refreshes only the changed root", WaffleFileSearchProviderTests.NativeWatcherRefreshesOnlyChangedRoot),
    ("Nested root refresh preserves ancestor checkpoints", WaffleFileSearchProviderTests.NestedRootRefreshPreservesAncestorCheckpoint),
    ("Named-pipe framing round-trips JSON messages", NamedPipeFileIndexSourceTests.FramingRoundTripsJsonMessages),
    ("Named-pipe framing rejects oversized frames", NamedPipeFileIndexSourceTests.FramingRejectsOversizedFrames),
    ("Named-pipe framing rejects protocol mismatches", NamedPipeFileIndexSourceTests.FramingRejectsProtocolVersionMismatch),
    ("Named-pipe results stay below the frame cap", NamedPipeFileIndexSourceTests.ResultEntriesAreChunkedBelowTheFrameCap),
    ("Named-pipe result entries are dynamically batched", NamedPipeFileIndexSourceTests.ResultEntriesAreDynamicallyBatched),
    ("Named-pipe refresh baselines are dynamically batched", NamedPipeFileIndexSourceTests.RefreshBaselineEntriesAreDynamicallyBatched),
    ("Named-pipe entry batches are bounded for early frames", NamedPipeFileIndexSourceTests.EntryBatchItemCountIsBoundedForEarlyFrames),
    ("Named-pipe result entries validate while streaming", NamedPipeFileIndexSourceTests.ResultEntryValidationOccursWhileStreaming),
    ("Named-pipe baseline entries validate while streaming", NamedPipeFileIndexSourceTests.BaselineEntryValidationOccursWhileStreaming),
    ("Named-pipe entry batches reject count overflow", NamedPipeFileIndexSourceTests.EntryBatchesRejectAdvertisedCountOverflow),
    ("Named-pipe result reader skips progress heartbeats", NamedPipeFileIndexSourceTests.ResultReaderSkipsProgressHeartbeats),
    ("Named-pipe frame reads enforce idle timeout", NamedPipeFileIndexSourceTests.FramingReadIdleTimeoutThrowsTimeoutException),
    ("Named-pipe frame writes enforce idle timeout", NamedPipeFileIndexSourceTests.FramingWriteIdleTimeoutThrowsTimeoutException),
    ("Named-pipe framing preserves caller cancellation", NamedPipeFileIndexSourceTests.FramingCallerCancellationRemainsOperationCanceled),
    ("Elevated helper launcher uses the exact sibling without arguments", NamedPipeFileIndexSecurityTests.ElevatedLauncherUsesExactSiblingWithoutArguments),
    ("Elevated helper launcher rejects unprotected deployments", NamedPipeFileIndexSecurityTests.ElevatedLauncherRejectsUnprotectedDeployment),
    ("Elevated helper launcher rejects managed helper images", NamedPipeFileIndexSecurityTests.ElevatedLauncherRejectsManagedHelperImage),
    ("Elevated helper launcher requires a Program Files path boundary", NamedPipeFileIndexSecurityTests.ElevatedLauncherRequiresProgramFilesBoundary),
    ("Elevated helper launcher rejects writable deployment ACLs", NamedPipeFileIndexSecurityTests.ElevatedLauncherRejectsWritableDeploymentAcl),
    ("Cross-process helper lock serializes independent owners", NamedPipeFileIndexSecurityTests.CrossProcessOperationLockSerializesIndependentOwners),
    ("Default helper pipe identity separates sessions and deployments", NamedPipeFileIndexSecurityTests.DefaultPipeIdentitySeparatesSessionsAndDeployments),
    ("Named-pipe peer images require exact sibling executables", NamedPipeFileIndexSecurityTests.PeerImagePolicyRequiresExactSiblingExecutable),
    ("Named-pipe roots allow only ready fixed NTFS drives", NamedPipeFileIndexSecurityTests.RootPolicyAllowsOnlyReadyFixedNtfsDriveRoots),
    ("Unavailable named-pipe host uses supported fallback signal", NamedPipeFileIndexSourceTests.UnavailableHostThrowsNotSupported),
    ("Missing named-pipe helper launches, reconnects, and restarts after idle exit", NamedPipeFileIndexSourceTests.SuccessfulLaunchReconnectsAndCanRestartAfterIdleExit),
    ("Declined helper launches are coalesced across clients", NamedPipeFileIndexSourceTests.DeclinedLaunchIsCoalescedAcrossClients),
    ("Concurrent successful requests share one helper launch", NamedPipeFileIndexSourceTests.ConcurrentSuccessfulRequestsShareOneHelperLaunch),
    ("Caller cancellation never launches the helper", NamedPipeFileIndexSourceTests.CallerCancellationDoesNotLaunchHelper),
    ("Cancellation after helper launch suppresses replacement prompts", NamedPipeFileIndexSourceTests.CancellationAfterLaunchSuppressesReplacementPrompt),
    ("Untrusted connected servers never trigger a replacement helper", NamedPipeFileIndexSourceTests.UntrustedConnectedServerDoesNotLaunchReplacement),
    ("Launched untrusted servers suppress replacement prompts", NamedPipeFileIndexSourceTests.LaunchedUntrustedServerSuppressesReplacementPrompt),
    ("Named-pipe host exits after its idle timeout", NamedPipeFileIndexSourceTests.HostExitsAfterIdleTimeout),
    ("Named-pipe host resets idle timeout after a connection", NamedPipeFileIndexSourceTests.CompletedConnectionResetsHostIdleTimeout),
    ("Named-pipe host honors lifetime cancellation before idle timeout", NamedPipeFileIndexSourceTests.LifetimeCancellationStopsHostBeforeIdleTimeout),
    ("Named-pipe host builds and refreshes snapshots", NamedPipeFileIndexSourceTests.SameProcessHostBuildsThroughNamedPipe),
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

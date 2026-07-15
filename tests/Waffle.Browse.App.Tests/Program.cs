using Waffle.Browse.App.Tests.Controls;
using Waffle.Browse.App.Tests;
using Waffle.Browse.App.Tests.Settings;
using Waffle.Browse.App.Tests.Search;
using Waffle.Browse.App.Tests.Shell;
using Waffle.Browse.App.Tests.Theming;

var tests = new (string Name, Action Run)[]
{
    ("Search toolbar exposes Waffle scope selector", MainWindowSearchToolbarTests.SearchToolbarExposesWaffleScopeSelector),
    ("Late search response cannot replace latest search", LatestSearchRequestCoordinatorTests.LateResponseCannotReplaceLatestSearch),
    ("Windows index source selects NTFS for an eligible root", WindowsFileIndexSourceTests.SelectsNtfsSourceForEligibleRoot),
    ("Windows index source selects recursive indexing for a non-NTFS root", WindowsFileIndexSourceTests.SelectsRecursiveSourceForNonNtfsRoot),
    ("Windows index source falls back after NTFS is unavailable", WindowsFileIndexSourceTests.FallsBackAfterNtfsUnavailableAndReportsWarning),
    ("Windows index source does not fall back after cancellation", WindowsFileIndexSourceTests.CancellationDoesNotFallBack),
    ("Windows index source rejects fallback without a checkpoint", WindowsFileIndexSourceTests.MissingFallbackCheckpointFailsWholeBuild),
    ("Windows index source aggregates multiple completed roots", WindowsFileIndexSourceTests.AggregatesMultipleRootResultsWarningsAndCheckpoints),
    ("MainWindow uses the Windows index source and version 2 snapshot", MainWindowFileIndexWiringTests.UsesWindowsSourceAndVersionTwoSnapshot),
    ("MainWindow leaves fixed-drive filesystem selection to the source", MainWindowFileIndexWiringTests.IncludesReadyFixedDrivesBeforeFilesystemSelection),
    ("NTFS parser reads v2 Unicode name", NtfsMftIndexingTests.ParserReadsVersion2UnicodeName),
    ("NTFS parser preserves v3 high file reference bits", NtfsMftIndexingTests.ParserReadsVersion3HighFileReferenceBits),
    ("NTFS parser reads mixed v2 and v3 batch", NtfsMftIndexingTests.ParserReadsMixedVersionBatch),
    ("NTFS parser accepts future minor version and runtime name offset", NtfsMftIndexingTests.ParserAcceptsFutureMinorVersionAndExtendedNameOffset),
    ("NTFS parser rejects truncated buffers", NtfsMftIndexingTests.ParserRejectsTruncatedBuffers),
    ("NTFS parser rejects zero record length", NtfsMftIndexingTests.ParserRejectsZeroRecordLength),
    ("NTFS parser rejects misaligned record length", NtfsMftIndexingTests.ParserRejectsMisalignedRecordLength),
    ("NTFS parser rejects unsupported major version", NtfsMftIndexingTests.ParserRejectsUnsupportedMajorVersion),
    ("NTFS parser rejects odd Unicode name length", NtfsMftIndexingTests.ParserRejectsOddUnicodeNameLength),
    ("NTFS parser rejects invalid Unicode name ranges", NtfsMftIndexingTests.ParserRejectsInvalidUnicodeNameRanges),
    ("NTFS path graph builds root-folder-file chain", NtfsMftIndexingTests.PathGraphBuildsRootFolderFileChain),
    ("NTFS path graph quarantines missing parent", NtfsMftIndexingTests.PathGraphQuarantinesMissingParent),
    ("NTFS path graph quarantines self and multi-node cycles", NtfsMftIndexingTests.PathGraphQuarantinesSelfAndMultiNodeCycles),
    ("NTFS path graph keeps entry when metadata read fails", NtfsMftIndexingTests.PathGraphKeepsEntryWhenMetadataReadFails),
    ("NTFS path graph rejects a missing root anchor", NtfsMftIndexingTests.PathGraphRejectsMissingRootAnchor),
    ("NTFS path graph quarantines a non-directory parent", NtfsMftIndexingTests.PathGraphQuarantinesNonDirectoryParent),
    ("NTFS native structs keep stable 24-byte layouts", NtfsMftIndexingTests.NativeInteropStructsHaveStableTwentyFourByteLayout),
    ("Shell native theme applies dark theme to root and descendants", ShellNativeThemeApplierTests.ApplyDarkThemeThemesRootAndDescendants),
    ("Shell native theme restores Explorer theme for light mode", ShellNativeThemeApplierTests.ApplyLightThemeRestoresExplorerTheme),
    ("Shell native light theme forces shell windows to refresh theme", ShellNativeThemeApplierTests.ApplyLightThemeForcesShellWindowsToRefreshTheme),
    ("Shell native theme can skip descendant shell windows", ShellNativeThemeApplierTests.ApplyCanSkipDescendantShellWindows),
    ("Shell native focus applies to host window", ShellNativeFocusManagerTests.FocusNativeWindowFocusesHostWindow),
    ("Shell native focus redirects host window to preferred shell view", ShellNativeFocusManagerTests.FocusNativeWindowRedirectsHostWindowToPreferredFocusWindow),
    ("Shell native focus ignores preferred focus outside host", ShellNativeFocusManagerTests.FocusNativeWindowSkipsPreferredFocusOutsideHost),
    ("Shell native focus applies to child window", ShellNativeFocusManagerTests.FocusNativeWindowFocusesChildWindow),
    ("Shell native focus skips outside window", ShellNativeFocusManagerTests.FocusNativeWindowSkipsOutsideWindow),
    ("Shell native focus resolves Win32 entry points", ShellNativeFocusManagerTests.DefaultNativeApiResolvesWin32EntryPoints),
    ("Native shell mouse focus activates panel then restores focus", NativeShellActivationClassifierTests.NativeShellMouseFocusActivatesPanelThenRestoresFocus),
    ("Native shell focus activates panel then restores focus", NativeShellActivationClassifierTests.NativeShellFocusActivatesPanelThenRestoresFocus),
    ("Non-shell messages do not activate panels", NativeShellActivationClassifierTests.OtherMessagesDoNotActivatePanel),
    ("Native shell directional key down is forwarded", NativeShellKeyboardInputClassifierTests.DirectionalKeyDownIsForwardedToNativeShell),
    ("Native shell directional syskey down is forwarded", NativeShellKeyboardInputClassifierTests.DirectionalSysKeyDownIsForwardedToNativeShell),
    ("Native shell Delete key down is forwarded", NativeShellKeyboardInputClassifierTests.DeleteKeyDownIsForwardedToNativeShell),
    ("Native shell letter key down is forwarded", NativeShellKeyboardInputClassifierTests.LetterKeyDownIsForwardedToNativeShell),
    ("Native shell function key down is forwarded", NativeShellKeyboardInputClassifierTests.FunctionKeyDownIsForwardedToNativeShell),
    ("Native shell Backspace is reserved for panel navigation", NativeShellKeyboardInputClassifierTests.BackspaceKeyDownIsNotForwardedToNativeShell),
    ("Native shell Alt+Left/Right is reserved for panel navigation", NativeShellKeyboardInputClassifierTests.AltLeftRightSysKeyDownIsNotForwardedToNativeShell),
    ("Native shell non-key messages are ignored", NativeShellKeyboardInputClassifierTests.NonKeyMessagesAreNotForwardedToNativeShell),
    ("Native shell inline rename detected for Edit box in panel", NativeShellRenameStateClassifierTests.EditBoxInsideShellPanelIsInlineRename),
    ("Native shell inline rename match is case-insensitive", NativeShellRenameStateClassifierTests.EditBoxClassNameIsCaseInsensitive),
    ("Native shell inline rename ignores Edit box outside panel", NativeShellRenameStateClassifierTests.EditBoxOutsideShellPanelIsNotInlineRename),
    ("Native shell inline rename ignores ordinary shell focus", NativeShellRenameStateClassifierTests.NonEditFocusInsideShellPanelIsNotInlineRename),
    ("Native shell inline rename ignores missing class name", NativeShellRenameStateClassifierTests.MissingClassNameIsNotInlineRename),
    ("Native shell inline rename routes arrow keys to edit box", NativeShellRenameStateClassifierTests.ArrowKeyDownIsRoutedToRenameEdit),
    ("Native shell inline rename leaves character keys native", NativeShellRenameStateClassifierTests.CharacterKeyDownIsNotRoutedToRenameEdit),
    ("Native shell inline rename ignores non-keydown for routing", NativeShellRenameStateClassifierTests.NonKeyDownMessageIsNotRoutedToRenameEdit),
    ("Shell folder view uses details mode", ShellFolderViewSettingsTests.DefaultFolderViewUsesDetailsMode),
    ("Shell initial folder settings do not include full row selection", ShellFolderViewSettingsTests.InitialFolderSettingsDoNotIncludeFullRowSelection),
    ("Shell current folder flags enable full row selection", ShellFolderViewSettingsTests.CurrentFolderFlagsEnableFullRowSelection),
    ("Shell view activation uses in-place then focused state", ShellViewActivationManagerTests.ActivateWithFocusUsesInPlaceThenFocusedShellViewState),
    ("Shell view activation ignores COM failures", ShellViewActivationManagerTests.ActivateWithFocusIgnoresComFailures),
    ("Shell view activation treats failed HRESULT as failure", ShellViewActivationManagerTests.ActivateWithFocusTreatsFailedHresultAsFailure),
    ("Shell focused item selection selects focused item", ShellFocusedItemSelectionManagerTests.SelectFocusedItemSelectsAndFocusesCurrentItem),
    ("Shell focused item keyboard selection uses keyboard flag", ShellFocusedItemSelectionManagerTests.KeyboardSelectionMarksItemAsKeyboardSelected),
    ("Shell focused item selection skips missing focus", ShellFocusedItemSelectionManagerTests.SelectFocusedItemSkipsWhenNoItemIsFocused),
    ("Shell focused item mouse selection skips empty shell selection", ShellFocusedItemSelectionManagerTests.MouseSelectionSkipsWhenShellSelectionIsEmpty),
    ("Shell focused item selection skips failed focus query", ShellFocusedItemSelectionManagerTests.SelectFocusedItemSkipsWhenFocusQueryFails),
    ("Window native theme applies to resolved window handle", WindowNativeThemeApplierTests.ApplyThemesResolvedWindowHandle),
    ("Window native theme skips windows without handle", WindowNativeThemeApplierTests.ApplySkipsWindowWithoutHandle),
    ("Explorer panel recreates shell host when theme changes", ExplorerPanelControlThemeTests.ApplyThemeRecreatesShellHostWhenThemeChanges),
    ("Explorer panel keeps shell host when theme stays same", ExplorerPanelControlThemeTests.ApplyThemeKeepsShellHostWhenThemeStaysSame),
    ("Explorer panel shell clicks do not request WPF panel focus", ExplorerPanelControlFocusTests.ShellHostAreaClickDoesNotRequestWpfPanelFocus),
    ("Explorer panel active state does not resize shell host", ExplorerPanelControlFocusTests.ActiveStateUpdateDoesNotChangeLayoutThickness),
    ("Explorer panel active state does not rewrite content", ExplorerPanelControlFocusTests.ActiveStateUpdateDoesNotRewritePanelContent),
    ("Explorer panel tabs do not accept keyboard focus", ExplorerPanelControlFocusTests.TabsListBoxDoesNotAcceptKeyboardFocus),
    ("UI settings store round-trips theme", UiSettingsStoreTests.UiSettingsStoreRoundTripsTheme),
    ("UI settings store round-trips search scope", UiSettingsStoreTests.UiSettingsStoreRoundTripsSearchScope),
    ("Application data path uses current directory for new installs", ApplicationDataPathTests.ResolveUsesCurrentDirectoryForNewInstall),
    ("Application data path migrates legacy directory", ApplicationDataPathTests.ResolveMigratesLegacyDirectory),
    ("Application data path prefers existing current directory", ApplicationDataPathTests.ResolvePrefersExistingCurrentDirectory),
    ("App theme resources apply dark theme brushes", AppThemeResourcesTests.ApplyDarkThemeUpdatesCoreBrushes),
    ("App theme resources apply light shell host background", AppThemeResourcesTests.ApplyLightThemeUsesLightShellHostBackground),
    ("App default resources use light shell host background", AppThemeResourcesTests.AppDefaultResourcesUseLightShellHostBackground),
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

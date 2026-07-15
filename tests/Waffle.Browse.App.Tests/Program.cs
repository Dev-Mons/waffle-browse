using Waffle.Browse.App.Tests.Controls;
using Waffle.Browse.App.Tests;
using Waffle.Browse.App.Tests.Settings;
using Waffle.Browse.App.Tests.Shell;
using Waffle.Browse.App.Tests.Theming;

var tests = new (string Name, Action Run)[]
{
    ("Search resolver returns search-ms URI when Shell can parse it", ShellSearchTargetResolverTests.ResolveReturnsSearchUriWhenShellCanParseTarget),
    ("Search resolver does not create saved search fallback", ShellSearchTargetResolverTests.ResolveDoesNotCreateSavedSearchFallback),
    ("Search toolbar does not expose search scope selector", MainWindowSearchToolbarTests.SearchToolbarDoesNotExposeSearchScopeSelector),
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
    ("UI settings store saves theme without search scope", UiSettingsStoreTests.UiSettingsStoreSavesThemeWithoutSearchScope),
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

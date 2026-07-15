using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Waffle.Browse.App.Diagnostics;
using Waffle.Browse.App.Settings;

namespace Waffle.Browse.App.Shell;

public sealed class ShellExplorerHost : HwndHost
{
    private static readonly Guid ExplorerBrowserClsid = new("71F96385-DDD6-48D3-A0C1-AE06E8B055FB");
    private static readonly Guid SearchFolderItemFactoryClsid = new("14010E02-BBBD-41F0-88E3-EDA371216584");
    private static readonly Guid ConditionFactoryClsid = new("E03E85B0-7BE3-4000-BA98-6C13DE9FA486");
    private static readonly Guid ShellItemIid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid ShellViewIid = new("000214E3-0000-0000-C000-000000000046");
    private static readonly Guid FolderViewIid = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    private static readonly Guid FolderView2Iid = new("1AF3A467-214F-4298-908E-06B03E0B39F9");

    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WmSetFocus = 0x0007;
    private const ushort VariantTypeWideString = 31;
    private const uint SvgioSelection = 0x1;

    private IExplorerBrowser? browser;
    private ExplorerBrowserEvents? browserEvents;
    private readonly ShellNativeFocusManager nativeFocusManager = new();
    private readonly ShellViewActivationManager viewActivationManager = new();
    private readonly ShellFocusedItemSelectionManager focusedItemSelectionManager = new();
    private uint browserEventsCookie;
    private IntPtr hostWindow;
    private IntPtr shellViewWindow;
    private string pendingPath;
    private PendingSearch? pendingSearch;

    public ShellExplorerHost(string initialPath)
    {
        pendingPath = initialPath;
    }

    public event EventHandler<string>? NavigationCompleted;

    public event EventHandler<string>? NavigationFailed;

    public bool ContainsNativeFocus(IntPtr focusedWindow)
    {
        return ContainsNativeWindow(focusedWindow);
    }

    public bool ContainsNativeWindow(IntPtr window)
    {
        return nativeFocusManager.ContainsNativeWindow(hostWindow, window);
    }

    public bool FocusNativeWindow(IntPtr window)
    {
        return nativeFocusManager.FocusNativeWindow(hostWindow, window, shellViewWindow);
    }

    public bool ForwardNativeKeyboardInput(IntPtr window, MSG msg)
    {
        var targetWindow = ResolveNativeKeyboardTarget(window);
        if (!ContainsNativeWindow(targetWindow))
        {
            return false;
        }

        var virtualKey = unchecked((int)msg.wParam.ToInt64());
        if (TryTranslateBrowserAccelerator(targetWindow, msg))
        {
            return true;
        }

        if (virtualKey is NativeShellKeyboardInputClassifier.VkLeft
            or NativeShellKeyboardInputClassifier.VkUp
            or NativeShellKeyboardInputClassifier.VkRight
            or NativeShellKeyboardInputClassifier.VkDown
            or NativeShellKeyboardInputClassifier.VkDelete)
        {
            SendMessage(targetWindow, msg.message, msg.wParam, msg.lParam);
            return true;
        }

        return false;
    }

    private bool TryTranslateBrowserAccelerator(IntPtr targetWindow, MSG msg)
    {
        if (browser is null)
        {
            return false;
        }

        try
        {
            if (browser is not IInputObject inputObject)
            {
                return false;
            }

            var translated = msg;
            translated.hwnd = targetWindow;
            return inputObject.TranslateAcceleratorIO(ref translated) == 0;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    public bool SelectFocusedShellItem(ShellFocusedItemSelectionMode mode)
    {
        if (browser is null)
        {
            return false;
        }

        object? viewObject = null;
        try
        {
            var folderViewIid = FolderViewIid;
            browser.GetCurrentView(ref folderViewIid, out viewObject);
            if (viewObject is not IFolderView folderView)
            {
                return false;
            }

            var selected = focusedItemSelectionManager.SelectFocusedItem(
                () => (folderView.GetFocusedItem(out var item), item),
                folderView.SelectItem,
                mode,
                () => (folderView.ItemCount(SvgioSelection, out var count), count));
            TraceSelectionState(folderView, mode, selected);
            return selected;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(viewObject);
        }
    }

    public bool ActivateShellViewWithFocus()
    {
        if (browser is null)
        {
            return false;
        }

        object? viewObject = null;
        try
        {
            var shellViewIid = ShellViewIid;
            browser.GetCurrentView(ref shellViewIid, out viewObject);
            return viewObject is IShellView shellView && ActivateShellViewWithFocus(shellView);
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(viewObject);
        }
    }

    public void ApplyTheme(UiTheme nextTheme)
    {
        // ExplorerBrowser owns its native theme. Forcing DarkMode_Explorer on
        // DirectUIHWND hides the selected item highlight on current Windows.
    }

    public void Navigate(string path)
    {
        pendingPath = path;
        pendingSearch = null;
        if (browser is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!BrowseToPath(path))
        {
            NavigationFailed?.Invoke(this, path);
        }
    }

    public void NavigateToSearch(string searchTarget, string queryText, IReadOnlyList<string> roots)
    {
        pendingPath = searchTarget;
        pendingSearch = new PendingSearch(queryText, roots.ToList());
        if (browser is null || string.IsNullOrWhiteSpace(searchTarget))
        {
            return;
        }

        if (!BrowseToSearchFolder(searchTarget, queryText, roots))
        {
            NavigationFailed?.Invoke(this, searchTarget);
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        hostWindow = CreateWindowEx(
            0,
            "static",
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0,
            0,
            Math.Max((int)ActualWidth, 1),
            Math.Max((int)ActualHeight, 1),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (hostWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not create the Explorer host window.");
        }

        browser = CreateExplorerBrowser();
        var bounds = new NativeRect(0, 0, Math.Max((int)ActualWidth, 1), Math.Max((int)ActualHeight, 1));
        var settings = new FolderSettings
        {
            ViewMode = (FolderViewMode)ShellFolderViewSettings.DetailsViewMode,
            Flags = (FolderFlags)ShellFolderViewSettings.InitialFlags
        };
        browser.Initialize(hostWindow, ref bounds, ref settings);
        ApplyCurrentFolderViewFlags();
        browserEvents = new ExplorerBrowserEvents(OnBrowserNavigationCompleted, OnBrowserViewCreated);
        browser.Advise(browserEvents, out browserEventsCookie);

        if (!string.IsNullOrWhiteSpace(pendingPath))
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (browser is not null && !string.IsNullOrWhiteSpace(pendingPath))
                {
                    var navigated = pendingSearch is { } search
                        ? BrowseToSearchFolder(pendingPath, search.QueryText, search.Roots)
                        : BrowseToPath(pendingPath);
                    if (!navigated)
                    {
                        NavigationFailed?.Invoke(this, pendingPath);
                    }
                }
            }, DispatcherPriority.Loaded);
        }

        return new HandleRef(this, hostWindow);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        shellViewWindow = IntPtr.Zero;
        if (browser is not null)
        {
            if (browserEventsCookie != 0)
            {
                browser.Unadvise(browserEventsCookie);
                browserEventsCookie = 0;
            }

            browser.Destroy();
            Marshal.FinalReleaseComObject(browser);
            browser = null;
            browserEvents = null;
        }

        if (hostWindow != IntPtr.Zero)
        {
            DestroyWindow(hostWindow);
            hostWindow = IntPtr.Zero;
        }
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (hwnd == hostWindow && msg == WmSetFocus && FocusShellViewWindow())
        {
            handled = true;
            return IntPtr.Zero;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        return FocusShellViewWindow();
    }

    protected override bool HasFocusWithinCore()
    {
        return ContainsNativeFocus(GetFocus());
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (browser is null)
        {
            return;
        }

        var bounds = new NativeRect(0, 0, Math.Max((int)rcBoundingBox.Width, 1), Math.Max((int)rcBoundingBox.Height, 1));
        browser.SetRect(IntPtr.Zero, ref bounds);
    }

    private bool BrowseToPath(string path)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            browser?.BrowseToIDList(pidl, 0);
            return true;
        }
        catch (COMException)
        {
            // Invalid, deleted, or inaccessible paths are normalized at restore time.
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                ILFree(pidl);
            }
        }
    }

    private bool BrowseToSearchFolder(string searchTarget, string queryText, IReadOnlyList<string> roots)
    {
        IShellItemArray? scope = null;
        ICondition? condition = null;
        object? searchItem = null;
        object? factoryObject = null;
        try
        {
            scope = CreateScope(roots);
            condition = CreateSearchCondition(queryText);
            factoryObject = CreateSearchFolderItemFactory();
            var factory = (ISearchFolderItemFactory)factoryObject;
            factory.SetDisplayName($"Search results for {queryText}");
            factory.SetScope(scope);
            factory.SetCondition(condition);
            var shellItemIid = ShellItemIid;
            factory.GetShellItem(ref shellItemIid, out searchItem);
            browser?.BrowseToObject(searchItem, 0);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(searchItem);
            ReleaseComObject(condition);
            ReleaseComObject(scope);
            ReleaseComObject(factoryObject);
        }
    }

    private static IShellItemArray CreateScope(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
        {
            throw new ArgumentException("At least one search root is required.", nameof(roots));
        }

        var pidls = new IntPtr[roots.Count];
        try
        {
            for (var index = 0; index < roots.Count; index++)
            {
                SHParseDisplayName(roots[index], IntPtr.Zero, out pidls[index], 0, out _);
            }

            SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var scope);
            return scope;
        }
        finally
        {
            foreach (var pidl in pidls)
            {
                if (pidl != IntPtr.Zero)
                {
                    ILFree(pidl);
                }
            }
        }
    }

    private static ICondition CreateSearchCondition(string queryText)
    {
        var conditionFactoryObject = CreateConditionFactory();
        var conditionFactory = (IConditionFactory)conditionFactoryObject;
        var value = PropVariant.FromString(queryText);
        var operation = queryText.IndexOfAny(['*', '?']) >= 0
            ? ConditionOperation.ValueMatchesWildcard
            : ConditionOperation.ValueContains;

        try
        {
            conditionFactory.MakeLeaf(
                "System.ItemNameDisplay",
                operation,
                null,
                ref value,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                out var condition);
            return condition;
        }
        finally
        {
            value.Dispose();
            ReleaseComObject(conditionFactoryObject);
        }
    }

    private static object CreateSearchFolderItemFactory()
    {
        var type = Type.GetTypeFromCLSID(SearchFolderItemFactoryClsid, throwOnError: true)
            ?? throw new InvalidOperationException("SearchFolderItemFactory COM type was not found.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("SearchFolderItemFactory COM object could not be created.");
    }

    private static object CreateConditionFactory()
    {
        var type = Type.GetTypeFromCLSID(ConditionFactoryClsid, throwOnError: true)
            ?? throw new InvalidOperationException("ConditionFactory COM type was not found.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("ConditionFactory COM object could not be created.");
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    private void OnBrowserNavigationCompleted(IntPtr pidl)
    {
        ApplyFolderSettings();
        ApplyCurrentFolderViewFlags();
        ActivateShellViewWithFocus();
        var path = PathFromPidl(pidl);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        pendingPath = path;
        NavigationCompleted?.Invoke(this, path);
    }

    private void OnBrowserViewCreated(object view)
    {
        ApplyFolderSettings();
        ApplyCurrentFolderViewFlags();
        if (view is IShellView shellView)
        {
            CaptureShellViewWindow(shellView);
            ActivateShellViewWithFocus(shellView);
        }
    }

    private bool ActivateShellViewWithFocus(IShellView shellView)
    {
        CaptureShellViewWindow(shellView);
        return viewActivationManager.ActivateWithFocus(shellView.UIActivate);
    }

    private bool FocusShellViewWindow()
    {
        if (shellViewWindow == IntPtr.Zero && !ActivateShellViewWithFocus())
        {
            return false;
        }

        if (shellViewWindow == IntPtr.Zero)
        {
            return false;
        }

        var focused = nativeFocusManager.FocusNativeWindow(hostWindow, shellViewWindow);
        var activated = ActivateShellViewWithFocus();
        return focused || activated;
    }

    private IntPtr ResolveNativeKeyboardTarget(IntPtr window)
    {
        return window == hostWindow && shellViewWindow != IntPtr.Zero
            ? shellViewWindow
            : window;
    }

    private void CaptureShellViewWindow(IShellView shellView)
    {
        if (shellView.GetWindow(out var viewWindow) < 0
            || viewWindow == IntPtr.Zero
            || !ContainsNativeWindow(viewWindow))
        {
            return;
        }

        shellViewWindow = viewWindow;
    }

    private void ApplyFolderSettings()
    {
        if (browser is null)
        {
            return;
        }

        var settings = new FolderSettings
        {
            ViewMode = (FolderViewMode)ShellFolderViewSettings.DetailsViewMode,
            Flags = (FolderFlags)ShellFolderViewSettings.InitialFlags
        };
        try
        {
            browser.SetFolderSettings(ref settings);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
        }
    }

    private bool ApplyCurrentFolderViewFlags()
    {
        if (browser is null)
        {
            return false;
        }

        object? viewObject = null;
        try
        {
            var folderView2Iid = FolderView2Iid;
            browser.GetCurrentView(ref folderView2Iid, out viewObject);
            if (viewObject is not IFolderView2 folderView)
            {
                return false;
            }

            return folderView.SetCurrentFolderFlags(
                ShellFolderViewSettings.CurrentFolderFlagMask,
                ShellFolderViewSettings.CurrentFolderFlags) >= 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(viewObject);
        }
    }

    private static void TraceSelectionState(IFolderView folderView, ShellFocusedItemSelectionMode mode, bool selected)
    {
        if (!FocusTraceLogger.IsEnabled)
        {
            return;
        }

        var selectionCountResult = folderView.ItemCount(SvgioSelection, out var selectionCount);
        var selectionMarkResult = folderView.GetSelectionMarkedItem(out var selectionMark);
        FocusTraceLogger.WriteLine(
            $"{DateTimeOffset.Now:O} stage=native-shell-selection-state mode={mode};selected={selected};" +
            $"selectionCountResult=0x{selectionCountResult:X8};selectionCount={selectionCount};" +
            $"selectionMarkResult=0x{selectionMarkResult:X8};selectionMark={selectionMark}");
    }

    private static string? PathFromPidl(IntPtr pidl)
    {
        var builder = new StringBuilder(260);
        return SHGetPathFromIDList(pidl, builder) ? builder.ToString() : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateShellItemArrayFromIDLists(
        uint cidl,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] rgpidl,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemArray ppsiItemArray);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    private static IExplorerBrowser CreateExplorerBrowser()
    {
        var explorerBrowserType = Type.GetTypeFromCLSID(ExplorerBrowserClsid, throwOnError: true)
            ?? throw new InvalidOperationException("ExplorerBrowser COM type was not found.");
        return (IExplorerBrowser)(Activator.CreateInstance(explorerBrowserType)
            ?? throw new InvalidOperationException("ExplorerBrowser COM object could not be created."));
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A0FFBC28-5482-4366-BE27-3E81E78E06C2")]
    private interface ISearchFolderItemFactory
    {
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName);

        void SetFolderTypeID(ref Guid ftid);

        void SetFolderLogicalViewMode(FolderLogicalViewMode flvm);

        void SetIconSize(int iIconSize);

        void SetVisibleColumns(uint cVisibleColumns, IntPtr rgKey);

        void SetSortColumns(uint cSortColumns, IntPtr rgSortColumns);

        void SetGroupColumn(ref PropertyKey keyGroup);

        void SetStacks(uint cStackKeys, IntPtr rgStackKeys);

        void SetScope([MarshalAs(UnmanagedType.Interface)] IShellItemArray psiaScope);

        void SetCondition([MarshalAs(UnmanagedType.Interface)] ICondition pCondition);

        void GetShellItem(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void GetIDList(out IntPtr ppidl);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    private interface IShellItemArray
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0FC988D4-C935-4B97-A973-46282EA175C8")]
    private interface ICondition
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A5EFE073-B16F-474f-9F3E-9F8B497A3E08")]
    private interface IConditionFactory
    {
        void MakeNot([MarshalAs(UnmanagedType.Interface)] ICondition pcSub, [MarshalAs(UnmanagedType.Bool)] bool fSimplify, [MarshalAs(UnmanagedType.Interface)] out ICondition ppcResult);

        void MakeAndOr(ConditionType ct, IntPtr peusubs, [MarshalAs(UnmanagedType.Bool)] bool fSimplify, [MarshalAs(UnmanagedType.Interface)] out ICondition ppcResult);

        void MakeLeaf(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPropertyName,
            ConditionOperation cop,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszValueType,
            ref PropVariant ppropvar,
            IntPtr pPropertyNameTerm,
            IntPtr pOperationTerm,
            IntPtr pValueTerm,
            [MarshalAs(UnmanagedType.Bool)] bool fExpand,
            [MarshalAs(UnmanagedType.Interface)] out ICondition ppcResult);

        void Resolve([MarshalAs(UnmanagedType.Interface)] ICondition pc, uint sqro, IntPtr pstReferenceTime, [MarshalAs(UnmanagedType.Interface)] out ICondition ppcResolved);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6")]
    private interface IExplorerBrowser
    {
        void Initialize(IntPtr hwndParent, ref NativeRect prc, ref FolderSettings pfs);

        void Destroy();

        void SetRect(IntPtr phdwp, ref NativeRect rcBrowser);

        void SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string pszPropertyBag);

        void SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string pszEmptyText);

        void SetFolderSettings(ref FolderSettings pfs);

        void Advise([MarshalAs(UnmanagedType.Interface)] IExplorerBrowserEvents psbe, out uint pdwCookie);

        void Unadvise(uint dwCookie);

        void SetOptions(ExplorerBrowserOptions dwFlag);

        void GetOptions(out ExplorerBrowserOptions pdwFlag);

        void BrowseToIDList(IntPtr pidl, uint uFlags);

        void BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint uFlags);

        void FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, int dwFlags);

        void RemoveAll();

        void GetCurrentView(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("361BBDC7-E6EE-4E13-BE58-58E2240C810F")]
    private interface IExplorerBrowserEvents
    {
        [PreserveSig]
        int OnNavigationPending(IntPtr pidlFolder);

        [PreserveSig]
        int OnViewCreated([MarshalAs(UnmanagedType.IUnknown)] object psv);

        [PreserveSig]
        int OnNavigationComplete(IntPtr pidlFolder);

        [PreserveSig]
        int OnNavigationFailed(IntPtr pidlFolder);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ExplorerBrowserEvents : IExplorerBrowserEvents
    {
        private readonly Action<IntPtr> navigationCompleted;
        private readonly Action<object> viewCreated;

        public ExplorerBrowserEvents(Action<IntPtr> navigationCompleted, Action<object> viewCreated)
        {
            this.navigationCompleted = navigationCompleted;
            this.viewCreated = viewCreated;
        }

        public int OnNavigationPending(IntPtr pidlFolder)
        {
            return 0;
        }

        public int OnViewCreated(object psv)
        {
            viewCreated(psv);
            return 0;
        }

        public int OnNavigationComplete(IntPtr pidlFolder)
        {
            navigationCompleted(pidlFolder);
            return 0;
        }

        public int OnNavigationFailed(IntPtr pidlFolder)
        {
            return 0;
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("68284FAA-6A48-11D0-8C78-00C04FD918B4")]
    private interface IInputObject
    {
        [PreserveSig]
        int UIActivateIO([MarshalAs(UnmanagedType.Bool)] bool fActivate, ref MSG lpMsg);

        [PreserveSig]
        int HasFocusIO();

        [PreserveSig]
        int TranslateAcceleratorIO(ref MSG lpMsg);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    private interface IShellView
    {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);

        [PreserveSig]
        int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);

        [PreserveSig]
        int TranslateAccelerator(ref MSG pmsg);

        [PreserveSig]
        int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool fEnable);

        [PreserveSig]
        int UIActivate(ShellViewActivationState uState);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    private interface IFolderView
    {
        [PreserveSig]
        int GetCurrentViewMode(out uint pViewMode);

        [PreserveSig]
        int SetCurrentViewMode(uint viewMode);

        [PreserveSig]
        int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        [PreserveSig]
        int Item(int itemIndex, out IntPtr pidl);

        [PreserveSig]
        int ItemCount(uint flags, out int count);

        [PreserveSig]
        int Items(uint flags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        [PreserveSig]
        int GetSelectionMarkedItem(out int item);

        [PreserveSig]
        int GetFocusedItem(out int item);

        [PreserveSig]
        int GetItemPosition(IntPtr pidl, out NativePoint point);

        [PreserveSig]
        int GetSpacing(out NativePoint point);

        [PreserveSig]
        int GetDefaultSpacing(out NativePoint point);

        [PreserveSig]
        int GetAutoArrange();

        [PreserveSig]
        int SelectItem(int item, ShellViewSelectionFlags flags);

        [PreserveSig]
        int SelectAndPositionItems(uint count, IntPtr pidls, IntPtr points, ShellViewSelectionFlags flags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("1AF3A467-214F-4298-908E-06B03E0B39F9")]
    private interface IFolderView2
    {
        [PreserveSig]
        int GetCurrentViewMode(out uint pViewMode);

        [PreserveSig]
        int SetCurrentViewMode(uint viewMode);

        [PreserveSig]
        int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        [PreserveSig]
        int Item(int itemIndex, out IntPtr pidl);

        [PreserveSig]
        int ItemCount(uint flags, out int count);

        [PreserveSig]
        int Items(uint flags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        [PreserveSig]
        int GetSelectionMarkedItem(out int item);

        [PreserveSig]
        int GetFocusedItem(out int item);

        [PreserveSig]
        int GetItemPosition(IntPtr pidl, out NativePoint point);

        [PreserveSig]
        int GetSpacing(out NativePoint point);

        [PreserveSig]
        int GetDefaultSpacing(out NativePoint point);

        [PreserveSig]
        int GetAutoArrange();

        [PreserveSig]
        int SelectItem(int item, ShellViewSelectionFlags flags);

        [PreserveSig]
        int SelectAndPositionItems(uint count, IntPtr pidls, IntPtr points, ShellViewSelectionFlags flags);

        [PreserveSig]
        int SetGroupBy(ref PropertyKey key, [MarshalAs(UnmanagedType.Bool)] bool ascending);

        [PreserveSig]
        int GetGroupBy(out PropertyKey key, [MarshalAs(UnmanagedType.Bool)] out bool ascending);

        [PreserveSig]
        int SetViewProperty(IntPtr pidl, ref PropertyKey key, ref PropVariant propVariant);

        [PreserveSig]
        int GetViewProperty(IntPtr pidl, ref PropertyKey key, out PropVariant propVariant);

        [PreserveSig]
        int SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string propertyList);

        [PreserveSig]
        int SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string propertyList);

        [PreserveSig]
        int SetText(uint textType, [MarshalAs(UnmanagedType.LPWStr)] string text);

        [PreserveSig]
        int SetCurrentFolderFlags(uint mask, uint flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FolderSettings
    {
        public FolderViewMode ViewMode;
        public FolderFlags Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant : IDisposable
    {
        private ushort vt;
        private ushort reserved1;
        private ushort reserved2;
        private ushort reserved3;
        private IntPtr value;
        private IntPtr value2;

        public static PropVariant FromString(string text)
        {
            return new PropVariant
            {
                vt = VariantTypeWideString,
                value = Marshal.StringToCoTaskMemUni(text)
            };
        }

        public void Dispose()
        {
            if (value != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(value);
                value = IntPtr.Zero;
            }
        }
    }

    private sealed record PendingSearch(string QueryText, IReadOnlyList<string> Roots);

    private enum FolderViewMode : uint
    {
        Details = ShellFolderViewSettings.DetailsViewMode
    }

    [Flags]
    private enum FolderFlags : uint
    {
        None = 0,
        FullRowSelect = ShellFolderViewSettings.FullRowSelect
    }

    [Flags]
    private enum ExplorerBrowserOptions : uint
    {
        None = 0,
    }

    private enum FolderLogicalViewMode
    {
        Unspecified = -1,
        First = 1,
        Details = 4
    }

    private enum ConditionType
    {
        And = 0,
        Or = 1,
        Not = 2,
        Leaf = 3
    }

    private enum ConditionOperation
    {
        ValueEndsWith = 8,
        ValueContains = 9,
        ValueMatchesWildcard = 11
    }
}

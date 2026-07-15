using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Waffle.Browse.Core.Search;

namespace Waffle.Browse.App.Search;

public sealed class EverythingSearchService : IEverythingSearchService, IDisposable
{
    private const string DllName = "Everything64.dll";
    private const string EverythingWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    private const uint RequestFileName = 0x00000001;
    private const uint RequestPath = 0x00000002;
    private const uint RequestSize = 0x00000010;
    private const uint RequestDateModified = 0x00000040;
    private const uint EverythingErrorIpc = 2;

    private readonly SemaphoreSlim nativeGate = new(1, 1);
    private bool disposed;

    public async Task<EverythingAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(CheckAvailability, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EverythingSearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.MaxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "MaxResults must be greater than zero.");
        }

        await nativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() => SearchCore(query, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            nativeGate.Release();
        }
    }

    private static EverythingAvailability CheckAvailability()
    {
        try
        {
            var databaseLoaded = NativeMethods.Everything_IsDBLoaded();
            if (NativeMethods.FindWindow(EverythingWindowClass, null) == IntPtr.Zero)
            {
                return new EverythingAvailability(
                    EverythingAvailabilityKind.NotRunning,
                    "Everything이 실행되고 있지 않습니다.");
            }

            return databaseLoaded
                ? Available()
                : new EverythingAvailability(
                    EverythingAvailabilityKind.IndexLoading,
                    "Everything 인덱스를 불러오는 중입니다.");
        }
        catch (DllNotFoundException)
        {
            return new EverythingAvailability(
                EverythingAvailabilityKind.SdkMissing,
                "Everything64.dll을 찾을 수 없습니다.");
        }
        catch (BadImageFormatException)
        {
            return new EverythingAvailability(
                EverythingAvailabilityKind.SdkMissing,
                "Everything SDK의 아키텍처가 앱과 일치하지 않습니다.");
        }
        catch (EntryPointNotFoundException ex)
        {
            return new EverythingAvailability(EverythingAvailabilityKind.Error, ex.Message);
        }
    }

    private static EverythingSearchResponse SearchCore(SearchQuery query, CancellationToken cancellationToken)
    {
        var availability = CheckAvailability();
        if (!availability.IsAvailable)
        {
            return new EverythingSearchResponse([], 0, availability);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            NativeMethods.Everything_Reset();
            NativeMethods.Everything_SetSearchW(EverythingQueryBuilder.Build(query));
            NativeMethods.Everything_SetMatchPath(false);
            NativeMethods.Everything_SetMatchCase(false);
            NativeMethods.Everything_SetMatchWholeWord(false);
            NativeMethods.Everything_SetRegex(false);
            NativeMethods.Everything_SetOffset(0);
            NativeMethods.Everything_SetMax((uint)Math.Min(query.MaxResults, 1000));
            NativeMethods.Everything_SetRequestFlags(RequestFileName | RequestPath | RequestSize | RequestDateModified);
            NativeMethods.Everything_SetSort(ToNativeSort(query.Sort));

            if (!NativeMethods.Everything_QueryW(true))
            {
                var error = NativeMethods.Everything_GetLastError();
                var kind = error == EverythingErrorIpc
                    ? EverythingAvailabilityKind.NotRunning
                    : EverythingAvailabilityKind.Error;
                return new EverythingSearchResponse(
                    [],
                    0,
                    new EverythingAvailability(kind, $"Everything 검색에 실패했습니다. 오류 코드: {error}"));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var count = NativeMethods.Everything_GetNumResults();
            var total = NativeMethods.Everything_GetTotResults();
            var results = new List<SearchResultItem>((int)Math.Min(count, int.MaxValue));
            var pathBuffer = new StringBuilder(32768);
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pathBuffer.Clear();
                if (NativeMethods.Everything_GetResultFullPathNameW(index, pathBuffer, (uint)pathBuffer.Capacity) == 0)
                {
                    continue;
                }

                var fullPath = pathBuffer.ToString();
                var isFolder = NativeMethods.Everything_IsFolderResult(index);
                long? size = null;
                if (!isFolder && NativeMethods.Everything_GetResultSize(index, out var nativeSize))
                {
                    size = nativeSize;
                }

                DateTimeOffset? modifiedAt = null;
                if (NativeMethods.Everything_GetResultDateModified(index, out var fileTime) && fileTime > 0)
                {
                    modifiedAt = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
                }

                results.Add(new SearchResultItem(
                    Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    fullPath,
                    Path.GetDirectoryName(fullPath) ?? string.Empty,
                    isFolder ? SearchItemKind.Folder : SearchItemKind.File,
                    size,
                    modifiedAt));
            }

            return new EverythingSearchResponse(SortFoldersFirst(results, query.Sort), total, Available());
        }
        catch (DllNotFoundException)
        {
            return new EverythingSearchResponse([], 0, new EverythingAvailability(
                EverythingAvailabilityKind.SdkMissing,
                "Everything64.dll을 찾을 수 없습니다."));
        }
        finally
        {
            NativeMethods.TryReset();
        }
    }

    private static IReadOnlyList<SearchResultItem> SortFoldersFirst(IEnumerable<SearchResultItem> results, SearchSort sort)
    {
        var foldersFirst = results.OrderBy(item => item.Kind == SearchItemKind.Folder ? 0 : 1);
        return sort switch
        {
            SearchSort.NameDescending => foldersFirst.ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            SearchSort.PathAscending => foldersFirst.ThenBy(item => item.ParentPath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            SearchSort.PathDescending => foldersFirst.ThenByDescending(item => item.ParentPath, StringComparer.OrdinalIgnoreCase).ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            SearchSort.ModifiedAscending => foldersFirst.ThenBy(item => item.ModifiedAt).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            SearchSort.ModifiedDescending => foldersFirst.ThenByDescending(item => item.ModifiedAt).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            SearchSort.SizeAscending => foldersFirst.ThenBy(item => item.Size).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            SearchSort.SizeDescending => foldersFirst.ThenByDescending(item => item.Size).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => foldersFirst.ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static uint ToNativeSort(SearchSort sort)
    {
        return sort switch
        {
            SearchSort.NameDescending => 2,
            SearchSort.PathAscending => 3,
            SearchSort.PathDescending => 4,
            SearchSort.SizeAscending => 5,
            SearchSort.SizeDescending => 6,
            SearchSort.ModifiedAscending => 13,
            SearchSort.ModifiedDescending => 14,
            _ => 1
        };
    }

    private static EverythingAvailability Available() => new(
        EverythingAvailabilityKind.Available,
        "Everything 검색을 사용할 수 있습니다.");

    public void Dispose()
    {
        disposed = true;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport(DllName, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Everything_IsDBLoaded();

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern void Everything_Reset();

        [DllImport(DllName, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern void Everything_SetSearchW(string searchString);

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern void Everything_SetMatchPath([MarshalAs(UnmanagedType.Bool)] bool enable);

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern void Everything_SetMatchCase([MarshalAs(UnmanagedType.Bool)] bool enable);

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern void Everything_SetMatchWholeWord([MarshalAs(UnmanagedType.Bool)] bool enable);

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern void Everything_SetRegex([MarshalAs(UnmanagedType.Bool)] bool enable);

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern void Everything_SetMax(uint maxResults);

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern void Everything_SetOffset(uint offset);

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern void Everything_SetRequestFlags(uint requestFlags);

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern void Everything_SetSort(uint sortType);

        [DllImport(DllName, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Everything_QueryW([MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern uint Everything_GetLastError();

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern uint Everything_GetNumResults();

        [DllImport(DllName, ExactSpelling = true)]
        internal static extern uint Everything_GetTotResults();

        [DllImport(DllName, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Everything_IsFolderResult(uint index);

        [DllImport(DllName, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint Everything_GetResultFullPathNameW(uint index, StringBuilder buffer, uint maxCount);

        [DllImport(DllName, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Everything_GetResultSize(uint index, out long size);

        [DllImport(DllName, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Everything_GetResultDateModified(uint index, out long fileTime);

        internal static void TryReset()
        {
            try
            {
                Everything_Reset();
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }
}

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Waffle.Browse.Core.Search;
using Waffle.Browse.Core.Search.Indexing;

namespace Waffle.Browse.Core.Tests.Search;

internal static class NamedPipeFileIndexSourceTests
{
    public static void FramingRoundTripsJsonMessages()
    {
        using var stream = new MemoryStream();
        var expected = new NamedPipeFileIndexProtocol.PipePathMessage("root", @"C:\자료");

        NamedPipeJsonFraming.WriteMessageAsync(stream, expected).AsTask().GetAwaiter().GetResult();
        stream.Position = 0;
        var actual = NamedPipeJsonFraming
            .ReadMessageAsync<NamedPipeFileIndexProtocol.PipePathMessage>(stream)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        TestAssert.Equal(expected, actual, "Length-prefixed JSON framing should round-trip Unicode payloads");
    }

    public static void FramingRejectsOversizedFrames()
    {
        using var stream = new MemoryStream();
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, NamedPipeJsonFraming.MaximumFrameBytes + 1);
        stream.Write(prefix);
        stream.Position = 0;

        ExpectInvalidData(
            () => NamedPipeJsonFraming
                .ReadMessageAsync<NamedPipeFileIndexProtocol.PipePathMessage>(stream)
                .AsTask()
                .GetAwaiter()
                .GetResult(),
            "Frames larger than 64 KiB must be rejected before allocating their payload");
    }

    public static void FramingRejectsProtocolVersionMismatch()
    {
        var logicalMessage = JsonSerializer.SerializeToUtf8Bytes(
            new NamedPipeFileIndexProtocol.PipePathMessage("root", @"C:\"),
            NamedPipeJsonFraming.SerializerOptions);
        var frame = new PipeChunkFrame(
            NamedPipeJsonFraming.ProtocolVersion + 1,
            0,
            true,
            Convert.ToBase64String(logicalMessage));
        var frameBytes = JsonSerializer.SerializeToUtf8Bytes(frame, NamedPipeJsonFraming.SerializerOptions);

        using var stream = new MemoryStream();
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, frameBytes.Length);
        stream.Write(prefix);
        stream.Write(frameBytes);
        stream.Position = 0;

        ExpectInvalidData(
            () => NamedPipeJsonFraming
                .ReadMessageAsync<NamedPipeFileIndexProtocol.PipePathMessage>(stream)
                .AsTask()
                .GetAwaiter()
                .GetResult(),
            "Every physical frame must carry the supported protocol version");
    }

    public static void ResultEntriesAreChunkedBelowTheFrameCap()
    {
        var longName = new string('가', 32_000);
        var expected = new FileIndexEntry(
            @"C:\" + longName,
            longName,
            @"C:\",
            SearchItemKind.File,
            123,
            DateTimeOffset.Parse("2026-07-15T01:02:03Z"),
            @"\\?\Volume{chunk-test}\",
            new FileReferenceId(99, 0, FileReferenceIdWidth.Bits128));
        var result = new FileIndexBuildResult([expected], [], [], 7);

        using var stream = new MemoryStream();
        NamedPipeFileIndexProtocol.WriteResultAsync(stream, result).AsTask().GetAwaiter().GetResult();

        var physicalFrameCount = CountPhysicalFrames(stream);
        TestAssert.True(
            physicalFrameCount >= 3,
            "A large entry should span multiple capped physical frames in addition to the result header");

        stream.Position = 0;
        var actual = NamedPipeFileIndexProtocol.ReadResultAsync(stream).AsTask().GetAwaiter().GetResult();
        TestAssert.Equal(1, actual.Entries.Count, "Chunked entry streams should preserve entry boundaries");
        TestAssert.Equal(expected, actual.Entries[0], "Chunked entry streams should preserve every field");
        TestAssert.True(
            actual.Entries[0].FileReferenceNumber!.Value.Is128Bit,
            "Pipe entry streaming should preserve a 128-bit file ID whose high half is zero");
        TestAssert.Equal(7L, actual.SkippedPathCount, "Result metadata should survive entry streaming");
    }

    public static void ResultEntriesAreDynamicallyBatched()
    {
        var expected = CreateEntries(300);
        var result = new FileIndexBuildResult(expected, [], []);

        using var stream = new MemoryStream();
        NamedPipeFileIndexProtocol.WriteResultAsync(stream, result).AsTask().GetAwaiter().GetResult();

        TestAssert.True(
            CountPhysicalFrames(stream) < 25,
            "Small result entries should share dynamically sized logical messages");
        stream.Position = 0;
        var actual = NamedPipeFileIndexProtocol.ReadResultAsync(stream).AsTask().GetAwaiter().GetResult();
        TestAssert.Equal(expected.Count, actual.Entries.Count, "Every batched result entry should round-trip");
        TestAssert.Equal(expected[0], actual.Entries[0], "The first batched result entry should be preserved");
        TestAssert.Equal(expected[^1], actual.Entries[^1], "The last batched result entry should be preserved");
    }

    public static void RefreshBaselineEntriesAreDynamicallyBatched()
    {
        var expected = CreateEntries(300);
        var baseline = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(
                FileIndexBuildState.Ready,
                9,
                expected.Count,
                DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
                []),
            expected);

        using var stream = new MemoryStream();
        NamedPipeFileIndexProtocol.WriteRequestAsync(stream, [@"C:\"], baseline)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        TestAssert.True(
            CountPhysicalFrames(stream) < 30,
            "Small refresh-baseline entries should share dynamically sized logical messages");
        stream.Position = 0;
        var actual = NamedPipeFileIndexProtocol.ReadRequestAsync(stream).AsTask().GetAwaiter().GetResult();
        TestAssert.Equal(expected.Count, actual.Baseline!.Entries.Count, "Every batched baseline entry should round-trip");
        TestAssert.Equal(expected[0], actual.Baseline.Entries[0], "The first batched baseline entry should be preserved");
        TestAssert.Equal(expected[^1], actual.Baseline.Entries[^1], "The last batched baseline entry should be preserved");
    }

    public static void EntryBatchItemCountIsBoundedForEarlyFrames()
    {
        var expected = CreateEntries(600);
        using var stream = new MemoryStream();
        NamedPipeFileIndexProtocol.WriteResultAsync(
                stream,
                new FileIndexBuildResult(expected, [], []))
            .AsTask()
            .GetAwaiter()
            .GetResult();
        stream.Position = 0;

        var header = NamedPipeJsonFraming
            .ReadMessageAsync<NamedPipeFileIndexProtocol.PipeResultHeader>(stream)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        var entryCount = 0;
        var batchCount = 0;
        while (entryCount < header.EntryCount)
        {
            var batch = NamedPipeJsonFraming
                .ReadMessageAsync<NamedPipeFileIndexProtocol.PipeEntryBatchMessage>(stream)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            TestAssert.True(
                batch.Entries.Count <= 256,
                "A writer batch should be capped so the first frame is produced promptly");
            entryCount += batch.Entries.Count;
            batchCount++;
        }

        TestAssert.Equal(expected.Count, entryCount, "Bounded batches should still include every entry");
        TestAssert.True(batchCount >= 3, "Six hundred entries should require at least three bounded batches");
    }

    public static void ResultEntryValidationOccursWhileStreaming()
    {
        using var stream = new MemoryStream();
        var result = new FileIndexBuildResult(
            [CreateEntries(1)[0], CreateInvalidEntry()],
            [],
            []);

        ExpectInvalidData(
            () => NamedPipeFileIndexProtocol.WriteResultAsync(stream, result)
                .AsTask()
                .GetAwaiter()
                .GetResult(),
            "Invalid result entries should be rejected while their batch is produced");
        TestAssert.True(stream.Length > 0, "Result metadata should be written before entry validation reaches later items");
        stream.Position = 0;
        var header = NamedPipeJsonFraming
            .ReadMessageAsync<NamedPipeFileIndexProtocol.PipeResultHeader>(stream)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        TestAssert.Equal(2, header.EntryCount, "The result header should advertise the entry count before streaming");
    }

    public static void BaselineEntryValidationOccursWhileStreaming()
    {
        var entries = new FileIndexEntry[] { CreateEntries(1)[0], CreateInvalidEntry() };
        var baseline = new FileIndexSnapshot(
            FileIndexSnapshot.CurrentFormatVersion,
            new FileIndexState(
                FileIndexBuildState.Ready,
                1,
                entries.Length,
                DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
                []),
            entries);
        using var stream = new MemoryStream();

        ExpectInvalidData(
            () => NamedPipeFileIndexProtocol.WriteRequestAsync(stream, [@"C:\"], baseline)
                .AsTask()
                .GetAwaiter()
                .GetResult(),
            "Invalid baseline entries should be rejected while their batch is produced");
        TestAssert.True(stream.Length > 0, "Request metadata should be written before baseline entry validation");
        stream.Position = 0;
        _ = NamedPipeJsonFraming
            .ReadMessageAsync<NamedPipeFileIndexProtocol.PipeRequestHeader>(stream)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        _ = NamedPipeJsonFraming
            .ReadMessageAsync<NamedPipeFileIndexProtocol.PipePathMessage>(stream)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        var header = NamedPipeJsonFraming
            .ReadMessageAsync<NamedPipeFileIndexProtocol.PipeBaselineHeader>(stream)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        TestAssert.Equal(2, header.EntryCount, "The baseline header should be written before entries are streamed");
    }

    public static void EntryBatchesRejectAdvertisedCountOverflow()
    {
        using var stream = new MemoryStream();
        NamedPipeJsonFraming.WriteMessageAsync(
                stream,
                new NamedPipeFileIndexProtocol.PipeResultHeader(
                    "result",
                    "success",
                    0,
                    0,
                    1,
                    0,
                    null,
                    null))
            .AsTask()
            .GetAwaiter()
            .GetResult();
        NamedPipeJsonFraming.WriteMessageAsync(
                stream,
                new NamedPipeFileIndexProtocol.PipeEntryBatchMessage(
                    "entries",
                    CreateEntries(2)
                        .Select(NamedPipeFileIndexProtocol.PipeEntryMessage.FromEntry)
                        .ToArray()))
            .AsTask()
            .GetAwaiter()
            .GetResult();
        stream.Position = 0;

        ExpectInvalidData(
            () => NamedPipeFileIndexProtocol.ReadResultAsync(stream).AsTask().GetAwaiter().GetResult(),
            "Entry batches must not contain more entries than the result header advertises");
    }

    public static void ResultReaderSkipsProgressHeartbeats()
    {
        var expected = new FileIndexBuildResult(CreateEntries(1), [], []);
        using var stream = new MemoryStream();
        NamedPipeFileIndexProtocol.WriteProgressAsync(stream).AsTask().GetAwaiter().GetResult();
        NamedPipeFileIndexProtocol.WriteProgressAsync(stream).AsTask().GetAwaiter().GetResult();
        NamedPipeFileIndexProtocol.WriteResultAsync(stream, expected).AsTask().GetAwaiter().GetResult();
        stream.Position = 0;

        var actual = NamedPipeFileIndexProtocol.ReadResultAsync(stream).AsTask().GetAwaiter().GetResult();
        TestAssert.Equal(
            expected.Entries.Single(),
            actual.Entries.Single(),
            "Progress heartbeats should keep the stream alive without becoming a result");
    }

    public static void FramingReadIdleTimeoutThrowsTimeoutException()
    {
        using var stream = new BlockingStream();
        ExpectTimeout(
            () => NamedPipeJsonFraming
                .ReadMessageAsync<NamedPipeFileIndexProtocol.PipePathMessage>(
                    stream,
                    idleTimeout: TimeSpan.FromMilliseconds(25))
                .AsTask()
                .GetAwaiter()
                .GetResult(),
            "An idle physical-frame read should surface as TimeoutException");
    }

    public static void FramingWriteIdleTimeoutThrowsTimeoutException()
    {
        using var stream = new BlockingStream();
        ExpectTimeout(
            () => NamedPipeJsonFraming
                .WriteMessageAsync(
                    stream,
                    new NamedPipeFileIndexProtocol.PipePathMessage("root", @"C:\"),
                    idleTimeout: TimeSpan.FromMilliseconds(25))
                .AsTask()
                .GetAwaiter()
                .GetResult(),
            "An idle physical-frame write should surface as TimeoutException");
    }

    public static void FramingCallerCancellationRemainsOperationCanceled()
    {
        using var stream = new BlockingStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            NamedPipeJsonFraming
                .ReadMessageAsync<NamedPipeFileIndexProtocol.PipePathMessage>(
                    stream,
                    cancellation.Token,
                    TimeSpan.FromSeconds(1))
                .AsTask()
                .GetAwaiter()
                .GetResult();
            throw new InvalidOperationException("Caller cancellation should not be reported as an idle timeout.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static void UnavailableHostThrowsNotSupported()
    {
        var client = new NamedPipeFileIndexSource(
            $"Waffle.Browse.Indexer.Missing.{Guid.NewGuid():N}",
            25,
            enforceProductionSecurity: false);
        try
        {
            client.BuildAsync([@"C:\"]).GetAwaiter().GetResult();
            throw new InvalidOperationException("An unavailable helper should be eligible for recursive fallback.");
        }
        catch (NotSupportedException)
        {
        }
    }

    public static void SuccessfulLaunchReconnectsAndCanRestartAfterIdleExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"Waffle.Browse.Indexer.Launch.Tests.{Guid.NewGuid():N}";
        var expected = new FileIndexBuildResult([], [], []);
        var fake = new FakeSnapshotSource(expected);
        var host = new NamedPipeFileIndexHost(
            fake,
            pipeName,
            enforceProductionSecurity: false,
            idleTimeout: TimeSpan.FromMilliseconds(150));
        Task? hostTask = null;
        var launcher = new StubIndexerProcessLauncher(() =>
        {
            hostTask = host.RunAsync();
            return new IndexerLaunchResult(
                IndexerLaunchStatus.Started,
                "test helper started");
        });
        var client = new NamedPipeFileIndexSource(
            pipeName,
            connectTimeoutMilliseconds: 25,
            operationTimeoutMilliseconds: 5_000,
            enforceProductionSecurity: false,
            launcher,
            launchConnectTimeoutMilliseconds: 5_000);

        _ = client.BuildAsync([@"C:\"]).GetAwaiter().GetResult();
        TestAssert.Equal(1, launcher.CallCount, "A missing helper should trigger one launch before reconnecting");
        var firstHostTask = hostTask
            ?? throw new InvalidOperationException("The first helper launch did not create a host task.");
        TestAssert.True(
            firstHostTask.Wait(TimeSpan.FromSeconds(5)),
            "The launched test helper should exit after becoming idle");
        firstHostTask.GetAwaiter().GetResult();

        _ = client.BuildAsync([@"C:\"]).GetAwaiter().GetResult();
        TestAssert.Equal(2, launcher.CallCount, "A successfully used helper may be launched again after its idle exit");
        TestAssert.Equal(2, fake.BuildCallCount, "Each launch should reconnect with a fresh pipe and complete the request");
        var secondHostTask = hostTask
            ?? throw new InvalidOperationException("The second helper launch did not create a host task.");
        TestAssert.True(
            secondHostTask.Wait(TimeSpan.FromSeconds(5)),
            "The relaunched helper should also honor its idle timeout");
        secondHostTask.GetAwaiter().GetResult();
    }

    public static void DeclinedLaunchIsCoalescedAcrossClients()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"Waffle.Browse.Indexer.Declined.Tests.{Guid.NewGuid():N}";
        var launcher = new StubIndexerProcessLauncher(() => new IndexerLaunchResult(
            IndexerLaunchStatus.Declined,
            "test user declined"));
        var coordinator = new IndexerLaunchCoordinator();
        var first = new NamedPipeFileIndexSource(
            pipeName,
            connectTimeoutMilliseconds: 25,
            operationTimeoutMilliseconds: 1_000,
            enforceProductionSecurity: false,
            launcher,
            launchConnectTimeoutMilliseconds: 25,
            coordinator);
        var second = new NamedPipeFileIndexSource(
            pipeName,
            connectTimeoutMilliseconds: 25,
            operationTimeoutMilliseconds: 1_000,
            enforceProductionSecurity: false,
            launcher,
            launchConnectTimeoutMilliseconds: 25,
            coordinator);

        var outcomes = Task.WhenAll(
                ReturnsNotSupportedAsync(first.BuildAsync([@"C:\"])),
                ReturnsNotSupportedAsync(second.BuildAsync([@"C:\"])))
            .GetAwaiter()
            .GetResult();

        TestAssert.True(outcomes.All(value => value), "Every declined helper request should remain eligible for recursive fallback");
        TestAssert.Equal(1, launcher.CallCount, "Concurrent clients must coalesce a declined UAC prompt for the app session");
    }

    public static void ConcurrentSuccessfulRequestsShareOneHelperLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"Waffle.Browse.Indexer.Concurrent.Tests.{Guid.NewGuid():N}";
        var source = new GatedSnapshotSource(new FileIndexBuildResult([], [], []));
        var host = new NamedPipeFileIndexHost(
            source,
            pipeName,
            enforceProductionSecurity: false,
            idleTimeout: TimeSpan.FromSeconds(30));
        using var lifetimeCancellation = new CancellationTokenSource();
        Task? hostTask = null;
        var launcher = new StubIndexerProcessLauncher(() =>
        {
            hostTask = host.RunAsync(lifetimeCancellation.Token);
            return new IndexerLaunchResult(
                IndexerLaunchStatus.Started,
                "test helper started");
        });
        var coordinator = new IndexerLaunchCoordinator();
        var first = new NamedPipeFileIndexSource(
            pipeName,
            connectTimeoutMilliseconds: 25,
            operationTimeoutMilliseconds: 5_000,
            enforceProductionSecurity: false,
            launcher,
            launchConnectTimeoutMilliseconds: 5_000,
            coordinator);
        var second = new NamedPipeFileIndexSource(
            pipeName,
            connectTimeoutMilliseconds: 25,
            operationTimeoutMilliseconds: 5_000,
            enforceProductionSecurity: false,
            launcher,
            launchConnectTimeoutMilliseconds: 5_000,
            coordinator);

        try
        {
            var firstOperation = first.BuildAsync([@"C:\"]);
            TestAssert.True(
                source.BuildStarted.Task.Wait(TimeSpan.FromSeconds(5)),
                "The first request should reach the launched helper");

            var secondOperation = second.BuildAsync([@"C:\"]);
            TestAssert.Equal(1, launcher.CallCount, "A queued request must not launch a second helper while the first is busy");

            source.Complete();
            Task.WhenAll(firstOperation, secondOperation).GetAwaiter().GetResult();
            TestAssert.Equal(1, launcher.CallCount, "Both requests should share the original helper process");
            TestAssert.Equal(2, source.BuildCallCount, "The helper should serve both serialized requests");
        }
        finally
        {
            source.Complete();
            lifetimeCancellation.Cancel();
            hostTask?.GetAwaiter().GetResult();
        }
    }

    public static void CallerCancellationDoesNotLaunchHelper()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var launcher = new StubIndexerProcessLauncher(() => new IndexerLaunchResult(
            IndexerLaunchStatus.Started,
            "unexpected launch"));
        var client = new NamedPipeFileIndexSource(
            $"Waffle.Browse.Indexer.Cancelled.Tests.{Guid.NewGuid():N}",
            connectTimeoutMilliseconds: 25,
            operationTimeoutMilliseconds: 1_000,
            enforceProductionSecurity: false,
            launcher,
            launchConnectTimeoutMilliseconds: 25);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            _ = client.BuildAsync([@"C:\"], cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Caller cancellation should stop before the UAC launcher runs.");
        }
        catch (OperationCanceledException)
        {
        }

        TestAssert.Equal(0, launcher.CallCount, "Caller cancellation must never open a UAC prompt");
    }

    public static void CancellationAfterLaunchSuppressesReplacementPrompt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var launcher = new StubIndexerProcessLauncher(() =>
        {
            cancellation.Cancel();
            return new IndexerLaunchResult(
                IndexerLaunchStatus.Started,
                "test helper startup pending");
        });
        var client = new NamedPipeFileIndexSource(
            $"Waffle.Browse.Indexer.CancelledAfterLaunch.Tests.{Guid.NewGuid():N}",
            connectTimeoutMilliseconds: 25,
            operationTimeoutMilliseconds: 1_000,
            enforceProductionSecurity: false,
            launcher,
            launchConnectTimeoutMilliseconds: 25);

        try
        {
            _ = client.BuildAsync([@"C:\"], cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancellation after Process.Start should remain caller cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        TestAssert.True(
            ReturnsNotSupportedAsync(client.BuildAsync([@"C:\"]))
                .GetAwaiter()
                .GetResult(),
            "A later request should fall back while the cancelled launch remains unresolved");
        TestAssert.Equal(1, launcher.CallCount, "Cancellation after launch must not create a second UAC prompt");
    }

    public static void UntrustedConnectedServerDoesNotLaunchReplacement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"Waffle.Browse.Indexer.Untrusted.Tests.{Guid.NewGuid():N}";
        var host = new NamedPipeFileIndexHost(
            new FakeSnapshotSource(new FileIndexBuildResult([], [], [])),
            pipeName,
            enforceProductionSecurity: false,
            idleTimeout: TimeSpan.FromSeconds(30));
        using var lifetimeCancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(lifetimeCancellation.Token);
        var launcher = new StubIndexerProcessLauncher(() => new IndexerLaunchResult(
            IndexerLaunchStatus.Started,
            "unexpected launch"));
        var client = new NamedPipeFileIndexSource(
            pipeName,
            connectTimeoutMilliseconds: 5_000,
            operationTimeoutMilliseconds: 1_000,
            enforceProductionSecurity: true,
            launcher,
            launchConnectTimeoutMilliseconds: 25);

        try
        {
            TestAssert.True(
                ReturnsNotSupportedAsync(client.BuildAsync([@"C:\"]))
                    .GetAwaiter()
                    .GetResult(),
                "A connected server with the wrong process identity must be rejected");
            TestAssert.Equal(0, launcher.CallCount, "Server identity failure must not launch another elevated process");
        }
        finally
        {
            lifetimeCancellation.Cancel();
            hostTask.GetAwaiter().GetResult();
        }
    }

    public static void LaunchedUntrustedServerSuppressesReplacementPrompt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"Waffle.Browse.Indexer.LaunchedUntrusted.Tests.{Guid.NewGuid():N}";
        var host = new NamedPipeFileIndexHost(
            new FakeSnapshotSource(new FileIndexBuildResult([], [], [])),
            pipeName,
            enforceProductionSecurity: false,
            idleTimeout: TimeSpan.FromSeconds(30));
        using var lifetimeCancellation = new CancellationTokenSource();
        Task? hostTask = null;
        var launcher = new StubIndexerProcessLauncher(() =>
        {
            hostTask = host.RunAsync(lifetimeCancellation.Token);
            return new IndexerLaunchResult(
                IndexerLaunchStatus.Started,
                "test untrusted helper started");
        });
        var client = new NamedPipeFileIndexSource(
            pipeName,
            connectTimeoutMilliseconds: 25,
            operationTimeoutMilliseconds: 1_000,
            enforceProductionSecurity: true,
            launcher,
            launchConnectTimeoutMilliseconds: 5_000);

        try
        {
            TestAssert.True(
                ReturnsNotSupportedAsync(client.BuildAsync([@"C:\"]))
                    .GetAwaiter()
                    .GetResult(),
                "A launched server with the wrong identity must be rejected");
            lifetimeCancellation.Cancel();
            hostTask?.GetAwaiter().GetResult();

            TestAssert.True(
                ReturnsNotSupportedAsync(client.BuildAsync([@"C:\"]))
                    .GetAwaiter()
                    .GetResult(),
                "A rejected launched peer should leave recursive fallback available");
            TestAssert.Equal(1, launcher.CallCount, "A rejected launched peer must not trigger a replacement UAC prompt");
        }
        finally
        {
            lifetimeCancellation.Cancel();
            hostTask?.GetAwaiter().GetResult();
        }
    }

    public static void HostExitsAfterIdleTimeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var host = new NamedPipeFileIndexHost(
            new FakeSnapshotSource(new FileIndexBuildResult([], [], [])),
            $"Waffle.Browse.Indexer.Idle.Tests.{Guid.NewGuid():N}",
            enforceProductionSecurity: false,
            idleTimeout: TimeSpan.FromMilliseconds(100));

        var hostTask = host.RunAsync();

        TestAssert.True(
            hostTask.Wait(TimeSpan.FromSeconds(5)),
            "An unused helper host should exit after its idle timeout");
        hostTask.GetAwaiter().GetResult();
    }

    public static void CompletedConnectionResetsHostIdleTimeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var expected = new FileIndexBuildResult([], [], []);
        var source = new GatedSnapshotSource(expected);
        var pipeName = $"Waffle.Browse.Indexer.Idle.Reset.Tests.{Guid.NewGuid():N}";
        var idleTimeout = TimeSpan.FromSeconds(1);
        var host = new NamedPipeFileIndexHost(
            source,
            pipeName,
            enforceProductionSecurity: false,
            idleTimeout);
        var client = new NamedPipeFileIndexSource(
            pipeName,
            5_000,
            enforceProductionSecurity: false);
        using var lifetimeCancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(lifetimeCancellation.Token);

        try
        {
            var clientTask = client.BuildAsync([@"C:\"]);
            TestAssert.True(
                source.BuildStarted.Task.Wait(TimeSpan.FromSeconds(5)),
                "The host should start the connected request");

            Task.Delay(idleTimeout + TimeSpan.FromMilliseconds(250)).GetAwaiter().GetResult();
            TestAssert.False(
                hostTask.IsCompleted,
                "The helper idle timeout must not cancel an active connection");

            source.Complete();
            _ = clientTask.GetAwaiter().GetResult();
            Task.Delay(TimeSpan.FromMilliseconds(150)).GetAwaiter().GetResult();
            TestAssert.False(
                hostTask.IsCompleted,
                "A completed connection should start a fresh helper idle timeout");

            TestAssert.True(
                hostTask.Wait(TimeSpan.FromSeconds(5)),
                "The helper should exit after the reset idle timeout elapses");
            hostTask.GetAwaiter().GetResult();
        }
        finally
        {
            source.Complete();
            lifetimeCancellation.Cancel();
            hostTask.GetAwaiter().GetResult();
        }
    }

    public static void LifetimeCancellationStopsHostBeforeIdleTimeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var host = new NamedPipeFileIndexHost(
            new FakeSnapshotSource(new FileIndexBuildResult([], [], [])),
            $"Waffle.Browse.Indexer.Lifetime.Tests.{Guid.NewGuid():N}",
            enforceProductionSecurity: false,
            idleTimeout: TimeSpan.FromSeconds(30));
        using var lifetimeCancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(lifetimeCancellation.Token);

        lifetimeCancellation.Cancel();

        TestAssert.True(
            hostTask.Wait(TimeSpan.FromSeconds(5)),
            "Lifetime cancellation should stop the helper without waiting for its idle timeout");
        hostTask.GetAwaiter().GetResult();
    }

    public static void SameProcessHostBuildsThroughNamedPipe()
    {
        var pipeName = $"Waffle.Browse.Indexer.Tests.{Guid.NewGuid():N}";
        var expected = new FileIndexBuildResult(
            [
                new FileIndexEntry(
                    @"C:\자료\문서.txt",
                    "문서.txt",
                    @"C:\자료",
                    SearchItemKind.File,
                    42,
                    DateTimeOffset.Parse("2026-07-15T01:02:03Z"))
            ],
            [],
            []);
        var fake = new FakeSnapshotSource(expected);
        var host = new NamedPipeFileIndexHost(
            fake,
            pipeName,
            enforceProductionSecurity: false);
        var client = new NamedPipeFileIndexSource(
            pipeName,
            5_000,
            enforceProductionSecurity: false);
        using var cancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(cancellation.Token);

        try
        {
            using (var malformed = new NamedPipeClientStream(
                       ".",
                       pipeName,
                       PipeDirection.InOut,
                       PipeOptions.Asynchronous))
            {
                malformed.Connect(5_000);
                var invalidPrefix = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(
                    invalidPrefix,
                    NamedPipeJsonFraming.MaximumFrameBytes + 1);
                malformed.Write(invalidPrefix);
            }

            var actual = client.BuildAsync([@"C:\"]).GetAwaiter().GetResult();
            TestAssert.Equal(1, fake.BuildCallCount, "The host should dispatch build requests to its source");
            TestAssert.Equal(expected.Entries[0], actual.Entries.Single(), "The client should receive the host build result");

            var baseline = new FileIndexSnapshot(
                FileIndexSnapshot.CurrentFormatVersion,
                new FileIndexState(
                    FileIndexBuildState.Ready,
                    3,
                    expected.Entries.Count,
                    DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
                    []),
                expected.Entries);
            var refreshed = client.RefreshAsync([@"C:\"], baseline).GetAwaiter().GetResult();
            TestAssert.Equal(1, fake.RefreshCallCount, "The host should dispatch refresh requests to its snapshot source");
            TestAssert.Equal(
                baseline.Entries[0],
                fake.LastBaseline!.Entries[0],
                "Refresh requests should stream the complete baseline through the pipe");
            TestAssert.Equal(expected.Entries[0], refreshed.Entries.Single(), "The client should receive the host refresh result");
        }
        finally
        {
            cancellation.Cancel();
            hostTask.GetAwaiter().GetResult();
        }
    }

    private static int CountPhysicalFrames(MemoryStream stream)
    {
        var buffer = stream.GetBuffer();
        var position = 0;
        var count = 0;
        while (position < stream.Length)
        {
            if (position + sizeof(int) > stream.Length)
            {
                throw new InvalidDataException("Physical frame prefix is truncated.");
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(position, sizeof(int)));
            TestAssert.True(
                length > 0 && length <= NamedPipeJsonFraming.MaximumFrameBytes,
                "Every physical frame must stay within the 64 KiB cap");
            position += sizeof(int) + length;
            count++;
        }

        TestAssert.Equal((long)position, stream.Length, "Physical frame lengths should cover the entire stream");
        return count;
    }

    private static IReadOnlyList<FileIndexEntry> CreateEntries(int count) => Enumerable
        .Range(0, count)
        .Select(index => new FileIndexEntry(
            $@"C:\자료\문서-{index:D4}.txt",
            $"문서-{index:D4}.txt",
            @"C:\자료",
            SearchItemKind.File,
            index,
            DateTimeOffset.Parse("2026-07-15T01:02:03Z"),
            @"\\?\Volume{batch-test}\",
            new FileReferenceId((ulong)index + 1)))
        .ToArray();

    private static FileIndexEntry CreateInvalidEntry() => new(
        string.Empty,
        string.Empty,
        string.Empty,
        SearchItemKind.File,
        null,
        null);

    private static void ExpectInvalidData(Action action, string message)
    {
        try
        {
            action();
            throw new InvalidOperationException(message);
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void ExpectTimeout(Action action, string message)
    {
        try
        {
            action();
            throw new InvalidOperationException(message);
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task<bool> ReturnsNotSupportedAsync(Task<FileIndexBuildResult> operation)
    {
        try
        {
            _ = await operation.ConfigureAwait(false);
            return false;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FakeSnapshotSource(FileIndexBuildResult result) : IFileIndexSnapshotSource
    {
        public int BuildCallCount { get; private set; }

        public int RefreshCallCount { get; private set; }

        public FileIndexSnapshot? LastBaseline { get; private set; }

        public Task<FileIndexBuildResult> BuildAsync(
            IReadOnlyList<string> roots,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildCallCount++;
            return Task.FromResult(result);
        }

        public Task<FileIndexBuildResult> RefreshAsync(
            IReadOnlyList<string> roots,
            FileIndexSnapshot baseline,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCallCount++;
            LastBaseline = baseline;
            return Task.FromResult(result);
        }
    }

    private sealed class GatedSnapshotSource(FileIndexBuildResult result) : IFileIndexSnapshotSource
    {
        private readonly TaskCompletionSource<FileIndexBuildResult> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int buildCallCount;

        public int BuildCallCount => Volatile.Read(ref buildCallCount);

        public TaskCompletionSource<bool> BuildStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<FileIndexBuildResult> BuildAsync(
            IReadOnlyList<string> roots,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref buildCallCount);
            BuildStarted.TrySetResult(true);
            return completion.Task.WaitAsync(cancellationToken);
        }

        public Task<FileIndexBuildResult> RefreshAsync(
            IReadOnlyList<string> roots,
            FileIndexSnapshot baseline,
            CancellationToken cancellationToken = default) =>
            BuildAsync(roots, cancellationToken);

        public void Complete() => completion.TrySetResult(result);
    }

    private sealed class StubIndexerProcessLauncher(
        Func<IndexerLaunchResult> launch) : IIndexerProcessLauncher
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public IndexerLaunchResult Launch()
        {
            Interlocked.Increment(ref callCount);
            return launch();
        }
    }
}

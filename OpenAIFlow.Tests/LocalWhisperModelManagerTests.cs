using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace ChatGptDictationBridge.Tests;

public sealed class LocalWhisperModelManagerTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "OpenAIFlow.ModelManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void OfficialDescriptorIsRevisionAndIntegrityPinned()
    {
        var descriptor = LocalWhisperModelDescriptor.LargeV3Turbo;

        Assert.Equal("ggml-large-v3-turbo.bin", descriptor.FileName);
        Assert.Equal(
            "5359861c739e955e79d9a303bcbc70fb988958b1",
            descriptor.Revision);
        Assert.Equal(1_624_555_275, descriptor.ExpectedLength);
        Assert.Equal(
            "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",
            descriptor.Sha256);
        Assert.Equal(
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/" +
            "5359861c739e955e79d9a303bcbc70fb988958b1/" +
            "ggml-large-v3-turbo.bin",
            descriptor.DownloadUri.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadReportsProgressAndPublishesOnlyVerifiedModel()
    {
        var payload = Enumerable.Range(0, 400_000)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var descriptor = CreateDescriptor(payload);
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
        using var httpClient = new HttpClient(handler);
        using var manager = new LocalWhisperModelManager(
            _testDirectory,
            httpClient,
            descriptor);
        var progress = new ProgressCollector();

        var modelPath = await manager.EnsureModelAsync(progress);

        Assert.Equal(manager.ModelPath, modelPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(modelPath));
        Assert.True(File.Exists(manager.VerificationPath));
        Assert.False(File.Exists(manager.PartialPath));
        Assert.Equal(1, handler.RequestCount);
        var downloadUpdates = progress.Values
            .Where(value => value.Stage == LocalWhisperModelProgressStage.Downloading)
            .ToArray();
        Assert.NotEmpty(downloadUpdates);
        Assert.Equal(0, downloadUpdates[0].BytesProcessed);
        Assert.Equal(payload.Length, downloadUpdates[^1].BytesProcessed);
        Assert.True(downloadUpdates
            .Zip(downloadUpdates.Skip(1))
            .All(pair => pair.First.BytesProcessed <= pair.Second.BytesProcessed));
        var ready = Assert.Single(
            progress.Values,
            value => value.Stage == LocalWhisperModelProgressStage.Ready);
        Assert.False(ready.FromCache);
        Assert.Equal(100d, ready.Percentage);
    }

    [Fact]
    public async Task VerifiedCacheIsReusedWithoutAnotherHttpRequest()
    {
        var payload = "small deterministic whisper model"u8.ToArray();
        var descriptor = CreateDescriptor(payload);
        var initialHandler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
        using (var initialClient = new HttpClient(initialHandler))
        using (var initialManager = new LocalWhisperModelManager(
                   _testDirectory,
                   initialClient,
                   descriptor))
        {
            await initialManager.EnsureModelAsync();
        }

        var cacheHandler = new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("A verified cache hit must not access HTTP."));
        using var cacheClient = new HttpClient(cacheHandler);
        using var cacheManager = new LocalWhisperModelManager(
            _testDirectory,
            cacheClient,
            descriptor);
        var progress = new ProgressCollector();

        var modelPath = await cacheManager.EnsureModelAsync(progress);

        Assert.Equal(cacheManager.ModelPath, modelPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(modelPath));
        Assert.Equal(0, cacheHandler.RequestCount);
        var ready = Assert.Single(
            progress.Values,
            value => value.Stage == LocalWhisperModelProgressStage.Ready);
        Assert.True(ready.FromCache);
    }

    [Fact]
    public async Task HashMismatchDeletesPartialFileAndDoesNotPublishModel()
    {
        byte[] expectedPayload = [1, 2, 3, 4, 5];
        byte[] corruptPayload = [1, 2, 3, 4, 6];
        var descriptor = CreateDescriptor(expectedPayload);
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(corruptPayload)
            }));
        using var httpClient = new HttpClient(handler);
        using var manager = new LocalWhisperModelManager(
            _testDirectory,
            httpClient,
            descriptor);

        var exception = await Assert.ThrowsAsync<LocalWhisperModelException>(
            () => manager.EnsureModelAsync());

        Assert.Equal(LocalWhisperModelFailure.Integrity, exception.Failure);
        Assert.Contains("Prüfsumme", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(manager.PartialPath));
        Assert.False(File.Exists(manager.ModelPath));
        Assert.False(File.Exists(manager.VerificationPath));
    }

    [Fact]
    public async Task CancellationDeletesPartialFile()
    {
        var payload = Enumerable.Range(0, 400_000)
            .Select(index => (byte)(index % 239))
            .ToArray();
        var descriptor = CreateDescriptor(payload);
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
        using var httpClient = new HttpClient(handler);
        using var manager = new LocalWhisperModelManager(
            _testDirectory,
            httpClient,
            descriptor);
        using var cancellation = new CancellationTokenSource();
        var progress = new ProgressCollector(value =>
        {
            if (value.Stage == LocalWhisperModelProgressStage.Downloading &&
                value.BytesProcessed > 0)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.EnsureModelAsync(progress, cancellation.Token));

        Assert.False(File.Exists(manager.PartialPath));
        Assert.False(File.Exists(manager.ModelPath));
        Assert.False(File.Exists(manager.VerificationPath));
    }

    [Fact]
    public async Task StalledResponseBodyFailsWithNetworkErrorAndDeletesPartialFile()
    {
        byte[] payload = [1, 2, 3, 4];
        var descriptor = CreateDescriptor(payload);
        var stalledStream = new FirstChunkThenStallStream(payload.AsMemory(0, 1));
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stalledStream)
            }));
        using var httpClient = new HttpClient(handler);
        using var manager = new LocalWhisperModelManager(
            _testDirectory,
            httpClient,
            descriptor,
            downloadIdleTimeout: TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<LocalWhisperModelException>(
            () => manager.EnsureModelAsync());

        Assert.Equal(LocalWhisperModelFailure.Network, exception.Failure);
        Assert.Contains("keine Daten", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(manager.PartialPath));
        Assert.False(File.Exists(manager.ModelPath));
        Assert.False(File.Exists(manager.VerificationPath));
    }

    [Fact]
    public async Task ConcurrentManagersDownloadOnceAndSecondManagerReusesCache()
    {
        var payload = Enumerable.Range(0, 200_000)
            .Select(index => (byte)(index % 233))
            .ToArray();
        var descriptor = CreateDescriptor(payload);
        var firstHandler = new DelayedResponseHandler(payload);
        var secondHandler = new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException(
                "The second manager must reuse the cache instead of downloading."));
        using var firstClient = new HttpClient(firstHandler);
        using var secondClient = new HttpClient(secondHandler);
        using var firstManager = new LocalWhisperModelManager(
            _testDirectory,
            firstClient,
            descriptor);
        using var secondManager = new LocalWhisperModelManager(
            _testDirectory,
            secondClient,
            descriptor);

        var firstEnsure = firstManager.EnsureModelAsync();
        await firstHandler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondEnsure = secondManager.EnsureModelAsync();
        await Task.Delay(250);

        Assert.Equal(0, secondHandler.RequestCount);
        firstHandler.AllowResponse.TrySetResult();
        var paths = await Task.WhenAll(firstEnsure, secondEnsure);

        Assert.All(paths, path => Assert.Equal(firstManager.ModelPath, path));
        Assert.Equal(1, firstHandler.RequestCount);
        Assert.Equal(0, secondHandler.RequestCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(firstManager.ModelPath));
        Assert.False(File.Exists(firstManager.PartialPath));
    }

    [Fact]
    public async Task WaitingForAnotherProcessLockCanBeCancelledCleanly()
    {
        byte[] payload = [7, 8, 9];
        var descriptor = CreateDescriptor(payload);
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException(
                "A manager waiting for the process lock must not access HTTP."));
        using var httpClient = new HttpClient(handler);
        using var manager = new LocalWhisperModelManager(
            _testDirectory,
            httpClient,
            descriptor);
        Directory.CreateDirectory(_testDirectory);
        await using var heldProcessLock = new FileStream(
            manager.ProcessLockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.EnsureModelAsync(cancellationToken: cancellation.Token));

        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(manager.PartialPath));
        Assert.False(File.Exists(manager.ModelPath));
    }

    [Fact]
    public async Task DisposeCancelsActiveDownloadWithoutMaskingCancellation()
    {
        byte[] payload = [11, 12, 13, 14];
        var descriptor = CreateDescriptor(payload);
        var stalledStream = new FirstChunkThenStallStream(payload.AsMemory(0, 1));
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stalledStream)
            }));
        using var httpClient = new HttpClient(handler);
        var manager = new LocalWhisperModelManager(
            _testDirectory,
            httpClient,
            descriptor,
            downloadIdleTimeout: TimeSpan.FromMinutes(10));
        var ensure = manager.EnsureModelAsync();
        await stalledStream.StallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = Task.Run(manager.Dispose);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ensure);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(dispose.IsCompletedSuccessfully);
        Assert.False(File.Exists(manager.PartialPath));
        Assert.False(File.Exists(manager.ModelPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private static LocalWhisperModelDescriptor CreateDescriptor(byte[] payload) => new(
        "test-model",
        "test-model.bin",
        new Uri("https://models.test.invalid/test-model.bin"),
        "immutable-test-revision",
        payload.LongLength,
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());

    private sealed class ProgressCollector(
        Action<LocalWhisperModelProgress>? onProgress = null)
        : IProgress<LocalWhisperModelProgress>
    {
        public List<LocalWhisperModelProgress> Values { get; } = [];

        public void Report(LocalWhisperModelProgress value)
        {
            Values.Add(value);
            onProgress?.Invoke(value);
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed class DelayedResponseHandler(byte[] payload) : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowResponse { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestStarted.TrySetResult();
            await AllowResponse.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        }
    }

    private sealed class FirstChunkThenStallStream(ReadOnlyMemory<byte> firstChunk) : Stream
    {
        private bool _firstChunkReturned;

        public TaskCompletionSource StallStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_firstChunkReturned)
            {
                _firstChunkReturned = true;
                var count = Math.Min(firstChunk.Length, buffer.Length);
                firstChunk.Span[..count].CopyTo(buffer.Span);
                return count;
            }

            StallStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

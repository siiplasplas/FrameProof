using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FrameProof.Core.Downloads;

internal static class DownloadTests
{
    public static void Run(Suite suite)
    {
        suite.Run("Downloader verifies and reuses cached bytes", root =>
        {
            var bytes = Bytes();
            var calls = 0;
            using var client = Client(message => { calls++; return Response(bytes); });
            var downloader = new HttpPackageDownloader(client, root);
            var request = Request(bytes);
            var path = downloader.DownloadAsync(request).GetAwaiter().GetResult();
            Suite.Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Downloaded bytes differ");
            Suite.Check(downloader.DownloadAsync(request).GetAwaiter().GetResult() == path && calls == 1, "Valid cache not reused");
            File.WriteAllBytes(path, new byte[bytes.Length]);
            downloader.DownloadAsync(request).GetAwaiter().GetResult();
            Suite.Check(calls == 2 && File.ReadAllBytes(path).SequenceEqual(bytes), "Corrupt cache trusted");
        });
        suite.Run("Cancelled downloader resumes with Range and If-Range", root =>
        {
            var bytes = Bytes();
            var calls = 0;
            long resumedAt = 0;
            using var client = Client(message =>
            {
                calls++;
                if (calls == 1) return Response(bytes);
                resumedAt = message.Headers.Range?.Ranges.Single().From ?? 0;
                Suite.Check(resumedAt > 0 && resumedAt < bytes.Length && message.Headers.IfRange?.EntityTag?.Tag == "\"v1\"", "Missing resume validators");
                return Response(bytes, resumedAt);
            });
            var downloader = new HttpPackageDownloader(client, root);
            var request = Request(bytes);
            CancelPartial(downloader, request);
            var partial = Directory.GetFiles(root, "*.part").Single();
            var before = new FileInfo(partial).Length;
            Suite.Check(before > 0 && before < bytes.Length && Directory.GetFiles(root, "*.bin").Length == 0, "Cancelled download marked ready");
            var result = downloader.DownloadAsync(request).GetAwaiter().GetResult();
            Suite.Check(resumedAt == before && File.ReadAllBytes(result).SequenceEqual(bytes), "Resume corrupted bytes");
        });
        suite.Run("Server ignoring Range restarts without appending", root =>
        {
            var bytes = Bytes();
            var calls = 0;
            using var client = Client(message =>
            {
                calls++;
                if (calls == 2) Suite.Check(message.Headers.Range is not null, "Resume was not attempted");
                return Response(bytes);
            });
            var downloader = new HttpPackageDownloader(client, root);
            var request = Request(bytes);
            CancelPartial(downloader, request);
            var path = downloader.DownloadAsync(request).GetAwaiter().GetResult();
            Suite.Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Ignored range produced append corruption");
        });
        suite.Run("Wrong hash removes partial and never marks ready", root =>
        {
            var bytes = Bytes();
            using var client = Client(_ => Response(bytes));
            var downloader = new HttpPackageDownloader(client, root);
            Suite.Throws<InvalidDataException>(() => downloader.DownloadAsync(Request(bytes) with { Sha256 = new string('0', 64) }).GetAwaiter().GetResult());
            Suite.Check(Directory.GetFiles(root, "*.bin").Length == 0 && Directory.GetFiles(root, "*.part").Length == 0, "Bad bytes retained as valid");
        });
        suite.Run("Downloader rejects invalid size and oversized stream", root =>
        {
            var bytes = Bytes();
            using var client = Client(_ => Response(bytes));
            var downloader = new HttpPackageDownloader(client, root);
            Suite.Throws<ArgumentException>(() => downloader.DownloadAsync(Request(bytes) with { SizeBytes = 0 }).GetAwaiter().GetResult());
            Suite.Throws<InvalidDataException>(() => downloader.DownloadAsync(Request(bytes) with { SizeBytes = bytes.Length - 1 }).GetAwaiter().GetResult());
            Suite.Check(Directory.GetFiles(root, "*.bin").Length == 0 && Directory.GetFiles(root, "*.part").Length == 0, "Oversized download retained");
            using var streaming = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableStream(new MemoryStream(bytes)))
            });
            var streamDownloader = new HttpPackageDownloader(streaming, Path.Combine(root, "streaming"));
            Suite.Throws<InvalidDataException>(() => streamDownloader.DownloadAsync(Request(bytes) with { SizeBytes = 100 }).GetAwaiter().GetResult());
            Suite.Check(Directory.GetFiles(Path.Combine(root, "streaming"), "*.part").Length == 0, "Unbounded response retained");
        });
        suite.Run("Invalid Content-Range cannot corrupt resumed package", root =>
        {
            var bytes = Bytes();
            var calls = 0;
            using var client = Client(message =>
            {
                calls++;
                if (calls == 1) return Response(bytes);
                var response = Response(bytes, message.Headers.Range!.Ranges.Single().From!.Value);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, bytes.Length - 1, bytes.Length);
                return response;
            });
            var downloader = new HttpPackageDownloader(client, root);
            CancelPartial(downloader, Request(bytes));
            Suite.Throws<InvalidDataException>(() => downloader.DownloadAsync(Request(bytes)).GetAwaiter().GetResult());
            Suite.Check(Directory.GetFiles(root, "*.bin").Length == 0, "Bad range accepted");
        });
        suite.Run("Downloader refuses HTTP URL and redirect downgrade", root =>
        {
            var bytes = Bytes();
            using var client = Client(_ =>
            {
                var response = Response(bytes);
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://example.test/file");
                return response;
            });
            var downloader = new HttpPackageDownloader(client, root);
            Suite.Throws<ArgumentException>(() => downloader.DownloadAsync(Request(bytes) with { Url = new Uri("http://example.test/file") }).GetAwaiter().GetResult());
            Suite.Throws<InvalidDataException>(() => downloader.DownloadAsync(Request(bytes)).GetAwaiter().GetResult());
        });
    }

    private static void CancelPartial(HttpPackageDownloader downloader, DownloadRequest request)
    {
        using var cancellation = new CancellationTokenSource();
        Suite.Throws<OperationCanceledException>(() => downloader.DownloadAsync(request, cancellation.Token,
            new ImmediateProgress(value => { if (value.ReceivedBytes > 0 && value.ReceivedBytes < value.TotalBytes) cancellation.Cancel(); }))
            .GetAwaiter().GetResult());
    }
    private static byte[] Bytes() => Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
    private static DownloadRequest Request(byte[] bytes) => new("game", new Uri("https://example.test/file"), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));
    private static HttpResponseMessage Response(byte[] bytes, long offset = 0)
    {
        var response = new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(bytes, (int)offset, bytes.Length - (int)offset) };
        response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
        if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private sealed class ImmediateProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

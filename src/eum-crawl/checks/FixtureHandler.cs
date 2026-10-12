using System.Net;
using System.Text;
using Aha.EumCrawl.Checks;
sealed class FixtureHandler(string directory, bool failTab = false, string? stalledPath = null) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        Requests.Add(uri);
        if (uri.Host != "www.eum.go.kr" || uri.Scheme != "https") throw new Exception("Unexpected endpoint");
        if (stalledPath is not null && uri.AbsolutePath.Contains(stalledPath))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) });
        if (failTab && uri.AbsolutePath.Contains("GYAjax")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var file = uri.AbsolutePath.EndsWith("luLandDet.jsp") ? "parcel-recorded.html" :
            uri.AbsolutePath.Contains("GYAjax") ? "ratios-recorded.html" : uri.AbsolutePath.Contains("CAjax") ? "height-recorded.html" : "setback-recorded.html";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(File.ReadAllText(Path.Combine(directory, FixtureData.FileName(file))), Encoding.GetEncoding(949), "text/html") };
        return Task.FromResult(response);
    }
}

sealed class StalledBody : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

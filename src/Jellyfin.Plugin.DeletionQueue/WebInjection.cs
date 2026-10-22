using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.DeletionQueue;

// Additive, request-time injection. No Jellyfin file/config/database writes and no dependency on Enhanced.
public sealed class WebInjection : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(InvokeAsync);
        next(app);
    };

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value;
        if (!HttpMethods.IsGet(context.Request.Method) ||
            !(string.Equals(path, "/web", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(path, "/web/", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(path, "/web/index.html", StringComparison.OrdinalIgnoreCase)))
        { await next(context); return; }

        var saved = new[] { "Accept-Encoding", "Range", "If-Range", "If-None-Match", "If-Modified-Since" }
            .ToDictionary(k => k, k => context.Request.Headers[k]);
        foreach (var key in saved.Keys) context.Request.Headers.Remove(key);
        var original = context.Response.Body;
        using var capture = new BoundedCapture(original);
        context.Response.Body = capture;
        try
        {
            await next(context);
            if (capture.Streaming) return;
            var bytes = capture.Bytes;
            if (!context.Response.HasStarted && context.Response.StatusCode == 200 &&
                context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true &&
                string.IsNullOrEmpty(context.Response.Headers.ContentEncoding))
            {
                try
                {
                    var html = new UTF8Encoding(false, true).GetString(bytes);
                    var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (end >= 0 && !html.Contains("id=\"dq-web-client\"", StringComparison.Ordinal))
                    {
                        var root = HtmlEncoder.Default.Encode((context.Request.PathBase.Value ?? "") + "/DeletionQueue/assets/");
                        var version = typeof(WebInjection).Assembly.GetName().Version!.ToString();
                        var tags = $"<link rel=\"stylesheet\" href=\"{root}integrated.css?v={version}\"><script id=\"dq-web-client\" defer src=\"{root}integrated.js?v={version}\"></script>";
                        bytes = Encoding.UTF8.GetBytes(html.Insert(end, tags));
                        context.Response.ContentLength = bytes.Length;
                        context.Response.Headers.Remove("ETag");
                        context.Response.Headers.Remove("Last-Modified");
                        context.Response.Headers.Remove("Accept-Ranges");
                        context.Response.Headers.CacheControl = "no-store";
                    }
                }
                catch (Exception) { /* An incompatible encoding/response must remain usable without the plugin. */ }
            }
            await original.WriteAsync(bytes, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = original;
            foreach (var (key, value) in saved)
                if (value.Count == 0) context.Request.Headers.Remove(key); else context.Request.Headers[key] = value;
        }
    }

    // Overflow switches permanently to exact passthrough. Flush is deferred while buffered because static-file
    // SendFileAsync starts by flushing the response-body feature before writing the index document.
    private sealed class BoundedCapture(Stream output) : Stream
    {
        private const int Limit = 2 * 1024 * 1024;
        private readonly MemoryStream buffer = new();
        public bool Streaming { get; private set; }
        public byte[] Bytes => buffer.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] data, int offset, int count) => Write(data.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> data)
        {
            if (!Streaming && buffer.Length + data.Length <= Limit) { buffer.Write(data); return; }
            if (!Streaming) { Streaming = true; buffer.Position = 0; buffer.CopyTo(output); buffer.SetLength(0); }
            output.Write(data);
        }
        public override Task WriteAsync(byte[] data, int offset, int count, CancellationToken ct) => WriteAsync(data.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (!Streaming && buffer.Length + data.Length <= Limit) { await buffer.WriteAsync(data, ct); return; }
            if (!Streaming) { Streaming = true; buffer.Position = 0; await buffer.CopyToAsync(output, ct); buffer.SetLength(0); }
            await output.WriteAsync(data, ct);
        }
        public override void Flush()
        {
            if (Streaming) output.Flush();
        }
        public override async Task FlushAsync(CancellationToken ct)
        {
            if (Streaming) await output.FlushAsync(ct);
        }
        public override int Read(byte[] data, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) buffer.Dispose(); base.Dispose(disposing); }
    }
}

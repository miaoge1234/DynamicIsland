using System.Collections.ObjectModel;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// 消息汇总：QQ 监听、本地推送都汇到这里，去重后交给界面显示。
/// </summary>
public sealed class MessageHub
{
    private const int MaxMessages = 40;

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, DateTimeOffset> _dedupe = new();

    public MessageHub(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>最新的消息在最前面。</summary>
    public ObservableCollection<IslandMessage> Messages { get; } = new();

    /// <summary>收到一条新消息（已去重）。</summary>
    public event EventHandler<IslandMessage>? MessageAdded;

    /// <summary>未读（没被看过）的消息数。</summary>
    public int UnreadCount { get; private set; }

    public void MarkAllRead()
    {
        UnreadCount = 0;
    }

    public void Push(IslandMessage message)
    {
        // 同一个来源 + 同一个人 + 同一句话，5 秒内只算一次，
        // 因为窗口闪烁和标题变化常常同时触发。
        string key = $"{message.Kind}|{message.Source}|{message.Title}|{message.Text}";
        var now = DateTimeOffset.Now;
        if (_dedupe.TryGetValue(key, out var last) && (now - last) < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _dedupe[key] = now;
        if (_dedupe.Count > 200)
        {
            _dedupe.Clear();
        }

        _dispatcher.BeginInvoke(() =>
        {
            Messages.Insert(0, message);
            while (Messages.Count > MaxMessages)
            {
                Messages.RemoveAt(Messages.Count - 1);
            }

            UnreadCount++;
            MessageAdded?.Invoke(this, message);
        });
    }
}

/// <summary>
/// 本地推送接口：一个极小的 HTTP 服务，监听 127.0.0.1。
/// 任何脚本都能往岛上推消息，也可以用来补 QQ 拿不到的正文。
///
///   curl -X POST http://127.0.0.1:7788/message -d "{\"title\":\"张三\",\"text\":\"在吗\",\"source\":\"QQ\"}"
///   curl -X POST http://127.0.0.1:7788/call    -d "{\"title\":\"李四\"}"
///   curl http://127.0.0.1:7788/ping
///
/// 用 TcpListener 自己解析最简 HTTP，避免 HttpListener 需要管理员预留 URL 的麻烦。
/// </summary>
public sealed class PushServer : IDisposable
{
    private readonly MessageHub _hub;
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public PushServer(MessageHub hub, int port)
    {
        _hub = hub;
        _port = port;
    }

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    public void Start()
    {
        if (_port <= 0 || IsRunning)
        {
            return;
        }

        try
        {
            _listener = new TcpListener(System.Net.IPAddress.Loopback, _port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _ = AcceptLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client), token);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 4000;
                client.SendTimeout = 4000;

                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);

                string response = "{\"ok\":true}";
                int status = 200;

                if (request.Path.StartsWith("/ping", StringComparison.OrdinalIgnoreCase))
                {
                    response = "{\"ok\":true,\"service\":\"DynamicIsland\"}";
                }
                else if (request.Path.StartsWith("/message", StringComparison.OrdinalIgnoreCase) ||
                         request.Path.StartsWith("/call", StringComparison.OrdinalIgnoreCase))
                {
                    bool isCall = request.Path.StartsWith("/call", StringComparison.OrdinalIgnoreCase);
                    string body = DecodeBody(request.Body, request.ContentType);
                    var parsed = ParsePayload(body);

                    if (parsed is null)
                    {
                        status = 400;
                        response = "{\"ok\":false,\"error\":\"无法解析请求体\"}";
                    }
                    else
                    {
                        _hub.Push(new IslandMessage
                        {
                            Kind = isCall ? MessageKind.IncomingCall : MessageKind.Message,
                            Source = parsed.Value.Source,
                            Title = parsed.Value.Title,
                            Text = parsed.Value.Text,
                        });
                        response = "{\"ok\":true}";
                    }
                }
                else
                {
                    status = 404;
                    response = "{\"ok\":false,\"error\":\"未知路径\"}";
                }

                await WriteResponseAsync(stream, status, response);
            }
            catch
            {
                // 单个请求失败不影响服务
            }
        }
    }

    /// <summary>
    /// 把请求体的字节解成字符串。
    /// 顺序：响应头里声明的 charset → 严格 UTF-8 → 本地代码页（中文 Windows 多为 936）。
    /// 很多老客户端（比如 Windows PowerShell 5.1）默认用本地代码页发中文，
    /// 直接按 UTF-8 解会变成乱码。
    /// </summary>
    private static string DecodeBody(byte[] body, string contentType)
    {
        if (body.Length == 0)
        {
            return string.Empty;
        }

        string? declared = ParseCharset(contentType);
        if (declared is not null)
        {
            try
            {
                return Encoding.GetEncoding(declared).GetString(body);
            }
            catch
            {
                // charset 名字不认识，继续往下试
            }
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法的 UTF-8，说明对方用的是本地代码页
        }
        catch
        {
            // 忽略
        }

        foreach (int codePage in new[] { 936, 950, 932, 1252 })
        {
            try
            {
                return Encoding.GetEncoding(codePage).GetString(body);
            }
            catch
            {
                // 这个代码页不可用，试下一个
            }
        }

        return Encoding.UTF8.GetString(body);
    }

    private static string? ParseCharset(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        int index = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        string value = contentType[(index + 8)..].Trim().Trim('"', '\'');
        int semicolon = value.IndexOf(';');
        if (semicolon >= 0)
        {
            value = value[..semicolon];
        }

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    private sealed record HttpRequest(string Method, string Path, string ContentType, byte[] Body);

    private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var raw = new MemoryStream(8192);

        string method = "GET";
        string path = "/";
        string contentType = string.Empty;
        int contentLength = 0;
        int headerEnd = -1;

        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                break;
            }

            raw.Write(buffer, 0, read);

            byte[] data = raw.GetBuffer();
            int length = (int)raw.Length;

            if (headerEnd < 0)
            {
                headerEnd = IndexOfDoubleCrlf(data, length);
                if (headerEnd >= 0)
                {
                    string head = Encoding.ASCII.GetString(data, 0, headerEnd);
                    var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

                    if (lines.Length > 0)
                    {
                        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            method = parts[0];
                            path = parts[1];
                        }
                    }

                    foreach (string line in lines)
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            _ = int.TryParse(line[15..].Trim(), out contentLength);
                        }
                        else if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                        {
                            contentType = line[13..].Trim();
                        }
                    }

                    if (contentLength < 0 || contentLength > 4 * 1024 * 1024)
                    {
                        contentLength = 0;
                    }
                }
            }

            if (headerEnd >= 0 && length - (headerEnd + 4) >= contentLength)
            {
                break;
            }

            // 防呆：请求头异常大就直接放弃
            if (length > 8 * 1024 * 1024)
            {
                break;
            }
        }

        byte[] all = raw.ToArray();
        byte[] body = Array.Empty<byte>();

        if (headerEnd >= 0)
        {
            int bodyStart = headerEnd + 4;
            int available = Math.Max(0, all.Length - bodyStart);
            int take = Math.Min(contentLength > 0 ? contentLength : available, available);
            if (take > 0)
            {
                body = new byte[take];
                Array.Copy(all, bodyStart, body, 0, take);
            }
        }

        return new HttpRequest(method, path, contentType, body);
    }

    private static int IndexOfDoubleCrlf(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        string reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : "Not Found";
        string header =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static (string Source, string Title, string Text)? ParsePayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        body = body.Trim();

        // 先按 JSON 试
        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string source = root.TryGetProperty("source", out var s) ? s.GetString() ?? "推送" : "推送";
                string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "推送" : "推送";
                string text = root.TryGetProperty("text", out var x) ? x.GetString() ?? string.Empty : string.Empty;
                if (text.Length == 0 && root.TryGetProperty("body", out var b))
                {
                    text = b.GetString() ?? string.Empty;
                }
                return (source, title, text);
            }
            catch
            {
                // 落到纯文本
            }
        }

        // 纯文本：当成正文
        return ("推送", "本地推送", body);
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // 忽略
        }
        finally
        {
            IsRunning = false;
        }
    }
}

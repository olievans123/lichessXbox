using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace LichessXbox.Services
{
    /// <summary>
    /// A tiny HTTP listener that runs inside the app on the console's LAN address.
    /// Used for device-pairing sign-in: the phone completes the Lichess login and is
    /// redirected to http://&lt;console-ip&gt;:&lt;port&gt;/callback?code=…, which lands here —
    /// so the token comes back to the console automatically, no typing.
    /// </summary>
    public sealed class LanCallbackServer : IDisposable
    {
        StreamSocketListener _listener;
        TaskCompletionSource<Dictionary<string, string>> _tcs;

        /// <summary>The redirect URI to register with Lichess, e.g. http://192.168.1.64:8787/callback</summary>
        public string RedirectUri { get; private set; }

        public static string GetLanIpv4()
        {
            // Prefer a private-range address tied to a real network adapter.
            foreach (var h in NetworkInformation.GetHostNames())
            {
                if (h.Type == HostNameType.Ipv4 && h.IPInformation != null && h.IPInformation.NetworkAdapter != null)
                {
                    var ip = h.CanonicalName;
                    if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172."))
                        return ip;
                }
            }
            foreach (var h in NetworkInformation.GetHostNames())
                if (h.Type == HostNameType.Ipv4 && h.IPInformation != null) return h.CanonicalName;
            return null;
        }

        /// <summary>Bind a listener; tries a few ports. Returns false if no LAN IP / all binds fail.</summary>
        public async Task<bool> StartAsync()
        {
            string ip = GetLanIpv4();
            if (string.IsNullOrEmpty(ip)) return false;

            _tcs = new TaskCompletionSource<Dictionary<string, string>>();
            foreach (int port in new[] { 8787, 8123, 51337 })
            {
                try
                {
                    _listener = new StreamSocketListener();
                    _listener.ConnectionReceived += OnConnection;
                    await _listener.BindServiceNameAsync(port.ToString());
                    RedirectUri = $"http://{ip}:{port}/callback";
                    return true;
                }
                catch
                {
                    try { _listener?.Dispose(); } catch { }
                    _listener = null;
                }
            }
            return false;
        }

        public Task<Dictionary<string, string>> WaitForCallbackAsync() => _tcs.Task;

        async void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            try
            {
                using (var socket = args.Socket)
                {
                    var reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
                    await reader.LoadAsync(4096);
                    string request = reader.UnconsumedBufferLength > 0 ? reader.ReadString(reader.UnconsumedBufferLength) : "";
                    string firstLine = request.Split('\n').Length > 0 ? request.Split('\n')[0] : request;
                    var query = ParseQuery(firstLine);

                    bool haveCode = query.ContainsKey("code");
                    string body = haveCode
                        ? "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'></head>"
                          + "<body style=\"font-family:-apple-system,Segoe UI,sans-serif;background:#161512;color:#f4f1ea;text-align:center;padding-top:18vh\">"
                          + "<h1 style='color:#8FCB3F'>Signed in &#9989;</h1><p>You can return to your Xbox now.</p></body></html>"
                        : "<!doctype html><html><body>Waiting for authorization…</body></html>";

                    var bytes = Encoding.UTF8.GetByteCount(body);
                    string response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
                                      + "Content-Length: " + bytes + "\r\nConnection: close\r\n\r\n" + body;

                    var writer = new DataWriter(socket.OutputStream);
                    writer.WriteString(response);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();

                    if (haveCode) _tcs?.TrySetResult(query);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LAN callback error: " + ex.Message);
            }
        }

        static Dictionary<string, string> ParseQuery(string requestLine)
        {
            var d = new Dictionary<string, string>();
            int q = requestLine.IndexOf('?');
            if (q < 0) return d;
            int space = requestLine.IndexOf(' ', q);
            string query = space > q ? requestLine.Substring(q + 1, space - q - 1) : requestLine.Substring(q + 1);
            foreach (var pair in query.Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2) d[kv[0]] = Uri.UnescapeDataString(kv[1]);
            }
            return d;
        }

        public void Dispose()
        {
            try { if (_listener != null) _listener.ConnectionReceived -= OnConnection; } catch { }
            try { _listener?.Dispose(); } catch { }
            _listener = null;
            _tcs?.TrySetCanceled();
        }
    }
}

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoHeim
{
    /// <summary>
    /// Localhost HTTP front end. Runs on a background thread and never touches
    /// game state: every request becomes a Job that the Runner executes on the
    /// Unity main thread. The request thread blocks until the job finishes or
    /// the wait budget runs out.
    ///
    ///   GET  /ping                      liveness, no main-thread hop
    ///   GET  /&lt;cmd&gt;?k=v&amp;k2=v2          run a command with query-string args
    ///   POST /&lt;cmd&gt;   {json args}       run a command with a JSON body
    ///   POST /        {"cmd":..., ...}  same, command named in the body
    ///
    /// Every response is {"ok":bool, "cmd":..., "result"|"error":..., "ms":...}.
    /// </summary>
    internal sealed class HttpApi
    {
        private readonly int _port;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        internal HttpApi(int port) => _port = port;

        internal void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "AutoHeim-HTTP" };
            _thread.Start();
        }

        internal void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"HTTP accept error: {e.Message}");
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            var started = DateTime.UtcNow;
            string cmd = null;
            JObject response;
            int status = 200;
            try
            {
                var req = ctx.Request;
                cmd = req.Url.AbsolutePath.Trim('/');
                JObject args = new JObject();

                foreach (string key in req.QueryString.AllKeys)
                {
                    if (key == null) continue;
                    args[key] = ParseScalar(req.QueryString[key]);
                }

                if (req.HasEntityBody)
                {
                    string body;
                    using (var r = new StreamReader(req.InputStream, Encoding.UTF8))
                        body = r.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var parsed = JToken.Parse(body);
                        if (parsed is JObject o)
                            foreach (var prop in o.Properties()) args[prop.Name] = prop.Value;
                        else
                            throw new ArgumentException("JSON body must be an object");
                    }
                }

                if (string.IsNullOrEmpty(cmd))
                    cmd = args.Value<string>("cmd") ?? "help";
                args.Remove("cmd");

                if (cmd == "ping")
                {
                    response = Envelope(true, cmd, JToken.FromObject(new
                    {
                        plugin = Plugin.PluginName,
                        version = Plugin.PluginVersion,
                        pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                        isolated = Plugin.Isolated,
                    }), null);
                }
                else
                {
                    var job = Runner.Submit(cmd, args);
                    float waitSecs = Commands.WaitBudget(cmd, args);
                    if (!job.Done.Wait(TimeSpan.FromSeconds(waitSecs)))
                    {
                        job.Cancelled = true;
                        status = 504;
                        response = Envelope(false, cmd, null, $"timed out after {waitSecs:F0}s waiting for main thread (is the game frozen or loading?)");
                    }
                    else
                    {
                        if (job.Error != null) status = job.Error.StartsWith("unknown command") ? 404 : 400;
                        response = Envelope(job.Error == null, cmd,
                            job.Result == null ? null : JToken.FromObject(job.Result, Json.Serializer), job.Error);
                    }
                }
            }
            catch (Exception e)
            {
                status = 400;
                response = Envelope(false, cmd, null, $"{e.GetType().Name}: {e.Message}");
            }

            response["ms"] = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"HTTP write failed: {e.Message}");
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        private static JObject Envelope(bool ok, string cmd, JToken result, string error)
        {
            var o = new JObject { ["ok"] = ok, ["cmd"] = cmd };
            if (ok) o["result"] = result;
            else o["error"] = error;
            return o;
        }

        /// Query-string values arrive as text; turn obvious numbers/bools into JSON scalars.
        private static JToken ParseScalar(string s)
        {
            if (s == null) return JValue.CreateNull();
            if (s == "true") return true;
            if (s == "false") return false;
            if (long.TryParse(s, out long l)) return l;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
            return s;
        }
    }
}

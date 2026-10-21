using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>One API request travelling from the HTTP thread to the main thread and back.</summary>
    internal sealed class Job
    {
        internal string Name;
        internal JObject Args;
        internal object Result;
        internal string Error;
        internal volatile bool Cancelled;
        internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    /// <summary>Thrown by commands to report a clean, expected failure.</summary>
    internal sealed class CommandError : Exception
    {
        internal CommandError(string message) : base(message) { }
    }

    /// <summary>
    /// Main-thread executor. Each job runs as a coroutine so long actions
    /// (walking somewhere, loading a world, recording audio) can span frames
    /// while other requests (status, screenshots) keep being served.
    /// </summary>
    internal sealed class Runner : MonoBehaviour
    {
        private static readonly ConcurrentQueue<Job> Pending = new ConcurrentQueue<Job>();
        internal static Runner Instance;
        private float _nextBackgroundAssert;

        internal static Job Submit(string name, JObject args)
        {
            var job = new Job { Name = name, Args = args ?? new JObject() };
            Pending.Enqueue(job);
            return job;
        }

        private void Awake() => Instance = this;

        private void Start()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = Plugin.EnvInt("AUTOHEIM_FPS", 60);
        }

        private void Update()
        {
            // Valheim (or Unity on focus changes) can drop this; without it the
            // game stops ticking whenever the window is not focused.
            if (Time.unscaledTime >= _nextBackgroundAssert)
            {
                _nextBackgroundAssert = Time.unscaledTime + 1f;
                if (!Application.runInBackground) Application.runInBackground = true;
            }

            while (Pending.TryDequeue(out var job))
                StartCoroutine(Execute(job));
        }

        private IEnumerator Execute(Job job)
        {
            IEnumerator body = null;
            try
            {
                var def = Commands.Get(job.Name);
                if (def == null)
                    throw new CommandError($"unknown command '{job.Name}' (try 'help')");
                body = def.Fn(new Ctx(job));
            }
            catch (Exception e)
            {
                Finish(job, e);
                yield break;
            }

            while (true)
            {
                object current;
                try
                {
                    if (job.Cancelled) throw new CommandError("cancelled (HTTP wait expired)");
                    if (!body.MoveNext()) break;
                    current = body.Current;
                }
                catch (Exception e)
                {
                    Finish(job, e);
                    yield break;
                }
                yield return current;
            }
            Finish(job, null);
        }

        private static void Finish(Job job, Exception e)
        {
            if (e != null)
            {
                if (e is CommandError || e is ArgumentException)
                    job.Error = e.Message;
                else
                {
                    job.Error = $"{e.GetType().Name}: {e.Message}";
                    Plugin.Log.LogError($"Command '{job.Name}' threw: {e}");
                }
            }
            job.Done.Set();
        }
    }

    /// <summary>Argument access and result plumbing handed to every command.</summary>
    internal sealed class Ctx
    {
        private readonly Job _job;
        internal Ctx(Job job) => _job = job;

        internal JObject Args => _job.Args;
        internal object Result { set => _job.Result = value; }
        internal bool Cancelled => _job.Cancelled;

        internal bool Has(string name) => _job.Args[name] != null && _job.Args[name].Type != JTokenType.Null;

        internal string Str(string name, string def = null) => Has(name) ? _job.Args[name].ToString() : def;
        internal float Float(string name, float def) => Has(name) ? _job.Args[name].Value<float>() : def;
        internal int Int(string name, int def) => Has(name) ? _job.Args[name].Value<int>() : def;
        internal bool Bool(string name, bool def)
        {
            if (!Has(name)) return def;
            var t = _job.Args[name];
            if (t.Type == JTokenType.Boolean) return t.Value<bool>();
            string s = t.ToString().ToLowerInvariant();
            return s == "1" || s == "true" || s == "yes" || s == "on";
        }

        internal string Need(string name)
        {
            string v = Str(name);
            if (string.IsNullOrEmpty(v)) throw new CommandError($"missing required argument '{name}'");
            return v;
        }

        internal float NeedFloat(string name)
        {
            if (!Has(name)) throw new CommandError($"missing required argument '{name}'");
            return Float(name, 0f);
        }

        internal static CommandError Fail(string message) => new CommandError(message);
    }
}

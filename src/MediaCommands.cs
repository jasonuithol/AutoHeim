using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>Screenshots and audio capture, written to files the agent can open.</summary>
    internal static partial class Commands
    {
        private static void RegisterMedia()
        {
            Add("screenshot", "screenshot [path=<file.png|.jpg>] [width=<px>] [mode=screen|camera] [hud=true] [quality=85]",
                "Capture the game view. width downscales (keeps aspect). mode=camera renders the 3D view without UI.", Screenshot, 20f);
            Add("record_audio", "record_audio [seconds=5] [path=<file.wav>]",
                "Record the game's final audio mix to a 16-bit WAV. Also returns peak/RMS levels.", RecordAudio, 15f);
        }

        private static string OutPath(Ctx c, string prefix, string ext)
        {
            string path = c.Str("path");
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Plugin.OutputDir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return path;
        }

        private static IEnumerator Screenshot(Ctx c)
        {
            string path = OutPath(c, "screenshot", ".png");
            bool jpg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            string mode = c.Str("mode", "screen");
            bool hud = c.Bool("hud", true);
            int width = c.Int("width", 0);

            GameObject hudRoot = (!hud && Hud.instance != null) ? Hud.instance.m_rootObject : null;
            bool hudWasActive = hudRoot != null && hudRoot.activeSelf;
            if (hudWasActive) { hudRoot.SetActive(false); yield return null; }

            Texture2D full = null, final = null;
            try
            {
                yield return new WaitForEndOfFrame();
                if (mode == "camera")
                {
                    var cam = Utils.GetMainCamera() ?? Camera.main ?? throw Ctx.Fail("no camera to render");
                    int w = Screen.width, h = Screen.height;
                    var rt = RenderTexture.GetTemporary(w, h, 24);
                    var prevTarget = cam.targetTexture;
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = prevTarget;
                    full = ReadRT(rt, w, h);
                    RenderTexture.ReleaseTemporary(rt);
                }
                else
                {
                    full = ScreenCapture.CaptureScreenshotAsTexture();
                }

                final = full;
                if (width > 0 && width < full.width)
                {
                    int h = Mathf.Max(1, Mathf.RoundToInt(full.height * (width / (float)full.width)));
                    var rt = RenderTexture.GetTemporary(width, h, 0);
                    Graphics.Blit(full, rt);
                    final = ReadRT(rt, width, h);
                    RenderTexture.ReleaseTemporary(rt);
                }

                byte[] bytes = jpg ? final.EncodeToJPG(Mathf.Clamp(c.Int("quality", 85), 1, 100)) : final.EncodeToPNG();
                File.WriteAllBytes(path, bytes);
                c.Result = new { path, width = final.width, height = final.height, bytes = bytes.Length, mode, focused = Application.isFocused };
            }
            finally
            {
                if (hudWasActive) hudRoot.SetActive(true);
                if (final != null && final != full) UnityEngine.Object.Destroy(final);
                if (full != null) UnityEngine.Object.Destroy(full);
            }
        }

        private static Texture2D ReadRT(RenderTexture rt, int w, int h)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        private static IEnumerator RecordAudio(Ctx c)
        {
            float seconds = Mathf.Clamp(c.Float("seconds", 5f), 0.1f, 300f);
            string path = OutPath(c, "audio", ".wav");
            var listener = UnityEngine.Object.FindObjectOfType<AudioListener>() ?? throw Ctx.Fail("no AudioListener in the scene");
            var rec = listener.gameObject.GetComponent<AudioRecorder>() ?? listener.gameObject.AddComponent<AudioRecorder>();
            rec.Begin();
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                if (rec == null) throw Ctx.Fail("audio listener was destroyed mid-recording (scene change?)");
                yield return null;
            }
            var (samples, channels) = rec.End();
            int rate = AudioSettings.outputSampleRate;
            AudioRecorder.WriteWav(path, samples, channels, rate);
            AudioRecorder.Levels(samples, out float peak, out float rms);
            c.Result = new
            {
                path,
                seconds = Json.R(samples.Length / (float)Mathf.Max(1, channels * rate)),
                sample_rate = rate,
                channels,
                peak = Math.Round(peak, 4),
                rms = Math.Round(rms, 4),
                silent = peak < 0.001f,
                listener_paused = AudioListener.pause,
                listener_volume = Json.R(AudioListener.volume),
            };
        }
    }

    /// <summary>Taps the final mix at the AudioListener (audio thread) while recording.</summary>
    internal sealed class AudioRecorder : MonoBehaviour
    {
        private readonly List<float> _buffer = new List<float>();
        private readonly object _lock = new object();
        private volatile bool _recording;
        private int _channels = 2;

        internal void Begin()
        {
            lock (_lock) _buffer.Clear();
            _recording = true;
        }

        internal (float[] samples, int channels) End()
        {
            _recording = false;
            lock (_lock) return (_buffer.ToArray(), _channels);
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_recording) return;
            _channels = channels;
            lock (_lock) _buffer.AddRange(data);
        }

        internal static void Levels(float[] s, out float peak, out float rms)
        {
            peak = 0f;
            double sum = 0;
            foreach (float v in s)
            {
                float a = Math.Abs(v);
                if (a > peak) peak = a;
                sum += v * v;
            }
            rms = s.Length > 0 ? (float)Math.Sqrt(sum / s.Length) : 0f;
        }

        internal static void WriteWav(string path, float[] samples, int channels, int rate)
        {
            using (var fs = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                int dataBytes = samples.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                foreach (float v in samples)
                    w.Write((short)(Mathf.Clamp(v, -1f, 1f) * short.MaxValue));
            }
        }
    }
}

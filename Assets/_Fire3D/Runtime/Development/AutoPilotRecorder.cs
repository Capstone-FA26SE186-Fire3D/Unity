using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace Fire3D.Unity.Development
{
    /// <summary>
    /// Frame time, memory and screenshots for an <see cref="AutoPilot"/> run. Numbers describe the machine the run
    /// happened on (Editor or a Windows player), never an Android device.
    /// </summary>
    internal sealed class AutoPilotRecorder : IDisposable
    {
        private const float WarmupSeconds = 2f;

        private readonly string _outDir;
        private readonly List<float> _frameMs = new(20000);
        private readonly List<float> _frameAt = new(20000);
        private readonly List<string> _frameStep = new(20000);
        private readonly List<bool> _frameCapture = new(20000);
        private int _excludeFrames;
        private readonly List<float> _cpuMs = new();
        private readonly List<float> _gpuMs = new();
        private readonly List<string> _shots = new();
        private readonly FrameTiming[] _timing = new FrameTiming[1];
        private ProfilerRecorder _drawCalls;
        private ProfilerRecorder _triangles;
        private ProfilerRecorder _setPass;
        private long _peakAllocated;
        private long _peakMono;
        private long _peakGfx;
        private int _gcStart;
        private float _start;
        private int _maxDrawCalls;
        private long _maxTriangles;
        private int _maxSetPass;

        public string OutputDirectory => _outDir;
        public IReadOnlyList<string> Screenshots => _shots;

        public AutoPilotRecorder(string outDir)
        {
            _outDir = outDir;
            Directory.CreateDirectory(outDir);
            _gcStart = GC.CollectionCount(0);
            _start = Time.realtimeSinceStartup;
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        }

        public void Dispose()
        {
            _drawCalls.Dispose();
            _triangles.Dispose();
            _setPass.Dispose();
        }

        public void Sample(string step)
        {
            var now = Time.realtimeSinceStartup - _start;
            _frameMs.Add(Time.unscaledDeltaTime * 1000f);
            _frameAt.Add(now);
            _frameStep.Add(step);
            // A screenshot is encoded at the end of a frame and shows up in the next frames' delta: not gameplay cost.
            _frameCapture.Add(_excludeFrames > 0);
            if (_excludeFrames > 0)
                _excludeFrames--;

            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _timing) == 1)
            {
                if (_timing[0].cpuFrameTime > 0)
                    _cpuMs.Add((float)_timing[0].cpuFrameTime);
                if (_timing[0].gpuFrameTime > 0)
                    _gpuMs.Add((float)_timing[0].gpuFrameTime);
            }

            if (_drawCalls.Valid)
                _maxDrawCalls = Math.Max(_maxDrawCalls, (int)_drawCalls.LastValue);
            if (_triangles.Valid)
                _maxTriangles = Math.Max(_maxTriangles, _triangles.LastValue);
            if (_setPass.Valid)
                _maxSetPass = Math.Max(_maxSetPass, (int)_setPass.LastValue);

            if (_frameMs.Count % 30 == 0)
            {
                _peakAllocated = Math.Max(_peakAllocated, Profiler.GetTotalAllocatedMemoryLong());
                _peakMono = Math.Max(_peakMono, Profiler.GetMonoUsedSizeLong());
                _peakGfx = Math.Max(_peakGfx, Profiler.GetAllocatedMemoryForGraphicsDriver());
            }
        }

        /// <summary>Captures the frame (including the HUD) at the end of this frame.</summary>
        public IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            var file = $"{_shots.Count + 1:00}_{name}.png";
            Texture2D tex = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex == null || tex.width < 16 || IsBlank(tex))
                {
                    if (tex != null)
                        UnityEngine.Object.Destroy(tex);
                    tex = RenderCamera();
                }

                if (tex == null)
                    yield break;
                File.WriteAllBytes(Path.Combine(_outDir, file), tex.EncodeToPNG());
                _shots.Add(file);
                _excludeFrames = 2;
            }
            finally
            {
                if (tex != null)
                    UnityEngine.Object.Destroy(tex);
            }
        }

        private static bool IsBlank(Texture2D tex)
        {
            var px = tex.GetPixels32(0);
            var step = Math.Max(1, px.Length / 512);
            for (var i = 0; i < px.Length; i += step)
                if (px[i].r > 8 || px[i].g > 8 || px[i].b > 8)
                    return false;
            return true;
        }

        /// <summary>Fallback for batch mode, where the back buffer is not presented: render the main camera (no overlay UI).</summary>
        private static Texture2D RenderCamera()
        {
            var cam = Camera.main;
            if (cam == null)
                return null;
            var rt = RenderTexture.GetTemporary(1600, 900, 24, RenderTextureFormat.ARGB32);
            var previous = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = previous;
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        public JObject Frames()
        {
            var steady = new List<float>();
            for (var i = 0; i < _frameMs.Count; i++)
                if (Counted(i))
                    steady.Add(_frameMs[i]);
            if (steady.Count == 0)
                steady.AddRange(_frameMs);

            var hitches = Enumerable.Range(0, _frameMs.Count)
                .Where(Counted)
                .OrderByDescending(i => _frameMs[i]).Take(10)
                .Select(i => new JObject { ["ms"] = Math.Round(_frameMs[i], 2), ["atSeconds"] = Math.Round(_frameAt[i], 2), ["step"] = _frameStep[i] });

            var perStep = new JObject();
            foreach (var g in Enumerable.Range(0, _frameMs.Count).Where(Counted).GroupBy(i => _frameStep[i]))
                perStep[g.Key] = Stats(g.Select(i => _frameMs[i]).ToList());

            var o = Stats(steady);
            o["warmupSeconds"] = WarmupSeconds;
            o["totalFrames"] = _frameMs.Count;
            o["excludedScreenshotFrames"] = _frameCapture.Count(c => c);
            o["over33ms"] = steady.Count(ms => ms > 33.4f);
            o["over50ms"] = steady.Count(ms => ms > 50f);
            o["worst"] = new JArray(hitches);
            o["perStep"] = perStep;
            if (_cpuMs.Count > 0)
                o["cpu"] = Stats(_cpuMs);
            if (_gpuMs.Count > 0)
                o["gpu"] = Stats(_gpuMs);
            return o;
        }

        private bool Counted(int i) => _frameAt[i] >= WarmupSeconds && !_frameCapture[i];

        public JObject Rendering() => new()
        {
            ["maxDrawCalls"] = _maxDrawCalls,
            ["maxSetPassCalls"] = _maxSetPass,
            ["maxTriangles"] = _maxTriangles,
        };

        public JObject Memory() => new()
        {
            ["peakAllocatedMB"] = Math.Round(_peakAllocated / 1048576.0, 1),
            ["peakManagedMB"] = Math.Round(_peakMono / 1048576.0, 1),
            ["peakGraphicsDriverMB"] = Math.Round(_peakGfx / 1048576.0, 1),
            ["gen0Collections"] = GC.CollectionCount(0) - _gcStart,
        };

        private static JObject Stats(List<float> values)
        {
            if (values.Count == 0)
                return new JObject { ["count"] = 0 };
            var sorted = values.OrderBy(v => v).ToList();
            float P(float q) => sorted[Mathf.Clamp(Mathf.CeilToInt(q * sorted.Count) - 1, 0, sorted.Count - 1)];
            var avg = sorted.Average();
            return new JObject
            {
                ["count"] = sorted.Count,
                ["avgMs"] = Math.Round(avg, 2),
                ["p50Ms"] = Math.Round(P(0.5f), 2),
                ["p95Ms"] = Math.Round(P(0.95f), 2),
                ["p99Ms"] = Math.Round(P(0.99f), 2),
                ["maxMs"] = Math.Round(sorted[^1], 2),
                ["avgFps"] = Math.Round(1000.0 / Math.Max(0.01, avg), 1),
                ["low1PercentFps"] = Math.Round(1000.0 / Math.Max(0.01, P(0.99f)), 1),
            };
        }

        public void WriteCsv()
        {
            using var w = new StreamWriter(Path.Combine(_outDir, "frames.csv"));
            w.WriteLine("seconds,frameMs,step,screenshot");
            for (var i = 0; i < _frameMs.Count; i++)
                w.WriteLine($"{_frameAt[i]:F3},{_frameMs[i]:F3},{_frameStep[i]},{(_frameCapture[i] ? 1 : 0)}");
        }

        public void WriteReport(JObject report) =>
            File.WriteAllText(Path.Combine(_outDir, "report.json"), report.ToString(Formatting.Indented));
    }
}

using System.Diagnostics;

namespace Valence.Core;

/// <summary>Overall progress of an analysis, 0..1.</summary>
public readonly record struct AnalysisProgress(string Phase, double Fraction, string? Detail);

/// <summary>
/// Passed to probes: logging, progress and cancellation. Progress is reported in the probe's own
/// 0..1 range; <see cref="Slice"/> maps that into the overall bar. Reports are throttled to ~10/s.
/// </summary>
public sealed class AnalysisContext
{
    private readonly Action<string> _log;
    private readonly Action<AnalysisProgress>? _progress;
    private readonly double _start;
    private readonly double _size;
    private readonly Throttle _throttle;

    public AnalysisContext(
        Action<string> log,
        Action<AnalysisProgress>? progress = null,
        CancellationToken cancellation = default)
        : this(log, progress, cancellation, 0, 1, new Throttle())
    {
    }

    private AnalysisContext(
        Action<string> log, Action<AnalysisProgress>? progress, CancellationToken cancellation,
        double start, double size, Throttle throttle)
    {
        _log = log;
        _progress = progress;
        Cancellation = cancellation;
        _start = start;
        _size = size;
        _throttle = throttle;
    }

    public CancellationToken Cancellation { get; }

    public void Log(string message) => _log(message);

    /// <param name="fraction">Progress within this context's slice, 0..1.</param>
    public void Report(string phase, double fraction, string? detail = null, bool force = false)
    {
        if (_progress == null) return;
        var overall = _start + _size * Math.Clamp(fraction, 0, 1);
        if (_throttle.ShouldSend(phase, force)) _progress(new AnalysisProgress(phase, overall, detail));
    }

    public void Report(string phase, int done, int total, string? detail = null) =>
        Report(phase, total <= 0 ? 1 : (double)done / total, detail, done >= total);

    /// <summary>A child context covering [start, start + size) of this one's range.</summary>
    public AnalysisContext Slice(double start, double size) =>
        new(_log, _progress, Cancellation, _start + _size * start, _size * size, _throttle);

    private sealed class Throttle
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private long _lastMs = -1000;
        private string? _lastPhase;

        public bool ShouldSend(string phase, bool force)
        {
            lock (_gate)
            {
                var now = _clock.ElapsedMilliseconds;
                if (!force && phase == _lastPhase && now - _lastMs < 100) return false;
                _lastMs = now;
                _lastPhase = phase;
                return true;
            }
        }
    }
}

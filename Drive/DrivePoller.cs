using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NanotecController
{
    /// <summary>
    /// One poll's result, produced on the poller thread and consumed on the UI thread.
    /// Positions/States are always populated (carried over when this tick didn't refresh them),
    /// so a sample superseded under UI load never loses a value.
    /// </summary>
    public sealed class DriveSample
    {
        /// <summary>This sample carries a fresh position/state read attempt (see <see cref="Error"/>).</summary>
        public bool Fresh { get; init; }
        public IReadOnlyDictionary<AxisId, long> Positions { get; init; } = new Dictionary<AxisId, long>();
        public IReadOnlyDictionary<AxisId, (string State, bool HasFault)> States { get; init; }
            = new Dictionary<AxisId, (string, bool)>();
        /// <summary>Why the position/state read failed, or null. Only meaningful when <see cref="Fresh"/>.</summary>
        public string? Error { get; init; }
        /// <summary>Analogue input 1 keyed by the DRIVE it was read from, or null when not polled.</summary>
        public IReadOnlyDictionary<AxisId, int>? Analog { get; init; }
        public bool AnalogError { get; init; }
    }

    /// <summary>
    /// Reads the drives on a dedicated thread and pushes each result to the UI thread, so no
    /// SDO round-trip ever blocks the message pump. Newest-only: a sample the UI hasn't taken
    /// yet is superseded rather than queued.
    ///
    /// Cadence, per <see cref="TICK_MS"/> tick: analogue inputs every tick while
    /// <see cref="PollAnalog"/>; positions every <see cref="POSITION_EVERY"/> ticks; statuswords
    /// every <see cref="STATE_EVERY"/> position polls (display-only, so it doesn't need the
    /// position's rate). See the Developer Guide, "GUI threading &amp; the drive poller".
    /// </summary>
    public sealed class DrivePoller : IDisposable
    {
        private const int TICK_MS = 50;         // analog / joystick rate
        private const int POSITION_EVERY = 4;   // 200 ms — live readout + soft-limit guard
        private const int STATE_EVERY = 3;      // every 3rd position poll → 600 ms
        private const int STOP_TIMEOUT_MS = 2000;

        private readonly MultiAxisController _motion;
        private readonly Control _marshal;
        private readonly Action<DriveSample> _onSample;
        private readonly IReadOnlyList<AxisId> _analogAxes;

        // Poller-thread only; every published sample is a copy.
        private readonly Dictionary<AxisId, long> _positions = new();
        private readonly Dictionary<AxisId, (string State, bool HasFault)> _states = new();

        private Task? _task;
        private CancellationTokenSource? _cts;

        private volatile bool _paused;
        private volatile bool _pollAnalog;

        private readonly object _gate = new();
        private DriveSample? _pending;
        private bool _postQueued;

        /// <param name="analogAxes">The drives whose analogue input 1 carries a joystick pot —
        /// only these are read, so the dead channel costs nothing.</param>
        public DrivePoller(MultiAxisController motion, Control marshal,
                           IReadOnlyList<AxisId> analogAxes, Action<DriveSample> onSample)
        {
            _motion = motion;
            _marshal = marshal;
            _analogAxes = analogAxes;
            _onSample = onSample;
        }

        /// <summary>Read the joystick pots every tick.</summary>
        public bool PollAnalog { get => _pollAnalog; set => _pollAnalog = value; }

        public void Start()
        {
            if (_task != null) return;
            _cts = new CancellationTokenSource();
            _task = Task.Factory.StartNew(() => Loop(_cts.Token), CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        /// <summary>Stops all bus traffic without ending the thread. Long drive ops pause the
        /// poller so it neither parks on the channel lock nor loads a timing-sensitive follow loop.</summary>
        public void Pause() => _paused = true;

        /// <summary>Resumes; the first tick back refreshes everything.</summary>
        public void Resume() => _paused = false;

        public void Dispose()
        {
            _cts?.Cancel();
            try { _task?.Wait(STOP_TIMEOUT_MS); }
            catch (AggregateException) { /* loop faulted; it no longer touches the channel */ }
            _cts?.Dispose();
            _cts = null;
            _task = null;
        }

        private void Loop(CancellationToken ct)
        {
            var clock = new Stopwatch();
            int tick = 0, posPolls = 0;
            bool wasPaused = false;

            while (!ct.IsCancellationRequested)
            {
                clock.Restart();
                if (_paused)
                {
                    wasPaused = true;
                }
                else
                {
                    if (wasPaused) { wasPaused = false; tick = 0; posPolls = 0; }   // resync after an op

                    bool doPos = tick % POSITION_EVERY == 0;
                    bool doState = doPos && posPolls % STATE_EVERY == 0;
                    if (doPos) posPolls++;
                    tick++;

                    bool doAnalog = _pollAnalog;
                    if (doPos || doAnalog) Publish(PollOnce(doPos, doState, doAnalog));
                }

                int rest = TICK_MS - (int)clock.ElapsedMilliseconds;
                if (rest > 0) ct.WaitHandle.WaitOne(rest);   // wakes immediately on cancel
            }
        }

        private DriveSample PollOnce(bool doPos, bool doState, bool doAnalog)
        {
            string? error = null;
            if (doPos)
            {
                try
                {
                    foreach (AxisId id in _motion.Axes)
                    {
                        _positions[id] = _motion.GetPosition(id);
                        if (doState) _states[id] = _motion.GetState(id);
                    }
                }
                catch (DriveException ex) { error = ex.Message; }
            }

            Dictionary<AxisId, int>? analog = null;
            bool analogError = false;
            if (doAnalog)
            {
                try
                {
                    analog = new Dictionary<AxisId, int>();
                    foreach (AxisId id in _analogAxes) analog[id] = _motion.GetAnalogInput1(id);
                }
                catch (DriveException) { analog = null; analogError = true; }
            }

            return new DriveSample
            {
                Fresh = doPos,
                Positions = new Dictionary<AxisId, long>(_positions),
                States = new Dictionary<AxisId, (string, bool)>(_states),
                Error = error,
                Analog = analog,
                AnalogError = analogError,
            };
        }

        private void Publish(DriveSample sample)
        {
            bool post;
            lock (_gate)
            {
                _pending = sample;
                post = !_postQueued;
                if (post) _postQueued = true;
            }
            if (!post) return;
            try { _marshal.BeginInvoke(new Action(Deliver)); }
            catch (InvalidOperationException) { lock (_gate) _postQueued = false; }   // handle gone (closing)
        }

        private void Deliver()
        {
            DriveSample? s;
            lock (_gate) { s = _pending; _pending = null; _postQueued = false; }
            if (s != null) _onSample(s);
        }
    }
}

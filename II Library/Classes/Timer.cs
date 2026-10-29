/* Timer.cs
    * Infirmary Integrated
 * By Ibi Keller (Tanjera), (c) 2023
 */

using System;
using System.Threading.Tasks;

namespace II {
    public class Timer {
        private int _Interval = 0;
        private bool _Locked = false;

        /* Milliseconds on Environment.TickCount64, not DateTime.Now: wall-clock time jumps back an hour
         * when daylight saving ends, which stalled every simulation timer for that hour */
        private static long Now => Environment.TickCount64;

        private long LastAt;
        private bool Running = false;

        /* For Epoch timekeeping (for simulation purposes e.g. pausing) */
        private long? PausedAt;
        private ulong? Gap = 0;
        private ulong _Epoch = 0;
        public ulong Epoch { get => _Epoch; }

        // Note: once a Gap is calculated (> 0), the Unpause() state has begun; IsPaused will be false 
        public bool IsPaused { get => PausedAt is not null; }
        public bool IsUnpausing { get => PausedAt is null && Gap > 0;}
        public bool IsRunning { get { return Running; } }

        public enum Bases {
            Realtime,
            Simulation
        }
        
        public event EventHandler<EventArgs>? Tick;
        
        ~Timer () => Dispose ();

        public void Dispose () {
            if (Tick != null) {
                foreach (Delegate d in Tick.GetInvocationList ()) {
                    Tick -= (EventHandler<EventArgs>)d;
                }
            }
        }

        public bool IsLocked { get => _Locked; }
        public int Interval { get => _Interval; }
        public int Elapsed { get => (int)System.Math.Min (Now - LastAt, int.MaxValue); }     // Unreset timer on a PC up > 24 days
        public int Remainder { get => _Interval - Elapsed; }

        public void Lock () => _Locked = true;

        public void Unlock () => _Locked = false;

        /// <summary>
        /// Starts a Timer (or continues a running Timer).
        /// Does not alter time since last tick.
        /// </summary>
        /// <returns></returns>
        public Task Start () {
            Running = true;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Adjusts Timer Interval without starting/stopping/resetting Timer.
        /// Does not alter time since last tick.
        /// </summary>
        /// <param name="interval"></param>
        /// <returns></returns>
        public Task Adjust (int interval) {
            _Interval = interval;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Adjusts Timer Interval while continuing a running Timer (or starting a stopped Timer!)
        /// Does not alter time since last tick.
        /// </summary>
        /// <param name="interval"></param>
        /// <returns></returns>
        public Task Continue (int interval) {
            _Interval = interval;
            Running = true;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Stops a Timer.
        /// Does not alter time since last tick.
        /// </summary>
        /// <returns></returns>
        public Task Stop () {
            Running = false;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Sets a Timer's Interval and resets time since last tick.
        /// </summary>
        /// <param name="interval"></param>
        /// <returns></returns>
        public Task Set (int interval) {
            _Interval = interval;
            ClearGap ();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Resets a Timer's time since last tick. Otherwise does not alter Timer state.
        /// </summary>
        /// <returns></returns>
        public Task Reset () {
            ClearGap ();

            return Task.CompletedTask;
        }

        /* Restarts the count from now: a pause before this point no longer applies */
        private void ClearGap () {
            LastAt = Now;
            Gap = 0;

            if (PausedAt is not null)
                PausedAt = LastAt;
        }

        /// <summary>
        /// Sets a Timer's Interval and resets time since last tick.
        /// </summary>
        /// <param name="interval"></param>
        /// <returns></returns>
        public async Task Reset (int interval)
            => await Set (interval);

        /// <summary>
        /// Macro for Reset() and Start()
        /// </summary>
        /// <returns></returns>
        public async Task ResetStart () {
            await Reset ();
            await Start ();
        }

        /// <summary>
        /// Macro for Reset(interval) and Start()
        /// </summary>
        /// <param name="interval"></param>
        /// <returns></returns>
        public async Task ResetStart (int interval) {
            await Reset (interval);
            await Start ();
        }

        /// <summary>
        /// Overload for Reset(int interval) and Start()
        /// </summary>
        /// <param name="interval"></param>
        /// <returns></returns>
        public async Task ResetStart (float interval)
            => await ResetStart ((int)interval);

        /// <summary>
        /// Macro for Reset() and Stop()
        /// </summary>
        /// <returns></returns>
        public async Task ResetStop () {
            await Reset ();
            await Stop ();
        }

        /// <summary>
        /// Macro for Reset(interval) and Stop()
        /// </summary>
        /// <param name="interval"></param>
        /// <returns></returns>
        public async Task ResetStop (int interval) {
            await Reset (interval);
            await Stop ();
        }

        /// <summary>
        /// Overload for Reset(int interval) and Stop()
        /// </summary>
        /// <param name="interval"></param>
        /// <returns></returns>
        public async Task ResetStop (float interval)
            => await ResetStop ((int)interval);

        /// <summary>
        /// Pauses the Timer without actually Stopping it (for Epoch time gap calculation) 
        /// </summary>
        /// <returns></returns>
        public Task Pause () {
            PausedAt ??= Now;        // Don't wipe Paused if Pause() is called multiple times!
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Unpauses the Timer and calculates Epoch time gap
        /// </summary>
        /// <returns></returns>
        public Task Unpause () {
            // Uses "partial" definition compared to IsPaused {get} because it would fault if Unpause() were called
            // twice simultaneously... only looking at PausedAt allows a smooth flow continuation
            if (PausedAt is not null) {
                Gap ??= 0;
                Gap += (ulong)(Now - PausedAt.Value);
                PausedAt = null;
            }

            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Invokes the Timer's Tick event
        /// </summary>
        /// <returns></returns>
        public Task Trigger () {
            Tick?.Invoke (this, new EventArgs ());
            return Task.CompletedTask;
        }

        /// <summary>
        /// "Runs" the Timer: checks time since last Tick and, if greater than Interval, invokes the
        /// Tick event and then resets time since the last tick.
        /// </summary>
        public void Process () {
            if (!IsRunning)
                return;

            /* System.Timers.Timer raises Elapsed on pool threads and does not wait for the last
             * handler: with many rooms on one 10 ms timer, a slow pass overlaps the next and the
             * same Timer would tick twice at once. A tick already in progress wins; skip this one. */
            if (System.Threading.Interlocked.Exchange (ref _Processing, 1) == 1)
                return;

            try {
                ProcessUnguarded ();
            } finally {
                System.Threading.Volatile.Write (ref _Processing, 0);
            }
        }

        private int _Processing = 0;

        private void ProcessUnguarded () {

            // Only run if the timer:
            // 1. is not Paused (Paused is null)
            // or 2. is Unpausing (Gap > 0)
            if (!IsPaused) {
                long now = Now, elapsed = now - LastAt;

                if (!IsUnpausing && elapsed > _Interval) {
                    // This condition is the base "Running" state
                    _Epoch += (ulong)elapsed;
                    
                    LastAt = now;
                    Tick?.Invoke (this, EventArgs.Empty);
                
                } else if (IsUnpausing && elapsed - (long)(Gap ?? 0) > _Interval) {
                    // This condition is the unpausing state; Gap is calculated but needs applying, and it is time to
                    // trigger once the paused time is taken *out* of the elapsed time
                    _Epoch += (ulong)(elapsed - (long)(Gap ?? 0));
                    Gap = 0;

                    LastAt = now;
                    Tick?.Invoke (this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Overload for Process()
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void Process (object? sender, EventArgs e)
            => Process ();
    }
}
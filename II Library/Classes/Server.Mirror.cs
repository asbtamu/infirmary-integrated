/* Sever.Mirror.cs
 * Infirmary Integrated
 * By Ibi Keller (Tanjera), (c) 2023
 */

using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using II.Settings;

namespace II.Server {
    public class Mirror {
        private int _ThreadLock = 0;
        public Timer? TimerSimulation;
        public Timer TimerUpdate = new ();

        public enum Statuses { INACTIVE, HOST, CLIENT };

        private string _Accession = "";

        public Statuses Status = Statuses.INACTIVE;

        public static string DefaultServer = "http://server.infirmary-integrated.com/";
        
        public string ServerAddress = DefaultServer,
            PasswordAccess = "",
            PasswordEdit = "";

        public DateTime PatientUpdated;

        public string Accession {
            // Can use local CultureInfo for ToUpper()- does not break functionality, works for end user
            // Rather than using Invariant culture
            get { return _Accession.ToUpper (); }
            set { _Accession = value.ToUpper (); }
        }

        
        public Mirror (Timer? timerSimulation) {
            TimerSimulation = timerSimulation;
        }

        public void ProcessTimer (object? sender, EventArgs e) {
            TimerUpdate.Process ();
        }

        public void TimerTick (Scenario.Step? step, Server s) {
            _ = TimerUpdate.ResetStart (5000);
            _ = GetStep (step, s);
        }

        /* Frees the lock so the next tick queries again; an in-flight request still completes */
        public void CancelOperation ()
            => Interlocked.Exchange (ref _ThreadLock, 0);
        
        public async Task<Server.ServerResponse> GetStep (Scenario.Step? step, Server s) {
            /* Mirroring not active; neither client or host */
            if (Status != Statuses.CLIENT)
                return Server.ServerResponse.NA;

            /* Pacing is TimerUpdate's job (q 5 s). A second "ServerQueried older than 5 s" gate used to
             * sit here, but ServerQueried is stamped after the round trip, so the tick right after
             * every update failed it and each change took ~10 s to reach a room. */

            // One request at a time; ticks arrive on thread-pool threads, so the check-and-set must be atomic
            if (Interlocked.Exchange (ref _ThreadLock, 1) == 1)
                return Server.ServerResponse.NA;

            try {
                (Server.ServerResponse resp, Scenario.Step? pBuffer) = await s.Get_StepMirror (this);

                if (pBuffer != null) {
                    step ??= new (TimerSimulation);

                    await step.Load (pBuffer.Save ());
                }

                return resp;
            } finally {
                Interlocked.Exchange (ref _ThreadLock, 0);
            }
        }

        public async Task<Server.ServerResponse> PostStep (Scenario.Step? step, Server s) {
            if (Status != Statuses.HOST)
                return Server.ServerResponse.NA;

            // Must use intermediary objects, if App.Patient is thread-locked, Waveforms stop populating!!
            string? pStr = step?.Save ();
            DateTime? pUp = step?.Physiology?.Updated;

            Regex regex = new ("^[a-zA-Z0-9]*$");
            if (Accession.Length <= 0 || !regex.IsMatch (Accession))
                return Server.ServerResponse.ErrorCredentials;

            Server.ServerResponse resp = await Server.Post_StepMirror (this, pStr, pUp);

            return resp;
        }
    }
}
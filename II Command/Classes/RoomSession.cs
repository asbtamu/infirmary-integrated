/* RoomSession.cs
 * Infirmary Integrated Command
 *
 * One instructor-controlled simulation session (one room): wraps a Scenario,
 * its simulation timer, and a Mirror in HOST mode broadcasting to the relay server.
 * Adapted from the wiring in II Simulator's Control.axaml.cs (InitScenario,
 * LoadInit/LoadValidateT1/LoadProcess, OnStepChanged) and DialogMirrorBroadcast.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using II;
using II.Server;

namespace IICMD {

    public class RoomSession : IDisposable {
        public string RoomName = "Room";
        public string? ScenarioFilePath;

        public II.Timer Timer_Simulation = new ();
        public Scenario? Scenario;
        public Server Server;
        public Mirror Mirror;

        public Physiology? Physiology { get => Scenario?.Physiology; }
        public bool IsBroadcasting { get => Mirror.Status == Mirror.Statuses.HOST; }

        /* Raised after the Scenario moves to a new Step (UI refreshes step name/progressions) */
        public event EventHandler<EventArgs>? StepChanged;

        private readonly System.Timers.Timer _TimerMain;
        private bool _Disposed = false;

        /* Last "updated" stamp sent to the relay; kept so every post is strictly newer */
        private DateTime _LastPostedStamp = DateTime.MinValue;

        private readonly System.Threading.SemaphoreSlim _PostLock = new (1, 1);

        /* Resting-default physiology broadcast while the room has no scenario loaded.
         * Rooms are permanent and scenarios are per-class, so a room is often LIVE before its
         * scenario exists. Posting a placeholder keeps the room's relay row present, which
         * matters because a room PC launched with --receive validates its credentials against
         * that row and gives up (with an error dialog) if it is missing. With the placeholder
         * the monitor connects and shows a calm default patient until the scenario arrives. */
        private Physiology? _IdlePhysiology;

        /* Record of the current scenario run (see SessionLog); null while no scenario is loaded */
        public SessionLog? Log { get; private set; }

        /* Set by instructor step actions so OnStepChanged can tell them from a step timer firing */
        private string? _NextStepCause;

        public RoomSession (System.Timers.Timer timerMain) {
            _TimerMain = timerMain;

            Server = new Server (Timer_Simulation);
            Mirror = new Mirror (Timer_Simulation);

            _TimerMain.Elapsed += Timer_Simulation.Process;
            _ = Timer_Simulation.Set (10);
            Timer_Simulation.Start ();
        }

        /* Loads a .ii scenario file (savefile type t1: metadata line, hash line, AES-encrypted data).
         * Returns false if the file is missing, fails validation, or contains no Scenario block. */

        public async Task<bool> LoadScenarioFile (string filepath) {
            /* A corrupt, truncated or still-syncing (OneDrive placeholder) file throws from the reader
             * or DecryptAES; caught here so one bad file can't take down every room's dashboard */
            try {
                return await LoadScenarioFileUnchecked (filepath);
            } catch (Exception ex) {
                CrashLog.Write ("LoadScenarioFile", ex, filepath);
                return false;
            }
        }

        private async Task<bool> LoadScenarioFileUnchecked (string filepath) {
            if (!System.IO.File.Exists (filepath))
                return false;

            string? metadata, data;
            using (StreamReader sr = new (filepath)) {
                metadata = await sr.ReadLineAsync ();
                data = await sr.ReadToEndAsync ();
            }

            if (String.IsNullOrEmpty (metadata) || !metadata.StartsWith (".ii:t1") || String.IsNullOrEmpty (data))
                return false;

            string file;
            using (StringReader sv = new (data)) {
                string? hash = (await sv.ReadLineAsync ())?.Trim ();
                file = Encryption.DecryptAES ((await sv.ReadToEndAsync ())?.Trim ());

                // Original save files used MD5, later changed to SHA256
                if (hash != Encryption.HashSHA256 (file) && hash != Encryption.HashMD5 (file))
                    return false;
            }

            /* Extract the Scenario block */
            StringBuilder? pbuffer = null;
            using (StringReader sread = new (file)) {
                string? line, pline;

                while ((line = (await sread.ReadLineAsync ())?.Trim ()) != null) {
                    if (line == "> Begin: Scenario") {
                        pbuffer = new StringBuilder ();

                        while ((pline = (await sread.ReadLineAsync ())?.Trim ()) != null
                                && pline != "> End: Scenario")
                            pbuffer.AppendLine (pline);
                    }
                }
            }

            if (pbuffer is null)                    // Not a Scenario file (e.g. a plain simulation save)
                return false;

            UnloadScenario ();

            Scenario = new Scenario (Timer_Simulation, false);
            Scenario.StepChangeRequest += OnStepChangeRequest;
            Scenario.StepChanged += OnStepChanged;
            Timer_Simulation.Tick += Scenario.ProcessTimer;

            await Scenario.Load (pbuffer.ToString ());

            if (!Scenario.IsLoaded)
                return false;

            ScenarioFilePath = filepath;

            HookPhysiology ();
            await Scenario.SetTimer ();
            await (Scenario.Physiology?.Activate () ?? Task.CompletedTask);

            Log = new SessionLog (RoomName,
                !String.IsNullOrEmpty (Scenario.Name) ? Scenario.Name : Path.GetFileNameWithoutExtension (filepath));
            Log.Add ("session_start", StepName (Scenario.Current), $"Loaded {Path.GetFileName (filepath)}", CurrentVitals ());

            return true;
        }

        private void UnloadScenario () {
            Log?.Add ("session_end", StepName (Scenario?.Current), null, CurrentVitals ());
            Log = null;

            if (Scenario is not null) {
                if (Scenario.Physiology is not null)
                    Timer_Simulation.Tick -= Scenario.Physiology.ProcessTimers;

                Timer_Simulation.Tick -= Scenario.ProcessTimer;
                Scenario.Dispose ();
                Scenario = null;
            }
        }

        private void HookPhysiology () {
            if (Scenario?.Physiology is not null)
                Timer_Simulation.Tick += Scenario.Physiology.ProcessTimers;
        }

        private void OnStepChangeRequest (object? sender, EventArgs e) {
            /* Unlink the outgoing Step's Physiology from the simulation timer prior to Step change */
            if (Scenario?.Physiology is not null)
                Timer_Simulation.Tick -= Scenario.Physiology.ProcessTimers;
        }

        private void OnStepChanged (object? sender, EventArgs e) {
            HookPhysiology ();
            _ = PostCurrentStep ();

            Log?.Add ("step", StepName (Scenario?.Current), _NextStepCause ?? "step timer", CurrentVitals ());
            _NextStepCause = null;

            StepChanged?.Invoke (this, EventArgs.Empty);
        }

        /* Broadcasting (Mirror HOST), following DialogMirrorBroadcast.SetMirrorBroadcast() */

        public async Task<Server.ServerResponse> StartBroadcast (string serverAddress, string accession, string passwordAccess, string passwordEdit) {
            Mirror.Status = Mirror.Statuses.HOST;
            Mirror.ServerAddress = String.IsNullOrEmpty (serverAddress) ? Mirror.DefaultServer : serverAddress;
            Mirror.Accession = accession;
            Mirror.PasswordAccess = passwordAccess;
            Mirror.PasswordEdit = passwordEdit;

            Server.ServerResponse resp = await PostCurrentStep ();

            /* NA = the relay answered with something unexpected (e.g. an HTML error page): nothing was stored */
            if (resp != Server.ServerResponse.Success)
                Mirror.Status = Mirror.Statuses.INACTIVE;
            else
                Log?.Add ("broadcast_start", StepName (Scenario?.Current), Mirror.Accession);

            return resp;
        }

        /* Stopping a broadcast first overwrites the room's relay row with a resting default
         * patient, so the relay never retains the *end state* of a session. The current step
         * of a room is an indirect record of what its learners did ("Shock" = they gave the
         * wrong drug), and for a graded event that is data we would rather not leave sitting
         * on a server -- ours or anyone else's. Best-effort: a killed process skips this. */

        public async Task StopBroadcast () {
            if (Mirror.Status != Mirror.Statuses.HOST)
                return;

            /* Wait out an in-flight post: the relay keeps whichever write lands last, so a live step
             * arriving after the scrub would put the session's state straight back */
            await _PostLock.WaitAsync ();

            try {
                _IdlePhysiology ??= new Physiology (Timer_Simulation);
                _IdlePhysiology.Updated = NextStamp ();

                await Mirror.PostStep (
                    new Scenario.Step (Timer_Simulation) { Physiology = _IdlePhysiology },
                    Server);
            } catch { } finally {
                Mirror.Status = Mirror.Statuses.INACTIVE;
                Log?.Add ("broadcast_stop", StepName (Scenario?.Current));
                _PostLock.Release ();
            }
        }

        public async Task<Server.ServerResponse> PostCurrentStep () {
            if (Mirror.Status != Mirror.Statuses.HOST)
                return Server.ServerResponse.NA;

            Physiology? physiology = Scenario?.Physiology;

            if (physiology is null) {
                _IdlePhysiology ??= new Physiology (Timer_Simulation);
                physiology = _IdlePhysiology;
            }

            /* Stamp the physiology as updated *now* before posting.
             *
             * A receiving client discards any step whose "updated" value is not strictly
             * newer than the last one it applied (II Library, Server.Get_StepMirror), and
             * Physiology.Updated otherwise still holds whatever date was baked into the .ii
             * file when the scenario was authored. Without this, every post looks stale, the
             * client applies nothing, and the room monitor sits on default vitals forever.
             *
             * II:SIM only avoids this as a side effect: its Control window pushes changes
             * through Physiology.UpdateParameters_*(), each of which sets Updated = UtcNow.
             * II Command posts the scenario's physiology directly, so it must stamp its own.
             *
             * The wire format (Utility.DateTime_ToString) has one-second resolution, so the
             * stamp is truncated to whole seconds and forced to advance -- two posts inside
             * the same second would otherwise serialize identically and the second would be
             * discarded as "not newer". */

            /* One post per room at a time. Posts come from the 5 s re-post, step changes and vitals
             * edits; against a slow relay they would otherwise stack up (and race on the stamp). A
             * skipped post is not lost: the next re-post carries the then-current state. */
            if (!_PostLock.Wait (0))
                return Server.ServerResponse.NA;

            try {
                physiology.Updated = NextStamp ();

                return await Mirror.PostStep (
                    new Scenario.Step (Timer_Simulation) {
                        Physiology = physiology,
                    },
                    Server);
            } finally {
                _PostLock.Release ();
            }
        }

        private DateTime NextStamp () {
            DateTime stamp = DateTime.UtcNow;
            stamp = new DateTime (stamp.Ticks - (stamp.Ticks % TimeSpan.TicksPerSecond), stamp.Kind);

            if (stamp <= _LastPostedStamp)
                stamp = _LastPostedStamp.AddSeconds (1);

            _LastPostedStamp = stamp;
            return stamp;
        }

        /* Credentials are fixed per physical room: set once, saved in the layout, baked into
         * that room PC's launcher. Refused while LIVE so a room never silently changes identity
         * under a connected client. Accession must be alphanumeric (relay + client both enforce). */
        public bool SetCredentials (string accession, string passwordAccess, string passwordEdit, out string? error) {
            error = null;

            if (IsBroadcasting) {
                error = "Stop broadcasting before changing credentials.";
                return false;
            }

            accession = accession.Trim ().ToUpperInvariant ();

            if (accession.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch (accession, "^[A-Z0-9]+$")) {
                error = "Accession must be letters and digits only.";
                return false;
            }

            passwordAccess = passwordAccess.Trim ();
            passwordEdit = passwordEdit.Trim ();

            /* Passwords end up on a .bat command line; cmd treats & | % ^ " and spaces specially */
            if (!System.Text.RegularExpressions.Regex.IsMatch (passwordAccess + passwordEdit, "^[A-Za-z0-9]*$")) {
                error = "Passwords must be letters and digits only.";
                return false;
            }

            Mirror.Accession = accession;
            Mirror.PasswordAccess = passwordAccess;
            Mirror.PasswordEdit = passwordEdit;

            return true;
        }

        /* Scenario progression controls (instructor actions) */

        /* Advance to a specific progression, or to the current Step's default when none is given.
         *
         * Guarded deliberately: Scenario.NextStep(null) resolves the destination to
         * Current.DefaultProgression.DestinationUUID, and if the scenario author never set a
         * default that is null -- which sets AtStep to null, leaves Current null, and silently
         * destroys the loaded scenario ("No scenario loaded" with no way back). A step with
         * exactly one exit falls back to that exit; otherwise this does nothing. */

        public async Task NextStep (Scenario.Step.Progression? progression = null) {
            if (Scenario is null)
                return;

            string? destination = progression?.DestinationUUID
                ?? Scenario.Current?.DefaultProgression?.DestinationUUID;

            if (String.IsNullOrEmpty (destination) && Scenario.Current?.Progressions.Count == 1)
                destination = Scenario.Current.Progressions [0].DestinationUUID;

            if (String.IsNullOrEmpty (destination))
                return;

            string? label = (progression ?? Scenario.Current?.DefaultProgression)?.Description;
            _NextStepCause = $"instructor: {(progression is null ? "Next" : "branch")}"
                + (String.IsNullOrEmpty (label) ? "" : $" \"{label}\"");

            try {
                await Scenario.NextStep (destination);
            } finally {
                _NextStepCause = null;
            }
        }

        public async Task PreviousStep () {
            if (Scenario is null)
                return;

            _NextStepCause = "instructor: Previous";

            try {
                await Scenario.LastStep ();
            } finally {
                _NextStepCause = null;
            }
        }

        /* Logged only on a real change: a debrief reader takes a "resume" on a running timer for an intervention */
        public async Task PauseStepTimer () {
            if (Scenario is null)
                return;

            if (!Scenario.ProgressTimer.IsPaused)
                Log?.Add ("step_timer_pause", StepName (Scenario.Current));
            await Scenario.PauseStep ();
        }

        public async Task PlayStepTimer () {
            if (Scenario is null)
                return;

            if (Scenario.ProgressTimer.IsPaused)
                Log?.Add ("step_timer_resume", StepName (Scenario.Current));
            await Scenario.PlayStep ();
        }

        /* Is a room PC actually reading this room? From the relay's last-read time (mirror_relay.py only).
         * Room PCs poll every 5 s, so three missed polls means the room monitor has stopped following. */
        public const int ReceivingWithinSeconds = 15;

        public (bool Supported, int? Age) Reception { get; private set; }

        public async Task CheckReception () {
            bool was = Reception.Age <= ReceivingWithinSeconds;
            Reception = IsBroadcasting ? await Server.Get_MirrorReadAge (Mirror) : (false, null);
            bool now = Reception.Age <= ReceivingWithinSeconds;

            /* Into the session log too: a monitor that dropped out mid-scenario matters in a debrief */
            if (Reception.Supported && now != was)
                Log?.Add (now ? "receiver_connected" : "receiver_lost", StepName (Scenario?.Current));
        }

        /* Called every few seconds by the dashboard so the debrief has a vitals trace */
        public void LogVitalsSample () => Log?.Add ("vitals", StepName (Scenario?.Current), null, CurrentVitals ());

        private SessionLog.Vitals? CurrentVitals () {
            Physiology? p = Physiology;
            return p is null ? null : new (p.HR, p.NSBP, p.NDBP, p.SPO2, p.RR, p.ETCO2, p.T);
        }

        private static string StepName (Scenario.Step? step)
            => step is null ? "" : !String.IsNullOrEmpty (step.Name) ? step.Name : "(unnamed step)";

        /* Debrief for the current run; null when no scenario is loaded */
        public string? WriteDebrief ()
            => Log?.WriteReport (Scenario?.Steps.Select (StepName) ?? Enumerable.Empty<string> ());

        /* Manual vitals: instructor overrides for the room's *current* step.
         *
         * Goes through Physiology.UpdateParameters_Cardiac/_Respiratory exactly as II:SIM's
         * Control window does, passing the room's present values for every parameter not
         * being edited. That resets the rhythm/respiratory baselines the same way an
         * operator-applied change would, and stamps Physiology.Updated so clients accept it.
         * The change lives on the current Step's Physiology: a step change replaces it with
         * the next step's authored values, which is the expected scenario behavior. */

        public async Task ApplyVitals (
                int hr, int nsbp, int ndbp, int spo2, double t, Cardiac_Rhythms.Values rhythm,
                int rr, int etco2) {
            Physiology? p = Physiology;

            if (p is null)
                return;

            Physiology.Vital_Signs was = p.VS_Settings;
            List<string> changes = new ();
            void Diff (string name, double before, double after) {
                if (before != after)
                    changes.Add ($"{name} {before:0.#}→{after:0.#}");
            }
            if (p.Cardiac_Rhythm.Value != rhythm)
                changes.Add ($"Rhythm {Utility.UnderscoreToSpace (p.Cardiac_Rhythm.Value.ToString ())}→{Utility.UnderscoreToSpace (rhythm.ToString ())}");
            Diff ("HR", was.HR, hr); Diff ("SBP", was.NSBP, nsbp); Diff ("DBP", was.NDBP, ndbp);
            Diff ("SpO2", was.SPO2, spo2); Diff ("RR", was.RR, rr); Diff ("EtCO2", was.ETCO2, etco2); Diff ("T", was.T, t);

            await p.UpdateParameters_Cardiac (
                hr,
                nsbp, ndbp, Physiology.CalculateMAP (nsbp, ndbp),
                spo2,
                t,
                rhythm,

                p.VS_Settings.CVP,
                p.VS_Settings.ASBP, p.VS_Settings.ADBP, p.VS_Settings.AMAP,
                p.VS_Settings.CO,
                p.PulmonaryArtery_Placement.Value,
                p.VS_Settings.PSP, p.VS_Settings.PDP, p.VS_Settings.PMP,
                p.VS_Settings.ICP, p.VS_Settings.IAP,

                p.Pacemaker_Threshold,
                p.Pulsus_Paradoxus, p.Pulsus_Alternans, p.Electrical_Alternans,
                p.Cardiac_Axis.Value,
                p.QRS_Interval, p.QTc_Interval,
                p.ST_Elevation, p.T_Elevation);

            await p.UpdateParameters_Respiratory (
                rr,
                p.Respiratory_Rhythm.Value,
                etco2,
                p.Mechanically_Ventilated,
                p.VS_Settings.RR_IE_I, p.VS_Settings.RR_IE_E);

            if (changes.Count > 0)
                Log?.Add ("vitals_override", StepName (Scenario?.Current), String.Join ("; ", changes),
                    new SessionLog.Vitals (hr, nsbp, ndbp, spo2, rr, etco2, t));

            _ = PostCurrentStep ();                 // Push immediately rather than waiting for the 5 s re-post
        }

        /* The vitals editable directly on a card (double-click to type, wheel to nudge) */
        public enum Vital { HR, NSBP, NDBP, SPO2, RR, ETCO2, T }

        public static double Step (Vital v) => v == Vital.T ? 0.1 : 1;

        public static double Clamp (Vital v, double value) => v switch {
            Vital.HR => System.Math.Clamp (value, 0, 300),
            Vital.NSBP => System.Math.Clamp (value, 0, 300),
            Vital.NDBP => System.Math.Clamp (value, 0, 300),
            Vital.SPO2 => System.Math.Clamp (value, 0, 100),
            Vital.RR => System.Math.Clamp (value, 0, 100),
            Vital.ETCO2 => System.Math.Clamp (value, 0, 100),
            Vital.T => System.Math.Round (System.Math.Clamp (value, 25, 45), 1),
            _ => value,
        };

        /* The *target* (setting) for a vital -- what an edit should start from -- as opposed
         * to the momentary actual value with variability that the card displays. */
        public double? GetSetting (Vital v) {
            Physiology.Vital_Signs? vs = Physiology?.VS_Settings;

            if (vs is null)
                return null;

            return v switch {
                Vital.HR => vs.HR,
                Vital.NSBP => vs.NSBP,
                Vital.NDBP => vs.NDBP,
                Vital.SPO2 => vs.SPO2,
                Vital.RR => vs.RR,
                Vital.ETCO2 => vs.ETCO2,
                Vital.T => vs.T,
                _ => null,
            };
        }

        /* Applies a set of overrides on top of the room's current targets in one pass */
        public async Task ApplyVitalOverrides (IReadOnlyDictionary<Vital, double> overrides) {
            Physiology? p = Physiology;

            if (p is null || overrides.Count == 0)
                return;

            Physiology.Vital_Signs vs = p.VS_Settings;

            double Pick (Vital v, double current)
                => overrides.TryGetValue (v, out double o) ? Clamp (v, o) : current;

            await ApplyVitals (
                (int)Pick (Vital.HR, vs.HR),
                (int)Pick (Vital.NSBP, vs.NSBP),
                (int)Pick (Vital.NDBP, vs.NDBP),
                (int)Pick (Vital.SPO2, vs.SPO2),
                Pick (Vital.T, vs.T),
                p.Cardiac_Rhythm.Value,
                (int)Pick (Vital.RR, vs.RR),
                (int)Pick (Vital.ETCO2, vs.ETCO2));
        }

        public void Dispose () {
            if (_Disposed)
                return;

            _Disposed = true;

            _ = StopBroadcast ();                   // Best effort on shutdown; may not complete if the process exits first
            UnloadScenario ();

            _TimerMain.Elapsed -= Timer_Simulation.Process;
            Timer_Simulation.Dispose ();
        }
    }
}

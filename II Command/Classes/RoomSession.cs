/* RoomSession.cs
 * Infirmary Integrated Command
 *
 * One instructor-controlled simulation session (one room): wraps a Scenario,
 * its simulation timer, and a Mirror in HOST mode broadcasting to the relay server.
 * Adapted from the wiring in II Simulator's Control.axaml.cs (InitScenario,
 * LoadInit/LoadValidateT1/LoadProcess, OnStepChanged) and DialogMirrorBroadcast.
 */

using System;
using System.IO;
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
        public bool IsPaused { get; private set; } = false;

        /* Raised after the Scenario moves to a new Step (UI refreshes step name/progressions) */
        public event EventHandler<EventArgs>? StepChanged;

        private readonly System.Timers.Timer _TimerMain;
        private bool _Disposed = false;

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

            return true;
        }

        private void UnloadScenario () {
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

            if (resp == Server.ServerResponse.ErrorCredentials || resp == Server.ServerResponse.ErrorNameResolution)
                Mirror.Status = Mirror.Statuses.INACTIVE;

            return resp;
        }

        public void StopBroadcast ()
            => Mirror.Status = Mirror.Statuses.INACTIVE;

        public async Task<Server.ServerResponse> PostCurrentStep () {
            if (Mirror.Status != Mirror.Statuses.HOST || Scenario?.Physiology is null)
                return Server.ServerResponse.NA;

            return await Mirror.PostStep (
                new Scenario.Step (Timer_Simulation) {
                    Physiology = Scenario.Physiology,
                },
                Server);
        }

        /* Scenario progression controls (instructor actions) */

        public async Task NextStep (Scenario.Step.Progression? progression = null)
            => await (Scenario?.NextStep (progression?.DestinationUUID) ?? Task.CompletedTask);

        public async Task PreviousStep ()
            => await (Scenario?.LastStep () ?? Task.CompletedTask);

        public async Task PauseStepTimer ()
            => await (Scenario?.PauseStep () ?? Task.CompletedTask);

        public async Task PlayStepTimer ()
            => await (Scenario?.PlayStep () ?? Task.CompletedTask);

        public void TogglePause () {
            /* Pauses/resumes this session's entire simulation (physiology + step timers) */
            if (!IsPaused)
                Timer_Simulation.Pause ();
            else
                Timer_Simulation.Unpause ();

            IsPaused = !IsPaused;
        }

        public void Dispose () {
            if (_Disposed)
                return;

            _Disposed = true;

            StopBroadcast ();
            UnloadScenario ();

            _TimerMain.Elapsed -= Timer_Simulation.Process;
            Timer_Simulation.Dispose ();
        }
    }
}

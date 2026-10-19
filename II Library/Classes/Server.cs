/* Server.cs
 * Infirmary Integrated
 * By Ibi Keller (Tanjera) (c) 2023
 *
 * Connection to MySQL database functioning as a data server for Infirmary Integrated.
 * Establishes database connection, includes functions for queries/commands.
 *
 * Note: Information for database connection (host, database, user ID and password) are
 * stored in Access.cs, to be ignored from git to prevent exposing login information to
 * public git server. On changes to Access.cs, to be archived in Access.rar with password
 * protection for version control.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using II.Localization;

namespace II.Server {
    public partial class Server {
        public Timer? TimerSimulation;
        
        /* Variables for checking for updates, running bootstrapper */
        public string UpgradeVersion = String.Empty;
        public string UpgradeWebpage = String.Empty;

        public enum ServerResponse {
            NA,
            Success,
            ErrorCredentials,
            ErrorNameResolution
        }
        
        /* One client for the process: a new HttpClient per call leaks sockets (and early returns
         * skipped Dispose entirely). 10 s, not the 100 s default, so a hung relay fails fast
         * instead of piling up requests from every 5 s poll/re-post. */
        private static readonly HttpClient _Http = new () { Timeout = TimeSpan.FromSeconds (10) };

        public Server (Timer? timerSimulation) {
            TimerSimulation = timerSimulation;
        }

        public static string FormatForPHP (string inc)
            => inc.Replace ("#", "_").Replace ("$", "_");

        public Task Get_LatestVersion () {
            /* Institutional deployment: update check disabled.
             * Upstream behavior called http://server.infirmary-integrated.com/version.php
             * on startup and via the manual menu check. Disabled so the application makes
             * no outbound calls to the official server; updates are managed institutionally.
             * UpgradeVersion remains empty -> IsNewerVersion() is false -> no upgrade prompt,
             * and the manual check reports "no update available". */
            UpgradeVersion = String.Empty;
            UpgradeWebpage = String.Empty;

            return Task.CompletedTask;
        }

        public async Task<(ServerResponse, Scenario.Step?)> Get_StepMirror (Mirror m) {
            try {
                string requri = FormatForPHP (
                    $"{m.ServerAddress}{(m.ServerAddress.EndsWith ('/') ? String.Empty : '/')}"
                    + $"mirror_get.php?accession={m.Accession}&accesshash={Encryption.HashSHA256 (m.PasswordAccess)}");
                
                HttpResponseMessage resp = await _Http.GetAsync (requri);

                // We want this exception thrown in case of web disconnect- it will finish the task and end the faux ThreadLock
                resp.EnsureSuccessStatusCode ();

                string updated, step;
                using (StringReader sr = new (await resp.Content.ReadAsStringAsync ())) {
                    updated = (await sr.ReadLineAsync ())?.Trim () ?? "";
                    step = (await sr.ReadLineAsync ())?.Trim () ?? "";
                }

                if (String.IsNullOrEmpty (updated) || String.IsNullOrEmpty (step))
                    return (ServerResponse.ErrorCredentials, null);

                DateTime serverUpdated = Utility.DateTime_FromString (updated);
                if (DateTime.Compare (serverUpdated, m.PatientUpdated) <= 0)
                    return (ServerResponse.Success, null);

                m.PatientUpdated = serverUpdated;

                Scenario.Step s = new (TimerSimulation);
                await s.Load (Encryption.DecryptAES (step.Replace (' ', '+')));

                return (ServerResponse.Success, s);
            } catch {
                return (ServerResponse.ErrorNameResolution, null);
            }
        }

        public static async Task<ServerResponse> Post_StepMirror (Mirror m, string? pStr, DateTime? pUp) {
            if (String.IsNullOrEmpty (pStr) || pUp is null) {
                return ServerResponse.NA;
            }
            
            try {
                string requri = FormatForPHP (
                    $"{m.ServerAddress}{(m.ServerAddress.EndsWith ('/') ? String.Empty : '/')}"
                    + $"mirror_post.php?accession={m.Accession}"
                    + $"&key_access={Encryption.HashSHA256 (m.PasswordAccess)}&key_edit={Encryption.HashSHA256 (m.PasswordEdit)}"
                    + $"&patient={Encryption.EncryptAES (pStr)}&updated={Utility.DateTime_ToString (pUp)}");
                
                HttpResponseMessage resp = await _Http.GetAsync (requri);

                resp.EnsureSuccessStatusCode ();
                string server_code = "";                // Response generated in mirror_post.php by printf()
                
                using (StringReader sr = new(await resp.Content.ReadAsStringAsync ())) {
                    server_code = (await sr.ReadLineAsync ())?.Trim () ?? "";
                }

                // These response codes are hard-coded into II Server/mirror_post.php
                if (server_code == "BAD_PASSWORD") {
                    return ServerResponse.ErrorCredentials;
                } else if (server_code == "ENTRY_UPDATED" || server_code == "ENTRY_ADDED") {
                    return ServerResponse.Success;
                } else {
                    return ServerResponse.NA;
                }
            } catch {
                return ServerResponse.ErrorNameResolution;
            }
        }
    }
}
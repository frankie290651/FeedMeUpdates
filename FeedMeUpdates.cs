/* FeedMeUpdates 1.5.8
   Changes: Added network failure tracking (lastNetworkFail/lastOxideStatusCode), configurable HttpTimeoutMs, unified User-Agent (FeedMeUpdates), extended feedme.status output only when called without args, restored webrequest smoke test + fallback WebClient. */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("FeedMeUpdates", "frankie290651", "1.5.8")]
    [Description("Highly configurable plugin for Oxide framework to orchestrate Server/Oxide/Plugins updates.")]
    public class FeedMeUpdates : CovalencePlugin
    {
        #region Config minimal

        private class ConfigData
        {
            public string ServerDirectory { get; set; } = "";
            public string SteamCmdPath { get; set; } = "";
            public string UpdaterExecutablePath { get; set; } = "";
            public bool ShowUpdaterConsole { get; set; } = false;

            public string ServerStartScript { get; set; } = "";
            public bool RunServerScriptHidden { get; set; } = false;
            public string ServerTmuxSession { get; set; } = "";

            public bool RustOnService { get; set; } = false;
            public string ServiceName { get; set; } = "";
            public string ServiceType { get; set; } = "";

            public int HttpTimeoutMs { get; set; } = 3000;
            public bool StartupScan { get; set; } = false;
            public int MaxAttempts { get; set; } = 0;
            public int CheckIntervalMinutes { get; set; } = 10;
            public int CountdownMinutes { get; set; } = 5;
            public bool UpdatePlugins { get; set; } = false;
            public bool OnlyServerProtocolUpdate { get; set; } = false;
            public bool UseScheme { get; set; } = false;
            public string SchemeFile { get; set; } = "";
            public string DailyRestartTime { get; set; } = "";
            public int MinutesBeforeRestart { get; set; } = 60;

            public bool DiscordNotificationsEnabled { get; set; } = false;
            public string DiscordWebhookUrl { get; set; } = "";

            public string UpdaterMarkerFileName { get; set; } = "updateresult.json";
            public string UpdaterLockFileName { get; set; } = "updating.lock";
            public string MarkersSubfolder { get; set; } = "markers";
        }

        private ConfigData configData;

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new ConfigData(), true);
        }

        #endregion

        #region Datafile name

        private const string DataFileName = "FeedMeUpdatesData";

        #endregion

        #region Runtime state

        private bool enablePlugin = true;

        private Timer DailyRestartCheckRepeater;

        private bool pendingServerUpdate = false;
        private bool pendingOxideUpdate = false;

        private bool tempPendingServerUpdate = false;
        private bool tempPendingOxideUpdate = false;

        private string localOxideVersion = null;
        private string localProtocol = null;
        private string localBuildFromServerVersion = null;

        private string LocalServerVersionString = "";

        private string LocalSteamBuildID = "";

        private string cachedRemoteBuild = null;
        private string cachedRemoteOxide = null;

        private Timer checkTimer;
        private volatile bool isFetchingRemote = false;
        private volatile bool countdownActive = false;

        private string cachedPluginVersion = null;

        private int trynumber = 0;

        private bool tmuxAvailable = false;
        private bool gnomeAvailable = false;

        private bool DoRestartTimeCheck = false;

        private int targetHour = 0;
        private int targetMinute = 0;

        Scheme scheme = new Scheme();

        private bool lastNetworkFail = false;
        private int lastOxideStatusCode = 0;

        private const string UAValue = "FeedMeUpdates";

        // Added: flag set by updater executable validation at init
        private bool updaterValidAtInit = true;

        #endregion

        #region Resilient HTTP wrapper (Solution A)

        private enum HttpBackend { Unknown, WebRequest, WebClient }
        private HttpBackend currentHttpBackend = HttpBackend.Unknown;
        private volatile bool httpBackendTested = false;
        private readonly object httpBackendLock = new object();

        /* Smoke test to select backend */
        private void EnsureHttpBackendDetected()
        {
            if (httpBackendTested) return;
            lock (httpBackendLock)
            {
                if (httpBackendTested) return;
                httpBackendTested = true;
                try
                {
                    webrequest.Enqueue("https://api.github.com/", null, (code, response) =>
                    {
                        timer.Once(0f, () =>
                        {
                            currentHttpBackend = HttpBackend.WebRequest;
                        });
                    }, this);
                    Task.Run(async () =>
                    {
                        await Task.Delay(1500).ConfigureAwait(false);
                        if (currentHttpBackend == HttpBackend.Unknown)
                            currentHttpBackend = HttpBackend.WebRequest;
                    });
                }
                catch
                {
                    currentHttpBackend = HttpBackend.WebClient;
                }
            }
        }

        /* Unified GET/POST with fallback and timeout */
        private (int statusCode, string body) ResilientHttpGetSync(string url, Dictionary<string, string> headers = null, int timeoutMs = 0)
        {
            EnsureHttpBackendDetected();
            int effectiveTimeout = timeoutMs > 0 ? timeoutMs : (configData?.HttpTimeoutMs > 0 ? configData.HttpTimeoutMs : 3000);
            try
            {
                var task = Task.Run(async () =>
                {
                    if (currentHttpBackend == HttpBackend.WebRequest)
                    {
                        try { return await HttpGetViaWebRequestAsync(url).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            Puts("WebRequest GET failed, fallback WebClient: " + ex.Message);
                            currentHttpBackend = HttpBackend.WebClient;
                            return await HttpGetViaWebClientAsync(url, headers).ConfigureAwait(false);
                        }
                    }
                    return await HttpGetViaWebClientAsync(url, headers).ConfigureAwait(false);
                });
                if (task.Wait(effectiveTimeout)) return task.Result;
                return (-1, null);
            }
            catch (Exception ex)
            {
                Puts("ResilientHttpGetSync error: " + ex.Message);
                return (-1, null);
            }
        }

        private (int statusCode, string body) ResilientHttpPostSync(string url, Dictionary<string, string> headers, string payload, int timeoutMs = 0)
        {
            EnsureHttpBackendDetected();
            int effectiveTimeout = timeoutMs > 0 ? timeoutMs : (configData?.HttpTimeoutMs > 0 ? configData.HttpTimeoutMs : 3000);
            try
            {
                var task = Task.Run(async () =>
                {
                    if (currentHttpBackend == HttpBackend.WebRequest)
                    {
                        try { return await HttpPostViaWebRequestAsync(url, payload).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            Puts("WebRequest POST failed, fallback WebClient: " + ex.Message);
                            currentHttpBackend = HttpBackend.WebClient;
                            return await HttpPostViaWebClientAsync(url, headers, payload).ConfigureAwait(false);
                        }
                    }
                    return await HttpPostViaWebClientAsync(url, headers, payload).ConfigureAwait(false);
                });
                if (task.Wait(effectiveTimeout)) return task.Result;
                return (-1, null);
            }
            catch (Exception ex)
            {
                Puts("ResilientHttpPostSync error: " + ex.Message);
                return (-1, null);
            }
        }

        /* WebRequest GET async */
        private Task<(int statusCode, string body)> HttpGetViaWebRequestAsync(string url)
        {
            var tcs = new TaskCompletionSource<(int, string)>();
            try
            {
                webrequest.Enqueue(url, null, (code, response) =>
                {
                    timer.Once(0f, () => tcs.TrySetResult((code, response)));
                }, this);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            return tcs.Task;
        }

        /* WebRequest POST async */
        private Task<(int statusCode, string body)> HttpPostViaWebRequestAsync(string url, string payload)
        {
            var tcs = new TaskCompletionSource<(int, string)>();
            try
            {
                webrequest.Enqueue(url, payload ?? "", (code, response) =>
                {
                    timer.Once(0f, () => tcs.TrySetResult((code, response)));
                }, this);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            return tcs.Task;
        }

        /* WebClient GET */
        private async Task<(int statusCode, string body)> HttpGetViaWebClientAsync(string url, Dictionary<string, string> headers = null)
        {
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers["User-Agent"] = UAValue;
                    if (headers != null)
                    {
                        foreach (var kv in headers)
                        {
                            if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) continue;
                            try { wc.Headers[kv.Key] = kv.Value; } catch { }
                        }
                    }
                    var body = await wc.DownloadStringTaskAsync(url).ConfigureAwait(false);
                    return (200, body);
                }
            }
            catch (WebException wex)
            {
                int code = -1;
                string body = null;
                try
                {
                    if (wex.Response is HttpWebResponse resp)
                    {
                        code = (int)resp.StatusCode;
                        using (var rs = resp.GetResponseStream())
                        using (var sr = new StreamReader(rs))
                            body = sr.ReadToEnd();
                    }
                }
                catch { }
                return (code, body);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        /* WebClient POST */
        private async Task<(int statusCode, string body)> HttpPostViaWebClientAsync(string url, Dictionary<string, string> headers, string payload)
        {
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers["User-Agent"] = UAValue;
                    wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                    if (headers != null)
                    {
                        foreach (var kv in headers)
                        {
                            if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) continue;
                            if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                            try { wc.Headers[kv.Key] = kv.Value; } catch { }
                        }
                    }
                    var body = await wc.UploadStringTaskAsync(url, "POST", payload ?? "").ConfigureAwait(false);
                    return (200, body);
                }
            }
            catch (WebException wex)
            {
                int code = -1;
                string body = null;
                try
                {
                    if (wex.Response is HttpWebResponse resp)
                    {
                        code = (int)resp.StatusCode;
                        using (var rs = resp.GetResponseStream())
                        using (var sr = new StreamReader(rs))
                            body = sr.ReadToEnd();
                    }
                }
                catch { }
                return (code, body);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        #endregion

        #region Scheme class and helper

        public class schemeInstruction
        {
            public bool isValid { get; set; } = false;
            public int RowNum { get; set; } = 0;
            public int ErrorColumn { get; set; } = 0;
            public string ErrorText { get; set; } = "";
            public bool OxideUpdateEvent { get; set; } = false;
            public bool OxideProtocolMatchEvent { get; set; } = false;
            public bool OxideProtocolMismatchEvent { get; set; } = false;
            public bool OxideProtocolUnknownEvent { get; set; } = false;
            public bool OxideProtocolErrorEvent { get; set; } = false;
            public bool ServerEvent { get; set; } = false;
            public bool UpdateServer { get; set; } = false;
            public bool UpdateOxide { get; set; } = false;
        }

        private class Scheme
        {
            public bool isValid { set; get; } = false;
            public string SchemePath { get; set; } = "";
            public List<schemeInstruction> instructions { get; set; } = null;
        }

        public schemeInstruction ReadSchemeInstruction(int row, string rowContent)
        {
            var instr = new schemeInstruction();
            instr.RowNum = row;

            if (string.IsNullOrWhiteSpace(rowContent))
            {
                instr.ErrorText = "Empty instruction";
                return instr;
            }

            try
            {
                var parts = rowContent.Split(new[] { '=' }, 2);
                if (parts.Length != 2)
                {
                    instr.ErrorText = "Invalid instruction format, expected 'events=action'";
                    return instr;
                }

                var left = parts[0].Trim();
                var right = parts[1].Trim();

                if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                {
                    instr.ErrorText = "Empty left or right side in instruction";
                    return instr;
                }

                if (right.Equals("oxide", StringComparison.OrdinalIgnoreCase))
                {
                    instr.UpdateOxide = true;
                    instr.UpdateServer = false;
                }
                else if (right.Equals("server", StringComparison.OrdinalIgnoreCase))
                {
                    instr.UpdateOxide = false;
                    instr.UpdateServer = true;
                }
                else if (right.Equals("both", StringComparison.OrdinalIgnoreCase))
                {
                    instr.UpdateOxide = true;
                    instr.UpdateServer = true;
                }
                else
                {
                    instr.ErrorText = $"Action '{right}' is not valid (expected 'server', 'oxide' or 'both')";
                    return instr;
                }

                var leftParts = left.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                if (leftParts.Length == 0)
                {
                    instr.ErrorText = "No events specified on left side";
                    return instr;
                }

                bool serverSet = false;
                bool oxideSet = false;

                foreach (var ev in leftParts)
                {
                    if (ev.IndexOf("server", StringComparison.OrdinalIgnoreCase) >= 0 && !serverSet)
                    {
                        instr.ServerEvent = true;
                        serverSet = true;
                        continue;
                    }

                    if (ev.IndexOf("oxide", StringComparison.OrdinalIgnoreCase) >= 0 && !oxideSet)
                    {
                        var m = Regex.Match(ev, @"oxide\s*\(([^)]*)\)", RegexOptions.IgnoreCase);
                        if (!m.Success)
                        {
                            instr.OxideProtocolErrorEvent = true;
                            instr.OxideProtocolMatchEvent = true;
                            instr.OxideProtocolMismatchEvent = true;
                            instr.OxideProtocolUnknownEvent = true;
                            instr.OxideUpdateEvent = true;
                            oxideSet = true;
                        }
                        else
                        {
                            var flags = m.Groups[1].Value;
                            if (string.IsNullOrEmpty(flags))
                            {
                                instr.ErrorText = "Oxide parentheses present but empty";
                                return instr;
                            }

                            foreach (char c in flags)
                            {
                                if (c == 'e') { instr.OxideProtocolErrorEvent = true; instr.OxideUpdateEvent = true; oxideSet = true; }
                                else if (c == 's') { instr.OxideProtocolMatchEvent = true; instr.OxideUpdateEvent = true; oxideSet = true; }
                                else if (c == 'c') { instr.OxideProtocolMismatchEvent = true; instr.OxideUpdateEvent = true; oxideSet = true; }
                                else if (c == 'u') { instr.OxideProtocolUnknownEvent = true; instr.OxideUpdateEvent = true; oxideSet = true; }
                                else if (char.IsWhiteSpace(c)) { }
                                else { instr.ErrorText = $"Unknown oxide protocol event flag: '{c}'"; return instr; }
                            }
                        }
                        continue;
                    }

                    instr.ErrorText = $"Unknown event specifier: '{ev}' (expected 'server' or 'oxide(...)')";
                    return instr;
                }

                if ((instr.ServerEvent || instr.OxideUpdateEvent) && string.IsNullOrEmpty(instr.ErrorText))
                    instr.isValid = true;
                else
                    instr.ErrorText = "No valid events parsed";
            }
            catch (Exception ex)
            {
                instr.ErrorText = "Error parsing instruction: " + ex.Message;
            }

            return instr;
        }

        public void ReadSchemeFile()
        {
            if (string.IsNullOrEmpty(configData.SchemeFile))
            {
                Puts("Scheme file path not configured.");
                return;
            }

            if (!File.Exists(configData.SchemeFile))
            {
                Puts("No scheme file found at: \"" + configData.SchemeFile + "\"");
                return;
            }
            string[] rows = File.ReadAllLines(configData.SchemeFile);
            int x = 1;

            scheme.SchemePath = configData.SchemeFile;
            scheme.instructions = new List<schemeInstruction>();
            scheme.isValid = true;

            foreach (string rawRow in rows)
            {
                var row = rawRow?.Trim() ?? "";
                if (string.IsNullOrEmpty(row) || row.StartsWith("//")) { x++; continue; }

                schemeInstruction sIn = ReadSchemeInstruction(x, row);
                if (!sIn.isValid) scheme.isValid = false;
                scheme.instructions.Add(sIn);
                x++;
            }
            if (scheme.isValid) Puts("Scheme correctly parsed. Now running on scheme behaviour.");
            else
            {
                Puts("Unable to parse scheme file. The following instructions are invalid:");
                foreach (schemeInstruction si in scheme.instructions)
                {
                    if (!si.isValid) Puts("(" + si.RowNum.ToString() + ") " + si.ErrorText);
                }
                Puts("UseScheme has been disabled. Plugin is going back to default behaviour.");
                configData.UseScheme = false;
            }
        }

        #endregion

        #region Discord Notification Helper

        private string EscapeJsonString(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "")
                    .Replace("\n", "\\n");
        }

        private async Task SendDiscordNotification(string message)
        {
            if (configData?.DiscordNotificationsEnabled != true) return;
            if (string.IsNullOrEmpty(configData?.DiscordWebhookUrl)) return;

            try
            {
                var payload = "{\"content\":\"" + EscapeJsonString(message) + "\"}";
                var headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" }
                };

                var result = await Task.Run(() => ResilientHttpPostSync(configData.DiscordWebhookUrl, headers, payload, configData.HttpTimeoutMs)).ConfigureAwait(false);
                timer.Once(0f, () =>
                {
                    if (result.statusCode >= 200 && result.statusCode < 300)
                        Puts("Discord notification sent successfully.");
                    else
                        Puts($"Error sending Discord notification: HTTP {result.statusCode} - {result.body}");
                });
            }
            catch (Exception ex)
            {
                timer.Once(0f, () => Puts("Error sending Discord notification: " + ex.Message));
            }
        }

        private void NotifyDiscordUpdateStart(bool updateServer, bool updateOxide, string remoteOxideVersion, bool protocolChanged)
        {
            if (configData?.DiscordNotificationsEnabled != true) return;

            var updateType = (updateServer && updateOxide) ? "Server & Oxide" : (updateServer ? "Server" : (updateOxide ? "Oxide" : "No update"));
            var protocolMsg = updateOxide ? (protocolChanged ? "Protocol NUMBER CHANGE" : "No protocol change") : "";
            var oxideMsg = updateOxide ? $"Remote Oxide version: {remoteOxideVersion ?? "unknown"}" : "";

            var msg = $"[FeedMeUpdates] Starting update: {updateType}\n" +
                (string.IsNullOrEmpty(oxideMsg) ? "" : oxideMsg + "\n") +
                (string.IsNullOrEmpty(protocolMsg) ? "" : protocolMsg);

            _ = SendDiscordNotification(msg);
        }

        private void NotifyDiscordUpdateResult(string result, string updateId, bool pluginsUpdated, List<string> pluginsList, string failureReason)
        {
            if (configData?.DiscordNotificationsEnabled != true) return;

            var msg = $"[FeedMeUpdates] Update FINISHED: {(string.IsNullOrEmpty(result) ? "UNKNOWN" : result.Trim().ToUpper())} (ID: {updateId ?? "unknown"})\n";
            if (!string.IsNullOrEmpty(result) && result.ToLower().Trim() == "success")
            {
                msg += "Update executed successfully.";
                if (pluginsUpdated && pluginsList != null && pluginsList.Count > 0)
                {
                    msg += $"\nUpdated plugins: {string.Join(", ", pluginsList)}";
                }
            }
            else
            {
                msg += "Update failed.";
                if (!string.IsNullOrEmpty(failureReason))
                {
                    msg += $"\nFailure reason: {failureReason}";
                }
            }

            _ = SendDiscordNotification(msg);
        }

        #endregion

        #region Init / Unload

        private void Init()
        {
            permission.RegisterPermission("feedme.run", this);
            try { cachedPluginVersion = Version.ToString(); } catch { cachedPluginVersion = "unknown"; }

            AddCovalenceCommand("feedme.testrun", nameof(Cmd_TestRun));
            AddCovalenceCommand("feedme.status", nameof(Cmd_Status));
            AddCovalenceCommand("feedme.version", nameof(Cmd_Version));

            try
            {
                configData = Config.ReadObject<ConfigData>() ?? new ConfigData();
                if (configData.HttpTimeoutMs <= 0) configData.HttpTimeoutMs = 3000;
                if (string.IsNullOrEmpty(configData.ServerStartScript) && !configData.RustOnService)
                {
                    configData.ServerStartScript = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\rust-server\start_server.bat" : "/home/rust/rust-server/start_server.sh";
                    Config.WriteObject(configData, true);
                }
            }
            catch (Exception ex)
            {
                Puts("Error loading config: " + ex.Message);
                configData = new ConfigData();
            }

            // Added: validation of updater executable at init
            try
            {
                string resolved, reason;
                if (!ValidateUpdaterExecutableAtInit(out resolved, out reason))
                {
                    updaterValidAtInit = false;
                    Puts("Updater executable validation failed at init: " + (reason ?? "unknown reason"));
                }
                else
                {
                    updaterValidAtInit = true;
                    Puts("Updater executable validated at init: " + (resolved ?? "<unknown>"));
                }
            }
            catch (Exception ex)
            {
                updaterValidAtInit = false;
                Puts("Updater executable validation error at init: " + ex.Message);
            }

            Puts($"FeedMeUpdates initialized (v{cachedPluginVersion}).");

            try { ProcessSteamTempAndDataFile(); } catch (Exception ex) { Puts("Error ProcessSteamTempAndDataFile: " + ex.Message); }
            try { ProcessTryNumber(); } catch (Exception ex) { Puts("Error ProcessTryNumber: " + ex.Message); }
            try { ProcessUpdaterMarkerIfPresent(); } catch (Exception ex) { Puts("Marker processing error: " + ex.Message); }
            try { ProcessUpdaterLockIfPresent(); } catch (Exception ex) { Puts("Lock processing error: " + ex.Message); }

            if (configData.MaxAttempts != 0 && trynumber >= configData.MaxAttempts) enablePlugin = false;
            else enablePlugin = true;

            try
            {
                try
                {
                    var rp = server.Protocol;
                    var rb = server.Version;
                    localProtocol = rp?.ToString();
                    localBuildFromServerVersion = rb?.ToString();
                }
                catch (Exception exServer)
                {
                    Puts("Error obtaining server.Protocol / server.Version: " + exServer.Message);
                }

                localOxideVersion = GetOxideVersionFromDll();
                LocalServerVersionString = localBuildFromServerVersion ?? "";
                Puts($"Local detected: protocol={(localProtocol ?? "null")} | server.Version={(localBuildFromServerVersion ?? "null")} | LocalSteamBuildID={(string.IsNullOrEmpty(LocalSteamBuildID) ? "<empty>" : LocalSteamBuildID)} | Oxide={(localOxideVersion ?? "unknown")}");
            }
            catch (Exception ex)
            {
                Puts("Error reading local values: " + ex.Message);
                return;
            }

            if (configData.UseScheme)
            {
                try { ReadSchemeFile(); }
                catch (Exception ex)
                {
                    Puts("Scheme reading error: " + ex.Message);
                    configData.UseScheme = false;
                    Puts("UseScheme has been disabled. Plugin is going back to default behaviour.");
                }
            }

            bool[] availableUnixStuff = DetectGnomeAndTmux();
            gnomeAvailable = availableUnixStuff[0];
            tmuxAvailable = availableUnixStuff[1];

            if (!string.IsNullOrEmpty(configData.DailyRestartTime) && configData.MinutesBeforeRestart > 0)
            {
                if (ValidateRestartTime())
                    DoRestartTimeCheck = true;
                if (DoRestartTimeCheck)
                {
                    int secondsToNextMinute = 60 - DateTime.Now.Second;
                    timer.Once(secondsToNextMinute, () =>
                    {
                        DailyRestartCheckerMethod();
                        DailyRestartCheckRepeater = timer.Every(60f, () => DailyRestartCheckerMethod());
                    });
                }
            }
        }

        void Unload()
        {
            checkTimer?.Destroy();
            checkTimer = null;
            DailyRestartCheckRepeater?.Destroy();
            DailyRestartCheckRepeater = null;
        }

        #endregion

        #region steambid.temp + datafile

        private void ProcessTryNumber()
        {
            if (!ReadTryNumber())
            {
                if (!WriteTryNumber())
                {
                    Puts("Unable to define try number in datafile.");
                }
            }
        }

        private bool ReadTryNumber()
        {
            try
            {
                var dict = ReadDataFileDict();
                if (dict == null)
                {
                    return false;
                }
                if (dict.ContainsKey("TryNumber") && !string.IsNullOrEmpty(dict["TryNumber"]))
                {
                    trynumber = Int32.Parse(dict["TryNumber"]);
                    return true;
                }
            }
            catch (Exception ex) { Puts("Error reading datafile: " + ex.Message); }
            return false;
        }

        private bool WriteTryNumber()
        {
            try
            {
                var dict = ReadDataFileDict() ?? new Dictionary<string, string>();
                dict["TryNumber"] = trynumber.ToString();
                WriteDataFileDict(dict);
            }
            catch (Exception ex)
            {
                Puts("Unexpected error when saving trynumber to datafile: " + ex.Message);
                return false;
            }
            return true;
        }

        private void ProcessSteamTempAndDataFile()
        {
            if (string.IsNullOrEmpty(configData.ServerDirectory))
            {
                Puts("ServerDirectory not configured; skipping steambid.temp handling.");
                return;
            }

            var tempPath = Path.Combine(configData.ServerDirectory, "steambid.temp");
            if (File.Exists(tempPath))
            {
                try
                {
                    var id = File.ReadAllText(tempPath).Trim();
                    if (!string.IsNullOrEmpty(id))
                    {
                        LocalSteamBuildID = id;
                        SaveLocalSteamToDataFile();
                        Puts($"steambid.temp found: LocalSteamBuildID set to '{LocalSteamBuildID}', saved in datafile.");
                    }
                    else
                    {
                        Puts("steambid.temp present but empty.");
                    }

                    try { File.Delete(tempPath); Puts("steambid.temp deleted after reading."); } catch (Exception exDel) { Puts("Unable to delete steambid.temp: " + exDel.Message); }
                    return;
                }
                catch (Exception ex)
                {
                    Puts("Error reading steambid.temp: " + ex.Message);
                }
            }

            var loaded = LoadLocalSteamFromDataFile();
            if (loaded) Puts($"LocalSteamBuildID loaded from datafile: {(string.IsNullOrEmpty(LocalSteamBuildID) ? "<empty>" : LocalSteamBuildID)}");
        }

        private bool LoadLocalSteamFromDataFile()
        {
            try
            {
                var dict = ReadDataFileDict();
                if (dict == null) return false;
                if (dict.ContainsKey("LocalSteamBuildID") && !string.IsNullOrEmpty(dict["LocalSteamBuildID"]))
                {
                    LocalSteamBuildID = dict["LocalSteamBuildID"];
                    return true;
                }
            }
            catch (Exception ex) { Puts("Error reading datafile: " + ex.Message); }
            return false;
        }

        private void SaveLocalSteamToDataFile()
        {
            try
            {
                var dict = ReadDataFileDict() ?? new Dictionary<string, string>();
                dict["LocalSteamBuildID"] = LocalSteamBuildID ?? "";
                WriteDataFileDict(dict);
            }
            catch (Exception ex) { Puts("Error writing datafile (LocalSteamBuildID): " + ex.Message); }
        }

        private void SaveRemoteSteamToDataFile(string remoteBuild)
        {
            try
            {
                var dict = ReadDataFileDict() ?? new Dictionary<string, string>();
                dict["RemoteSteamBuildID"] = remoteBuild ?? "";
                WriteDataFileDict(dict);
                Puts($"RemoteSteamBuildID '{remoteBuild}' saved in datafile.");
            }
            catch (Exception ex) { Puts("Error writing datafile (RemoteSteamBuildID): " + ex.Message); }
        }

        private void RemoveRemoteSteamFromDataFile()
        {
            try
            {
                var dict = ReadDataFileDict();
                if (dict == null) return;
                if (dict.ContainsKey("RemoteSteamBuildID"))
                {
                    dict.Remove("RemoteSteamBuildID");
                    WriteDataFileDict(dict);
                    Puts("RemoteSteamBuildID removed from datafile.");
                }
            }
            catch (Exception ex) { Puts("Error removing RemoteSteamBuildID: " + ex.Message); }
        }

        private Dictionary<string, string> ReadDataFileDict()
        {
            try
            {
                var dict = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, string>>(DataFileName);
                return dict ?? new Dictionary<string, string>();
            }
            catch (Exception ex) { Puts("Generic datafile read error: " + ex.Message); return new Dictionary<string, string>(); }
        }

        private void WriteDataFileDict(Dictionary<string, string> dict)
        {
            try { Interface.Oxide.DataFileSystem.WriteObject(DataFileName, dict); }
            catch (Exception ex) { Puts("Generic datafile write error: " + ex.Message); }
        }

        #endregion

        #region Marker & Lock

        private void ProcessUpdaterMarkerIfPresent()
        {
            try
            {
                if (string.IsNullOrEmpty(configData.ServerDirectory)) return;
                var markerPath = Path.Combine(configData.ServerDirectory, configData.UpdaterMarkerFileName ?? "updateresult.json");
                if (!File.Exists(markerPath)) return;

                var content = File.ReadAllText(markerPath);
                string result = ExtractJsonString(content, "result");
                if (result == "success")
                    trynumber = 1;
                if (result == "failed")
                    trynumber++;
                string update_id = ExtractJsonString(content, "update_id") ?? ExtractJsonString(content, "updateId");
                string failureReason = ExtractJsonString(content, "fail_reason") ?? ExtractJsonString(content, "error");
                List<string> pluginsUpdated = null;
                try
                {
                    var plgMatch = Regex.Match(content, "\"updated_plugins\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (plgMatch.Success)
                    {
                        var arrRaw = plgMatch.Groups[1].Value;
                        pluginsUpdated = Regex.Matches(arrRaw, "\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                    }
                }
                catch { }

                NotifyDiscordUpdateResult(result, update_id, pluginsUpdated != null && pluginsUpdated.Count > 0, pluginsUpdated, failureReason);

                Puts("=== Updater marker found ===");
                Puts($"result: {(result ?? "null")}");
                Puts($"update_id: {(update_id ?? "unknown")}");
                Puts("Full payload: " + content);
                Puts("=== End marker ===");

                try
                {
                    if (!string.IsNullOrEmpty(result) && string.Equals(result.Trim(), "success", StringComparison.OrdinalIgnoreCase))
                    {
                        var dict = ReadDataFileDict();
                        if (dict != null && dict.ContainsKey("RemoteSteamBuildID") && !string.IsNullOrEmpty(dict["RemoteSteamBuildID"]))
                        {
                            var remoteId = dict["RemoteSteamBuildID"];
                            LocalSteamBuildID = remoteId;
                            SaveLocalSteamToDataFile();
                            RemoveRemoteSteamFromDataFile();
                            Puts($"Marker success: RemoteSteamBuildID '{remoteId}' promoted to LocalSteamBuildID and removed from datafile.");
                        }
                        else
                        {
                            Puts("Marker success but no RemoteSteamBuildID to promote.");
                        }
                    }
                    else
                    {
                        Puts("Marker does not indicate success; no promotion performed.");
                    }
                }
                catch (Exception exProm) { Puts("Error promoting RemoteSteamBuildID: " + exProm.Message); }

                try
                {
                    var markersFolder = Path.Combine(configData.ServerDirectory, configData.MarkersSubfolder ?? "markers");
                    if (!Directory.Exists(markersFolder)) Directory.CreateDirectory(markersFolder);
                    var destName = "marker_" + (string.IsNullOrEmpty(update_id) ? Guid.NewGuid().ToString("N") : update_id) + ".json";
                    var destPath = Path.Combine(markersFolder, destName);
                    File.Move(markerPath, destPath);
                    Puts("Marker moved to: " + destPath);
                }
                catch (Exception ex) { Puts("Error moving marker: " + ex.Message); }
            }
            catch (Exception ex) { Puts("Error reading marker: " + ex.Message); }
        }

        private string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            try
            {
                var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
                if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value);
                m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*([^,\\}}\\n\\r]+)", RegexOptions.IgnoreCase);
                if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value.Trim().Trim('"'));
            }
            catch { }
            return null;
        }

        private void ProcessUpdaterLockIfPresent()
        {
            try
            {
                if (string.IsNullOrEmpty(configData.ServerDirectory)) return;
                var lockPath = Path.Combine(configData.ServerDirectory, configData.UpdaterLockFileName ?? "updating.lock");
                if (!File.Exists(lockPath)) return;
                Puts($"Warning: updater lock file present at {lockPath}. Updater may have been interrupted.");
                try { File.Delete(lockPath); Puts("Lock file removed."); } catch (Exception ex) { Puts("Could not remove lock file: " + ex.Message); }
            }
            catch (Exception ex) { Puts("Error processing lock: " + ex.Message); }
        }

        #endregion

        #region Update detection (async)

        private bool[] SchemeResult()
        {
            bool shithappens = false;
            string remoteBuild = null;
            string remoteOxide = null;
            bool OxideChanged = false;
            bool ServerChanged = false;
            bool? sameProtocol = null;
            bool protocolUnknown = true;

            try
            {
                remoteOxide = GetRemoteOxideVersion();
                if (string.IsNullOrEmpty(remoteOxide))
                {
                    return new bool[] { false, false, shithappens };
                }
                cachedRemoteOxide = remoteOxide;
            }
            catch (Exception ex)
            {
                shithappens = true;
                Puts("Error obtaining remote Oxide version: " + ex.Message); throw;
            }
            if (NormalizeVersionString(remoteOxide) != NormalizeVersionString(localOxideVersion))
            {
                OxideChanged = true;
            }
            if (OxideChanged)
            {
                string note;
                string oxideProtocol;
                string localProto = localProtocol;
                sameProtocol = GetOxideCompatibilityInfo(remoteOxide, out localProto, out oxideProtocol, out note);
                protocolUnknown = sameProtocol == null;
            }

            remoteBuild = GetRemoteRustBuild();
            if (string.IsNullOrEmpty(remoteBuild))
            {
                cachedRemoteBuild = remoteBuild;
                if (string.IsNullOrEmpty(LocalSteamBuildID))
                {
                    shithappens = true;
                    throw new Exception("LocalSteamBuildID empty");
                }
                else
                {
                    if (remoteBuild != LocalSteamBuildID)
                    {
                        ServerChanged = true;
                    }
                    else
                    {
                        ServerChanged = false;
                    }
                }
            }
            else
            {
                ServerChanged = false;
            }

            bool _updateServer = false;
            bool _updateOxide = false;

            if (scheme?.instructions == null)
            {
                return new bool[3] { false, false, shithappens };
            }

            foreach (schemeInstruction si in scheme.instructions)
            {
                if (OxideChanged == true)
                {
                    if (si.OxideUpdateEvent == true)
                    {
                        if (protocolUnknown)
                        {
                            if (si.OxideProtocolUnknownEvent)
                            {
                                if (ServerChanged && si.ServerEvent)
                                {
                                    if (si.UpdateOxide) _updateOxide = true;
                                    if (si.UpdateServer) _updateServer = true;
                                    break;
                                }
                                else if (!ServerChanged && !si.ServerEvent)
                                {
                                    if (si.UpdateOxide) _updateOxide = true;
                                    if (si.UpdateServer) _updateServer = true;
                                    break;
                                }
                            }
                            if (si.OxideProtocolErrorEvent)
                            {
                                if (ServerChanged && si.ServerEvent)
                                {
                                    if (si.UpdateOxide) _updateOxide = true;
                                    if (si.UpdateServer) _updateServer = true;
                                    break;
                                }
                                else if (!ServerChanged && !si.ServerEvent)
                                {
                                    if (si.UpdateOxide) _updateOxide = true;
                                    if (si.UpdateServer) _updateServer = true;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            if (sameProtocol == true && si.OxideProtocolMatchEvent)
                            {
                                if (ServerChanged && si.ServerEvent)
                                {
                                    if (si.UpdateOxide) _updateOxide = true;
                                    if (si.UpdateServer) _updateServer = true;
                                    break;
                                }
                                else if (!ServerChanged && !si.ServerEvent)
                                {
                                    if (si.UpdateOxide) _updateOxide = true;
                                    if (si.UpdateServer) _updateServer = true;
                                    break;
                                }
                            }
                            if (sameProtocol == false && si.OxideProtocolMismatchEvent)
                            {
                                if (ServerChanged && si.ServerEvent)
                                {
                                    if (si.UpdateOxide) _updateOxide = true;
                                    if (si.UpdateServer) _updateServer = true;
                                    break;
                                }
                                else if (!ServerChanged && !si.ServerEvent)
                                {
                                    if (si.UpdateOxide) _updateOxide = true;
                                    if (si.UpdateServer) _updateServer = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (si.OxideUpdateEvent == false)
                    {
                        if (ServerChanged)
                        {
                            if (si.ServerEvent)
                            {
                                if (si.UpdateServer)
                                {
                                    _updateServer = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            if (_updateOxide || _updateServer)
            {
                return new bool[3] { _updateServer, _updateOxide, shithappens };
            }

            return new bool[3] { false, false, shithappens };
        }

        private bool[] UpdateLogics()
        {
            bool shithappens = false;
            tempPendingServerUpdate = false;
            tempPendingOxideUpdate = false;

            try
            {
                string remoteOxide = null;
                try
                {
                    remoteOxide = GetRemoteOxideVersion();
                    if (string.IsNullOrEmpty(remoteOxide))
                    {
                        return new bool[] { false, false, shithappens };
                    }
                    cachedRemoteOxide = remoteOxide;
                }
                catch (Exception ex)
                {
                    Puts("Error obtaining remote Oxide version: " + ex.Message);
                    throw;
                }

                bool OxideChanged = false;
                if (NormalizeVersionString(remoteOxide) != NormalizeVersionString(localOxideVersion))
                {
                    OxideChanged = true;
                }

                bool ServerChanged = false;
                string remoteBuild = null;

                Func<bool> DetermineServerChanged = () =>
                {
                    if (pendingServerUpdate)
                    {
                        return true;
                    }

                    try
                    {
                        remoteBuild = GetRemoteRustBuild();
                        if (string.IsNullOrEmpty(remoteBuild))
                        {
                            return false;
                        }
                        cachedRemoteBuild = remoteBuild;
                    }
                    catch (Exception ex)
                    {
                        Puts("Error obtaining remote steam build (steamcmd): " + ex.Message);
                        throw;
                    }

                    if (string.IsNullOrEmpty(LocalSteamBuildID))
                    {
                        throw new Exception("LocalSteamBuildID empty");
                    }

                    return remoteBuild != LocalSteamBuildID;
                };

                if (OxideChanged)
                {
                    string localProto = localProtocol;
                    string oxideProto;
                    string note;
                    bool? compat = null;
                    try
                    {
                        compat = GetOxideCompatibilityInfo(remoteOxide, out localProto, out oxideProto, out note);
                    }
                    catch (Exception ex)
                    {
                        Puts("GetOxideCompatibilityInfo error: " + ex.Message);
                        compat = null;
                        note = "exception: " + ex.Message;
                    }

                    bool oxide_s = false, oxide_c = false, oxide_u = false, oxide_e = false;
                    if (compat.HasValue)
                    {
                        if (compat.Value) oxide_s = true;
                        else oxide_c = true;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(note))
                        {
                            var n = note.ToLowerInvariant();
                            if (n.StartsWith("exception") || n.Contains("failed") || n.Contains("error") || n.Contains("lookup failed"))
                                oxide_e = true;
                            else if (n.Contains("protocol not found") || n.Contains("no commit info"))
                                oxide_u = true;
                            else
                                oxide_u = true;
                        }
                        else
                        {
                            oxide_u = true;
                        }
                    }

                    try
                    {
                        ServerChanged = DetermineServerChanged();
                    }
                    catch
                    {
                        shithappens = true;
                        throw;
                    }

                    if (ServerChanged)
                    {
                        tempPendingServerUpdate = true;
                        tempPendingOxideUpdate = true;
                        return new bool[] { tempPendingServerUpdate, tempPendingOxideUpdate, shithappens };
                    }
                    else
                    {
                        if (oxide_s || oxide_u)
                        {
                            tempPendingOxideUpdate = true;
                            return new bool[] { tempPendingServerUpdate, tempPendingOxideUpdate, shithappens };
                        }
                        else
                        {
                            return new bool[] { false, false, shithappens };
                        }
                    }
                }
                else
                {
                    try
                    {
                        ServerChanged = DetermineServerChanged();
                    }
                    catch
                    {
                        shithappens = true;
                        throw;
                    }

                    if (ServerChanged)
                    {
                        if (configData.OnlyServerProtocolUpdate)
                        {
                            return new bool[] { false, false, shithappens };
                        }
                        else
                        {
                            tempPendingServerUpdate = true;
                            return new bool[] { tempPendingServerUpdate, tempPendingOxideUpdate, shithappens };
                        }
                    }
                    else
                    {
                        return new bool[3] { false, false, shithappens };
                    }
                }
            }
            catch
            {
                shithappens = true;
                throw;
            }
        }

        private void StartPeriodicCheck()
        {
            if (!enablePlugin)
                return;

            checkTimer?.Destroy();
            int interval = Math.Max(1, configData.CheckIntervalMinutes);
            checkTimer = timer.Every(interval * 60f, () => StartUpdateDetectionAsync());
            Puts($"Periodic checks every {interval} minutes started.");
            timer.Once(2f, () => StartUpdateDetectionAsync());
        }

        private void StartUpdateDetectionAsync()
        {
            if (isFetchingRemote)
            {
                Puts("Remote fetch already in progress; skipping this detection cycle.");
                return;
            }

            var prevPendingServer = pendingServerUpdate;
            var prevPendingOxide = pendingOxideUpdate;
            var prevTempPendingServer = tempPendingServerUpdate;
            var prevTempPendingOxide = tempPendingOxideUpdate;

            tempPendingServerUpdate = pendingServerUpdate;
            tempPendingOxideUpdate = pendingOxideUpdate;

            isFetchingRemote = true;

            Task.Run(() =>
            {
                string remoteBuild = null;
                string remoteOxide = null;
                bool[] LogicsResult = new bool[3] { false, false, false };
                try
                {
                    if (configData.UseScheme)
                        LogicsResult = SchemeResult();
                    else
                        LogicsResult = UpdateLogics();

                    tempPendingServerUpdate = LogicsResult[0];
                    tempPendingOxideUpdate = LogicsResult[1];
                    bool shithappens = LogicsResult[2];
                    if (shithappens)
                        Puts("An error occurred while completing update logic function.");

                    remoteBuild = cachedRemoteBuild;
                    remoteOxide = cachedRemoteOxide;

                    timer.Once(0f, () =>
                    {
                        pendingServerUpdate = tempPendingServerUpdate;
                        pendingOxideUpdate = tempPendingOxideUpdate;
                        Puts($"Detection complete: pendingServerUpdate={pendingServerUpdate} pendingOxideUpdate={pendingOxideUpdate}");

                        if (pendingServerUpdate && !string.IsNullOrEmpty(remoteBuild))
                        {
                            SaveRemoteSteamToDataFile(remoteBuild);
                        }

                        if (pendingServerUpdate || pendingOxideUpdate)
                        {
                            BeginUpdateCountdown(remoteBuild, remoteOxide);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Puts("Detection error: " + ex.Message + " - skipping this cycle and restoring previous pending/temp flags.");
                    pendingServerUpdate = prevPendingServer;
                    pendingOxideUpdate = prevPendingOxide;
                    tempPendingServerUpdate = prevTempPendingServer;
                    tempPendingOxideUpdate = prevTempPendingOxide;
                }
                finally
                {
                    isFetchingRemote = false;
                }
            });
        }

        #endregion

        #region Remote helpers

        private string GetRemoteRustBuild()
        {
            try
            {
                if (string.IsNullOrEmpty(configData.SteamCmdPath) || !File.Exists(configData.SteamCmdPath))
                {
                    Puts("steamcmd not found: " + configData.SteamCmdPath);
                    return null;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = configData.SteamCmdPath,
                    Arguments = "+login anonymous +app_info_print 258550 +quit",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(60000);
                    return ParseBuildId(output);
                }
            }
            catch (Exception ex)
            {
                Puts("GetRemoteRustBuild error: " + ex.Message);
                throw;
            }
        }

        private string ParseBuildId(string steamOutput)
        {
            if (string.IsNullOrEmpty(steamOutput)) return null;
            var publicBuildMatch = Regex.Match(steamOutput, @"public\s*\{[\s\S]*?""buildid""\s*""([0-9]{5,12})""", RegexOptions.IgnoreCase);
            if (publicBuildMatch.Success && publicBuildMatch.Groups.Count >= 2)
            {
                var build = publicBuildMatch.Groups[1].Value;
                return build;
            }
            return null;
        }

        private string GetRemoteOxideVersion()
        {
            try
            {
                int timeout = configData.HttpTimeoutMs > 0 ? configData.HttpTimeoutMs : 3000;
                var res = ResilientHttpGetSync("https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest", null, timeout);
                if (res.statusCode == -1)
                {
                    lastNetworkFail = true;
                    lastOxideStatusCode = -1;
                    Puts("[FeedMeUpdates] Oxide fetch timeout.");
                    return null;
                }
                if (res.statusCode != 200 || string.IsNullOrEmpty(res.body))
                {
                    lastNetworkFail = true;
                    lastOxideStatusCode = res.statusCode;
                    Puts($"[FeedMeUpdates] Oxide fetch HTTP {res.statusCode}.");
                    if (res.statusCode == 403) Puts("[FeedMeUpdates] Possible GitHub rate limit.");
                    return null;
                }
                lastNetworkFail = false;
                lastOxideStatusCode = res.statusCode;
                var tagMatch = Regex.Match(res.body, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"", RegexOptions.IgnoreCase);
                string tag = tagMatch.Success ? tagMatch.Groups[1].Value : null;
                return tag;
            }
            catch (Exception ex)
            {
                lastNetworkFail = true;
                lastOxideStatusCode = 0;
                Puts("GetRemoteOxideVersion error: " + ex.Message);
                throw;
            }
        }

        #endregion

        #region Local Oxide via FileVersionInfo

        private string GetOxideVersionFromDll()
        {
            try
            {
                var candidates = new[]
                {
                    Path.Combine(configData.ServerDirectory ?? "", "oxide", "managed", "Oxide.Rust.dll"),
                    Path.Combine(configData.ServerDirectory ?? "", "RustDedicated_Data", "Managed", "Oxide.Rust.dll"),
                    Path.Combine(configData.ServerDirectory ?? "", "oxide", "Oxide.Rust.dll"),
                    Path.Combine(configData.ServerDirectory ?? "", "Oxide.Rust.dll")
                };

                foreach (var p in candidates)
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        try
                        {
                            var fv = FileVersionInfo.GetVersionInfo(p);
                            string raw = !string.IsNullOrEmpty(fv.ProductVersion) ? fv.ProductVersion : fv.FileVersion;
                            if (!string.IsNullOrEmpty(raw))
                            {
                                return NormalizeVersionString(raw);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return "unknown";
        }

        private string NormalizeVersionString(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            var m = Regex.Match(v, @"\d+\.\d+\.\d+");
            if (m.Success) return m.Groups[0].Value;
            var idx = v.IndexOf('+');
            if (idx > 0) return v.Substring(0, idx);
            idx = v.IndexOf('-');
            if (idx > 0) return v.Substring(0, idx);
            return v;
        }

        #endregion

        #region Compatibility check

        private bool? GetOxideCompatibilityInfo(string oxideTag, out string localProto, out string oxideProto, out string note)
        {
            localProto = localProtocol;
            oxideProto = null;
            note = null;

            if (string.IsNullOrEmpty(oxideTag)) { note = "no oxide tag"; return null; }
            if (string.IsNullOrEmpty(localProto)) { note = "local protocol unknown"; return null; }

            try
            {
                int timeout = configData.HttpTimeoutMs > 0 ? configData.HttpTimeoutMs : 3000;
                string commitUrl = $"https://api.github.com/repos/OxideMod/Oxide.Rust/commits/{WebUtility.UrlEncode(oxideTag)}";
                var r = ResilientHttpGetSync(commitUrl, null, timeout);
                string commitJson = null;

                if (r.statusCode == 200) commitJson = r.body;
                else
                {
                    var relUrl = $"https://api.github.com/repos/OxideMod/Oxide.Rust/releases/tags/{WebUtility.UrlEncode(oxideTag)}";
                    var relR = ResilientHttpGetSync(relUrl, null, timeout);
                    if (relR.statusCode == 200 && !string.IsNullOrEmpty(relR.body))
                    {
                        var tc = Regex.Match(relR.body, "\"target_commitish\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                        var commitish = tc.Success ? tc.Groups[1].Value : null;
                        if (!string.IsNullOrEmpty(commitish))
                        {
                            commitUrl = $"https://api.github.com/repos/OxideMod/Oxide.Rust/commits/{WebUtility.UrlEncode(commitish)}";
                            var commitR = ResilientHttpGetSync(commitUrl, null, timeout);
                            if (commitR.statusCode == 200) commitJson = commitR.body;
                        }
                    }
                }

                if (string.IsNullOrEmpty(commitJson)) { note = "no commit info"; return null; }

                var msgMatch = Regex.Match(commitJson, "\"message\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (msgMatch.Success)
                {
                    var msg = WebUtility.HtmlDecode(msgMatch.Groups[1].Value);
                    var pm = Regex.Match(msg, @"protocol[^\d\n\r]{0,10}(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
                    if (pm.Success) oxideProto = pm.Groups[1].Value;
                    else
                    {
                        var f = Regex.Match(msg, @"(\d+\.\d+\.\d+)");
                        if (f.Success) oxideProto = f.Groups[1].Value;
                    }
                }

                if (string.IsNullOrEmpty(oxideProto))
                {
                    var anyProto = Regex.Match(commitJson, @"protocol[^\d\n\r]{0,10}(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
                    if (anyProto.Success) oxideProto = anyProto.Groups[1].Value;
                }

                if (string.IsNullOrEmpty(oxideProto)) { note = "oxide protocol not found"; return null; }
                if (oxideProto == localProto) { note = "protocols match"; return true; }
                note = "protocols differ"; return false;
            }
            catch (Exception ex) { note = "exception: " + ex.Message; return null; }
        }

        #endregion

        #region Countdown & Updater invocation

        private void BeginUpdateCountdown(string remoteBuild, string remoteOxide)
        {
            if (countdownActive)
            {
                Puts("Countdown already active; skipping new countdown.");
                return;
            }

            int minutes = Math.Max(1, configData.CountdownMinutes);
            int remaining = minutes;

            bool updateServer = pendingServerUpdate || !string.IsNullOrEmpty(remoteBuild);
            bool updateOxide = pendingOxideUpdate || !string.IsNullOrEmpty(remoteOxide);

            BroadcastToPlayers($"Notice: update detected. Server will restart to apply updates in {minutes} minute(s).");
            Puts($"Countdown started: {minutes} minute(s) — server={updateServer} oxide={updateOxide}");
            countdownActive = true;

            Action tick = null;
            tick = () =>
            {
                if (remaining <= 0)
                {
                    BroadcastToPlayers("Saving world before update...");
                    try { ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.save"); } catch (Exception ex) { Puts("server.save error in countdown: " + ex.Message); }

                    string remoteBuildArg = pendingServerUpdate ? remoteBuild : null;
                    string remoteOxideArg = pendingOxideUpdate ? remoteOxide : null;

                    StartUpdaterExecutable(updateServer: pendingServerUpdate, updateOxide: pendingOxideUpdate, remoteBuildArg: remoteBuildArg, remoteOxideArg: remoteOxideArg);

                    Puts("Countdown end: scheduling graceful save+quit...");
                    PerformSaveAndQuitGracefully();

                    countdownActive = false;
                    return;
                }

                BroadcastToPlayers($"ATTENTION: server will restart for update in {remaining} minute(s).");
                remaining--;
                timer.Once(60f, tick);
            };

            timer.Once(60f, tick);
        }

        #endregion

        #region StartUpdaterExecutable

        private void StartUpdaterExecutable(bool updateServer, bool updateOxide, string overrideUpdateId = null, string overrideWhat = null, string remoteBuildArg = null, string remoteOxideArg = null)
        {
            try
            {
                if (!updaterValidAtInit)
                {
                    Puts("Cannot launch updater: updater executable failed validation at init.");
                    BroadcastToAdmins("Updater launch aborted: invalid updater executable detected at init.");
                    return;
                }

                string updateId = !string.IsNullOrEmpty(overrideUpdateId) ? overrideUpdateId : new System.Random().Next(0, 100000000).ToString("D8");
                string what = !string.IsNullOrEmpty(overrideWhat) ? overrideWhat : (updateServer && updateOxide ? "both" : (updateServer ? "server" : (updateOxide ? "oxide" : "none")));
                //string updatePluginsArg = configData.UpdatePlugins ? "yes" : "no";

                var exePath = configData.UpdaterExecutablePath;

                bool protocolChanged = false;
                string remoteOxideVersion = remoteOxideArg;
                if (updateOxide && !string.IsNullOrEmpty(remoteOxideVersion))
                {
                    string _localProto, _oxideProto, _note;
                    var compat = GetOxideCompatibilityInfo(remoteOxideVersion, out _localProto, out _oxideProto, out _note);
                    protocolChanged = compat.HasValue && compat.Value == false;
                }
                NotifyDiscordUpdateStart(updateServer, updateOxide, remoteOxideVersion, protocolChanged);

                if (string.IsNullOrEmpty(exePath))
                {
                    Puts("UpdaterExecutablePath not configured.");
                    BroadcastToAdmins("Updater executable not configured; cannot start updater.");
                    return;
                }
                if (!Path.IsPathRooted(exePath) && !string.IsNullOrEmpty(configData.ServerDirectory))
                {
                    var candidate = Path.Combine(configData.ServerDirectory, exePath);
                    if (File.Exists(candidate)) exePath = candidate;
                }
                if (!File.Exists(exePath))
                {
                    Puts("Updater executable not found: " + exePath);
                    BroadcastToAdmins("Updater executable not found; check UpdaterExecutablePath.");
                    return;
                }

                string remoteBuildArgEsc = (!string.IsNullOrEmpty(overrideUpdateId) && overrideUpdateId == "init") ? "no" : (updateServer ? (remoteBuildArg ?? "") : "no");
                string remoteOxideArgEsc = (!string.IsNullOrEmpty(overrideUpdateId) && overrideUpdateId == "init") ? "no" : (updateOxide ? (remoteOxideArg ?? "") : "no");

                string args;
                {
                    string argFailReason;
                    if (!ValidateAndBuildArgsForStart(updateId, what, remoteBuildArgEsc, remoteOxideArgEsc, out args, out argFailReason))
                    {
                        Puts("Argument validation failed: " + argFailReason);
                        BroadcastToAdmins("Updater launch aborted: invalid arguments.");
                        return;
                    }
                }

                Puts($"Preparing to launch updater: {exePath} {args} (ShowUpdaterConsole={configData.ShowUpdaterConsole})");

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (configData.ShowUpdaterConsole)
                    {
                        var cmd = $"cmd.exe";
                        var cmdArgs = $"/c start \"FeedMeUpdates\" \"{exePath}\" {args}";
                        var psi = new ProcessStartInfo { FileName = cmd, Arguments = cmdArgs, UseShellExecute = true, CreateNoWindow = false, WorkingDirectory = Path.GetDirectoryName(exePath) ?? configData.ServerDirectory };
                        Process.Start(psi);
                    }
                    else
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = args,
                            WorkingDirectory = Path.GetDirectoryName(exePath) ?? configData.ServerDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = false,
                            RedirectStandardError = false
                        };
                        Process.Start(psi);
                    }
                }
                else
                {
                    string EscapeShellArg(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

                    if (configData.ShowUpdaterConsole)
                    {
                        if (gnomeAvailable)
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = "gnome-terminal",
                                Arguments = "-- bash -lc " + EscapeShellArg($"exec '{exePath}' {args}"),
                                UseShellExecute = false,
                                RedirectStandardOutput = false,
                                RedirectStandardError = false
                            };
                            Process.Start(psi);
                        }
                        if (!gnomeAvailable && tmuxAvailable)
                        {
                            if (TmuxSessionExists("feedmeupdate"))
                            {
                                var psi = new ProcessStartInfo
                                {
                                    FileName = "tmux",
                                    Arguments = $"send-keys -t feedmeupdate " + EscapeShellArg($"bash -lc \"exec '{exePath}' {args}\"") + " C-m",
                                    UseShellExecute = false,
                                    RedirectStandardOutput = false,
                                    RedirectStandardError = false
                                };
                                Process.Start(psi);
                            }
                            else
                            {
                                var psi = new ProcessStartInfo
                                {
                                    FileName = "tmux",
                                    Arguments = $"new-session -d -s feedmeupdate " + EscapeShellArg($"exec '{exePath}' {args}"),
                                    UseShellExecute = false,
                                    RedirectStandardOutput = false,
                                    RedirectStandardError = false
                                };
                                Process.Start(psi);
                            }
                        }
                        if (!gnomeAvailable && !tmuxAvailable)
                        {
                            var logRedirect = ">/dev/null 2>&1";
                            var bashArgs = $"-c \"nohup \\\"{exePath}\\\" {args} {logRedirect} &\"";
                            var psi = new ProcessStartInfo { FileName = "/bin/bash", Arguments = bashArgs, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = configData.ServerDirectory };
                            Process.Start(psi);
                        }
                    }
                    else
                    {
                        var logRedirect = ">/dev/null 2>&1";
                        var bashArgs = $"-c \"nohup \\\"{exePath}\\\" {args} {logRedirect} &\"";
                        var psi = new ProcessStartInfo { FileName = "/bin/bash", Arguments = bashArgs, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = configData.ServerDirectory };
                        Process.Start(psi);
                    }
                }
            }
            catch (Exception ex)
            {
                Puts("Error launching updater: " + ex.Message);
                BroadcastToAdmins("Error launching updater. Check permissions and executable path.");
            }
            WriteTryNumber();
        }

        #endregion

        #region Commands

        private void Cmd_Status(IPlayer player, string command, string[] args)
        {
            if (!HasPermissionOrConsole(player)) return;

            if (args != null && args.Length > 0)
            {
                var a = args[0].ToLowerInvariant();
                if (a == "on" || a == "enable")
                {
                    StartPeriodicCheck();
                    player?.Message("FeedMeUpdates: periodic checks enabled.");
                    Puts("Periodic checks enabled via command.");
                    return;
                }
                else if (a == "off" || a == "disable")
                {
                    checkTimer?.Destroy();
                    checkTimer = null;
                    player?.Message("FeedMeUpdates: periodic checks disabled.");
                    Puts("Periodic checks disabled via command.");
                    return;
                }
                else
                {
                    player?.Message("Usage: feedme.status [on|off]");
                    return;
                }
            }

            var status = checkTimer == null ? "OFF" : "ON";
            string extra = $"backend={currentHttpBackend} httpTimeoutMs={(configData?.HttpTimeoutMs ?? 0)} lastOxideStatus={lastOxideStatusCode} netFail={(lastNetworkFail ? "YES" : "NO")} scheme={(configData.UseScheme ? "ON" : "OFF")}";
            string buildLocal = string.IsNullOrEmpty(LocalSteamBuildID) ? "<empty>" : LocalSteamBuildID;
            player?.Message($"FeedMeUpdates status: {status}. pendingServerUpdate={pendingServerUpdate} pendingOxideUpdate={pendingOxideUpdate} LocalSteamBuildID={buildLocal} {extra}");
            Puts($"Status requested. {status}. pendingServerUpdate={pendingServerUpdate} pendingOxideUpdate={pendingOxideUpdate} {extra}");
        }

        private void Cmd_Version(IPlayer player, string command, string[] args)
        {
            if (!HasPermissionOrConsole(player)) return;

            string pluginVersion = cachedPluginVersion ?? "unknown";
            string serverVersion = LocalServerVersionString ?? "";
            string buildLocal = string.IsNullOrEmpty(LocalSteamBuildID) ? "<empty>" : LocalSteamBuildID;
            string protocolLocal = localProtocol ?? "<unknown>";
            string oxideVersion = NormalizeVersionString(localOxideVersion) ?? "unknown";

            var outMsg = $"FeedMeUpdates v{pluginVersion} | Rust version={serverVersion} | build={buildLocal} | protocol={protocolLocal} | Oxide v{oxideVersion}";
            player?.Message(outMsg);
            Puts(outMsg);
        }

        private void Cmd_TestRun(IPlayer player, string command, string[] args)
        {
            if (!HasPermissionOrConsole(player)) return;

            var who = player == null ? "Console" : player.Name;
            Puts($"[{who}] invoked testrun: immediate update (server+oxide).");

            pendingServerUpdate = true;
            pendingOxideUpdate = true;

            try { cachedRemoteBuild = GetRemoteRustBuild(); } catch { cachedRemoteBuild = null; }
            try { cachedRemoteOxide = GetRemoteOxideVersion(); } catch { cachedRemoteOxide = null; }

            if (pendingServerUpdate && !string.IsNullOrEmpty(cachedRemoteBuild))
            {
                SaveRemoteSteamToDataFile(cachedRemoteBuild);
            }

            timer.Once(0.5f, () =>
            {
                string remoteBuildArg = pendingServerUpdate ? cachedRemoteBuild : null;
                string remoteOxideArg = pendingOxideUpdate ? cachedRemoteOxide : null;

                StartUpdaterExecutable(updateServer: true, updateOxide: true, overrideUpdateId: "testrun", overrideWhat: "both", remoteBuildArg: remoteBuildArg, remoteOxideArg: remoteOxideArg);

                Puts("Testrun: scheduling graceful save+quit...");
                PerformSaveAndQuitGracefully();
            });
        }

        #endregion

        #region Utilities

        public bool ValidateRestartTime()
        {
            if (string.IsNullOrWhiteSpace(configData.DailyRestartTime) || !TryParseHourMinute(configData.DailyRestartTime, out targetHour, out targetMinute))
            {
                Puts($"DailyRestartTime can't be parsed to a valid HH:MM format. Please fix it.");
                DoRestartTimeCheck = false;
                return false;
            }
            return true;
        }

        private void DailyRestartCheckerMethod()
        {
            DateTime now = DateTime.Now;
            DateTime target = new DateTime(now.Year, now.Month, now.Day, targetHour, targetMinute, 0);

            if (target <= now) target = target.AddDays(1);

            double remainingMinutes = (target - now).TotalMinutes;
            int remainingRounded = (int)Math.Round(remainingMinutes);

            if (remainingRounded == configData.MinutesBeforeRestart)
            {
                checkTimer?.Destroy();
                checkTimer = null;
                DailyRestartCheckRepeater?.Destroy();
                DailyRestartCheckRepeater = null;
                DoRestartTimeCheck = false;
                Puts("Periodic check for updates has been disabled due to proximity to the daily restart.");
            }
        }

        private bool TryParseHourMinute(string input, out int hour, out int minute)
        {
            hour = 0;
            minute = 0;
            if (TimeSpan.TryParseExact(input, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan ts) ||
                TimeSpan.TryParseExact(input, "h\\:mm", CultureInfo.InvariantCulture, out ts))
            {
                if (ts.TotalHours >= 0 && ts.TotalHours < 24)
                {
                    hour = ts.Hours;
                    minute = ts.Minutes;
                    return true;
                }
            }

            if (DateTime.TryParseExact(input, new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
            {
                hour = dt.Hour;
                minute = dt.Minute;
                return true;
            }

            return false;
        }

        public static bool TmuxSessionExists(string sessionName, int timeoutMs = 2000)
        {
            if (string.IsNullOrWhiteSpace(sessionName)) return false;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "tmux",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                    Arguments = $"has-session -t {sessionName}"
                };

                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return false;

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }

                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool[] DetectGnomeAndTmux()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return new bool[] { false, false };

            bool Exists(string name)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "sh",
                        Arguments = $"-c \"command -v {name} >/dev/null 2>&1 && printf 1 || printf 0\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null) return false;
                        string outp = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit(500);
                        return outp == "1";
                    }
                }
                catch
                {
                    return false;
                }
            }

            bool gnome = Exists("gnome-terminal") || Exists("gnome-terminal-server");
            bool tmux = Exists("tmux");
            return new bool[] { gnome, tmux };
        }

        private bool HasPermissionOrConsole(IPlayer player)
        {
            if (player == null) return true;
            if (player.IsServer) return true;
            if (player.IsAdmin) return true;
            if (permission.UserHasPermission(player.Id, "feedme.run")) return true;
            player.Message("You do not have permission to run this command.");
            return false;
        }

        private void BroadcastToPlayers(string message)
        {
            try
            {
                foreach (var pl in players.Connected) pl.Message(message);
                Puts($"Broadcast: {message}");
            }
            catch (Exception ex) { Puts("Broadcast error: " + ex.Message); }
        }

        private void BroadcastToAdmins(string message)
        {
            try
            {
                foreach (var pl in players.Connected)
                {
                    if (pl.IsServer || pl.IsAdmin || permission.UserHasPermission(pl.Id, "feedme.run")) pl.Message(message);
                }
                Puts($"Broadcast(admins): {message}");
            }
            catch (Exception ex) { Puts("BroadcastToAdmins error: " + ex.Message); }
        }

        // Server shutdown sequence: write configuration then quit after 500 ms.
        private void PerformSaveAndQuitGracefully()
        {
            try
            {
                RunSequence(
                    (0f, () =>
                    {
                        Puts("FeedMeUpdates: server.writecfg...");
                        ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.writecfg");
                    }),
                    (0.5f, () =>
                    {
                        Puts("FeedMeUpdates: quit...");
                        ConsoleSystem.Run(ConsoleSystem.Option.Server, "quit");
                    })
                );
            }
            catch (Exception ex)
            {
                Puts("PerformSaveAndQuitGracefully error: " + ex.Message);
            }
        }

        private void RunSequence(params (float delay, Action action)[] steps)
        {
            void RunStep(int i)
            {
                if (i >= steps.Length) return;
                var (delay, act) = steps[i];
                timer.Once(delay, () =>
                {
                    try { act?.Invoke(); } catch (Exception ex) { Puts("Sequence step error: " + ex.Message); }
                    RunStep(i + 1);
                });
            }
            RunStep(0);
        }

        #endregion

        #region Hooks

        void OnServerInitialized()
        {
            if (string.IsNullOrEmpty(LocalSteamBuildID))
            {
                Puts("OnServerInitialized: LocalSteamBuildID is empty -> starting updater immediately (init flow).");
                StartUpdaterExecutable(updateServer: true, updateOxide: true, overrideUpdateId: "init", overrideWhat: "both", remoteBuildArg: "no", remoteOxideArg: "no");
                Puts("OnServerInitialized: scheduled save+quit after updater start (init).");
                PerformSaveAndQuitGracefully();
                return;
            }

            if (!string.IsNullOrEmpty(LocalSteamBuildID) && configData != null && configData.StartupScan)
            {
                Puts("OnServerInitialized: StartupScan enabled -> checking for available updates now.");

                tempPendingServerUpdate = false;
                tempPendingOxideUpdate = false;
                pendingServerUpdate = false;
                pendingOxideUpdate = false;
                bool shithappens = false;
                bool updateServer = false;
                bool updateOxide = false;
                string remoteBuild = null;
                string remoteOxide = null;
                bool[] LogicsResult = new bool[] { false, false, false };

                if (!configData.UseScheme)
                    LogicsResult = UpdateLogics();
                else
                    LogicsResult = SchemeResult();

                tempPendingServerUpdate = LogicsResult[0];
                tempPendingOxideUpdate = LogicsResult[1];
                shithappens = LogicsResult[2];
                if (shithappens)
                    Puts("An error occurred while completing update logic function.");

                if (tempPendingServerUpdate)
                {
                    pendingServerUpdate = true;
                    updateServer = true;
                }
                if (tempPendingOxideUpdate)
                {
                    pendingOxideUpdate = true;
                    updateOxide = true;
                }

                if (updateServer || updateOxide)
                {
                    if (updateServer && !string.IsNullOrEmpty(remoteBuild))
                    {
                        SaveRemoteSteamToDataFile(remoteBuild);
                    }

                    string what = (updateServer && updateOxide) ? "both" : (updateServer ? "server" : "oxide");
                    string remoteBuildArg = updateServer ? remoteBuild : null;
                    string remoteOxideArg = updateOxide ? remoteOxide : null;

                    Puts($"StartupScan: updates available (server={updateServer}, oxide={updateOxide}) -> launching updater immediately (what={what}).");
                    StartUpdaterExecutable(updateServer: updateServer, updateOxide: updateOxide, overrideUpdateId: null, overrideWhat: what, remoteBuildArg: remoteBuildArg, remoteOxideArg: remoteOxideArg);

                    Puts("StartupScan: scheduling graceful save+quit after updater start.");
                    PerformSaveAndQuitGracefully();
                    return;
                }
                else
                {
                    if (lastNetworkFail)
                        Puts("StartupScan: remote Oxide fetch failed; skipping update for safety.");
                    else
                        Puts("StartupScan: no updates available at startup.");
                }
            }

            if (checkTimer == null) StartPeriodicCheck();
        }

        void OnServerSave() { }

        #endregion

        #region Validation helpers (added)

        // Validates updater executable at init (existence, not directory, size>0; on Linux resolves symlink and checks +x)
        private bool ValidateUpdaterExecutableAtInit(out string resolvedPath, out string reason)
        {
            resolvedPath = null;
            reason = null;
            try
            {
                var exePath = configData?.UpdaterExecutablePath ?? "";
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    reason = "UpdaterExecutablePath is empty.";
                    return false;
                }

                if (!Path.IsPathRooted(exePath) && !string.IsNullOrEmpty(configData?.ServerDirectory))
                {
                    var candidate = Path.Combine(configData.ServerDirectory, exePath);
                    if (File.Exists(candidate)) exePath = candidate;
                }

                try { exePath = Path.GetFullPath(exePath); } catch { }

                if (!File.Exists(exePath))
                {
                    reason = "Updater executable not found: " + exePath;
                    return false;
                }

                var attr = File.GetAttributes(exePath);
                if ((attr & FileAttributes.Directory) != 0)
                {
                    reason = "Updater path is a directory: " + exePath;
                    return false;
                }

                var fi = new FileInfo(exePath);
                if (fi.Length == 0)
                {
                    reason = "Updater file has zero length: " + exePath;
                    return false;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    string target = TryResolveSymlinkUnix(exePath) ?? exePath;
                    if (!IsExecutableUnix(target))
                    {
                        reason = "Updater is not executable (+x missing): " + target;
                        return false;
                    }
                    if (!string.Equals(target, exePath, StringComparison.Ordinal))
                        Puts("Init: updater is a symlink -> resolved target: " + target);
                    resolvedPath = target;
                    return true;
                }

                resolvedPath = exePath;
                return true;
            }
            catch (Exception ex)
            {
                reason = "Exception during updater validation: " + ex.Message;
                return false;
            }
        }

        // Builds and validates a platform-safe arguments string for updater execution
        private bool ValidateAndBuildArgsForStart(string updateId, string what, string remoteBuildArgValue, string remoteOxideArgValue, out string argsForProcess, out string failReason)
        {
            argsForProcess = null;
            failReason = null;
            try
            {
                var argMap = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "-update_id", updateId ?? "" },
                    { "-what", what ?? "" },
                    { "-update_plugins", configData.UpdatePlugins ? "yes" : "no" },
                    { "-server_dir", configData.ServerDirectory ?? "" },
                    { "-steamcmd", configData.SteamCmdPath ?? "" },
                    { "-start_script", configData.ServerStartScript ?? "" },
                    { "-remote_build", string.IsNullOrEmpty(remoteBuildArgValue) ? "" : remoteBuildArgValue },
                    { "-remote_oxide", string.IsNullOrEmpty(remoteOxideArgValue) ? "" : remoteOxideArgValue },
                    { "-isService", configData.RustOnService ? "1" : "0" },
                    { "-serviceType", configData.ServiceType ?? "" },
                    { "-serviceName", configData.ServiceName ?? "" },
                    { "-showserverconsole", configData.RunServerScriptHidden ? "0" : "1" },
                    { "-servertmux", configData.ServerTmuxSession ?? "" }
                };

                foreach (var kv in argMap)
                {
                    if (!IsSafeArgValue(kv.Value))
                    {
                        failReason = $"Unsafe value for {kv.Key}";
                        return false;
                    }
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var parts = argMap.Select(kv => $"{kv.Key} \"{kv.Value ?? ""}\"");
                    argsForProcess = string.Join(" ", parts);
                    return true;
                }
                else
                {
                    var parts = argMap.Select(kv => $"{kv.Key} {QuoteForShell(kv.Value ?? "")}");
                    argsForProcess = string.Join(" ", parts);
                    return true;
                }
            }
            catch (Exception ex)
            {
                failReason = "Args build exception: " + ex.Message;
                return false;
            }
        }

        // Returns true if argument value is free of control/metacharacters and matches whitelist
        private bool IsSafeArgValue(string v)
        {
            if (v == null) return true;
            if (v.IndexOfAny(new char[] { '\0', '\r', '\n' }) >= 0) return false;
            if (v.IndexOfAny(new char[] { '"', '\'', '`', ';', '|', '&', '$', '<', '>', '(', ')', '{', '}', '[', ']', '*', '?', '~', '%' }) >= 0) return false;
            return Regex.IsMatch(v, @"^[A-Za-z0-9 _\.\-\/:\@\+\=,\\]*$");
        }

        // Returns true if file is executable on Unix (test -x)
        private bool IsExecutableUnix(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "-c " + QuoteForShell($"test -x \"{path.Replace("\"", "\\\"")}\""),
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    if (!p.WaitForExit(1000))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // Resolves symlink target using readlink -f (best effort)
        private string TryResolveSymlinkUnix(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "-c " + QuoteForShell($"readlink -f \"{path.Replace("\"", "\\\"")}\""),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var outp = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(1000)) { try { p.Kill(); } catch { } }
                    outp = outp?.Trim();
                    if (string.IsNullOrEmpty(outp)) return null;
                    return outp;
                }
            }
            catch { return null; }
        }

        // Quotes a string for shell single-quoted context
        private string QuoteForShell(string s)
        {
            return "'" + (s ?? "").Replace("'", "'\\''") + "'";
        }

        #endregion
    }
}

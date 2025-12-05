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
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using System.Security.Cryptography;

namespace Oxide.Plugins
{
    [Info("FeedMeUpdates", "frankie290651", "1.6.5")]
    [Description("Highly configurable plugin for Oxide framework to orchestrate Server/Oxide/Plugins updates.")]
    public class FeedMeUpdates : CovalencePlugin
    {
        #region Configuration

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

            public int BeforeForceWipeRange { get; set; } = 15;
            public string CustomWipeDay { get; set; } = "";
            public string CustomWipeTime { get; set; } = "";
            public string ServerIdentity { get; set; } = "";
            public string NextWipeServerName { get; set; } = "";
            public string NextWipeServerDescription { get; set; } = "";
            public string NextWipeMapUrl { get; set; } = "";
            public string NextWipeLevel { get; set; } = "";
            public string NextWipeSeed { get; set; } = "";
            public bool NextWipeRandomSeed { get; set; } = false;
            public string NextWipeMapsize { get; set; } = "";
            public bool NextWipeKeepBps { get; set; } = true;
            public bool NextWipeResetRustPlus { get; set; } = false;
            public bool NextWipeDeletePlayerData { get; set; } = false;
            public string NextWipeDeletePluginDatafiles { get; set; } = "";

            public string UpdaterMarkerFileName { get; set; } = "updateresult.json";
            public string UpdaterLockFileName { get; set; } = "updating.lock";
            public string MarkersSubfolder { get; set; } = "markers";
        }

        private ConfigData configData;

        // Writes a default configuration file when none is present or on reset.
        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new ConfigData(), true);
        }

        #endregion

        #region Constants

        private const string DataFileName = "FeedMeUpdatesData";

        #endregion

        #region Runtime State
		
		
		private bool? lastOxideCompat = null;
		private string lastOxideProto = null;
		private string lastCompatNote = null;

        private bool debugging = false;
		private bool quitBeforeExecOnDebugging = true;

        private bool enablePlugin = true;

        private bool SystemUpdating = false;

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

        private bool updaterValidAtInit = true;

        private int? minutesBeforeForceWipe = null;
        private Timer fwTimer;
        private bool nextUpdateIsForce = false;
        private int? minutesBeforeCustomWipe = null;
        private string pluginDatafilesToRemoveString = "";

        #endregion

        #region Resilient HTTP Wrapper

        private enum HttpBackend { Unknown, WebRequest, WebClient }
        private HttpBackend currentHttpBackend = HttpBackend.Unknown;
        private volatile bool httpBackendTested = false;
        private readonly object httpBackendLock = new object();

        // Detects and selects the most reliable HTTP backend available on this server.
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

        // Performs a resilient HTTP GET with timeout and fallback between backends.
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

        // Performs a resilient HTTP POST with timeout and backend fallback.
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

        // Executes an async HTTP GET using Oxide's webrequest API and returns status/body.
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

        // Executes an async HTTP POST using Oxide's webrequest API and returns status/body.
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

        // Executes an async HTTP GET using WebClient and returns status/body.
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

        // Executes an async HTTP POST using WebClient and returns status/body.
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

        #region Scheme Parsing

        private class Scheme
        {
            public bool isValid { set; get; } = false;
            public string SchemePath { get; set; } = "";
            public List<schemeInstruction> instructions { get; set; } = null;
        }

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

        // Parses a single scheme instruction row into a structured object with validation.
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

        // Reads and validates the entire scheme file, enabling scheme-driven behavior if valid.
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

        #region Discord Notifications

        // Escapes a string for safe embedding in JSON payloads.
        private string EscapeJsonString(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "")
                    .Replace("\n", "\\n");
        }

        // Sends a message to Discord via webhook, handling network/timeout errors.
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

                var prevBackend = currentHttpBackend;
                currentHttpBackend = HttpBackend.WebClient;
                try
                {
                    var result = await Task.Run(() => ResilientHttpPostSync(configData.DiscordWebhookUrl, headers, payload, configData.HttpTimeoutMs)).ConfigureAwait(false);

                    timer.Once(0f, () =>
                    {
                        if (result.statusCode >= 200 && result.statusCode < 300)
                            Puts("Discord notification sent successfully.");
                        else
                            Puts($"Error sending Discord notification: HTTP {result.statusCode} - {result.body}");
                    });
                }
                finally
                {
                    currentHttpBackend = prevBackend;
                }
            }
            catch (Exception ex)
            {
                timer.Once(0f, () => Puts("Error sending Discord notification: " + ex.Message));
            }
        }

        // Composes and sends a Discord notification at the start of an update/wipe.
        private void NotifyDiscordUpdateStart(bool updateServer, bool updateOxide, string remoteOxideVersion, bool protocolChanged)
        {
            if (configData?.DiscordNotificationsEnabled != true) return;

            var updateType = (updateServer && updateOxide) ? "Server & Oxide" : (updateServer ? "Server" : (updateOxide ? "Oxide" : "No update"));
            var protocolMsg = updateOxide ? (protocolChanged ? "Protocol NUMBER CHANGE" : "No protocol change") : "";
            var oxideMsg = updateOxide ? $"Remote Oxide version: {remoteOxideVersion ?? "unknown"}" : "";
            var msg = "";

            if (updateType == "No update")
            {
                msg = $"[FeedMeUpdates] Starting custom wipe.";
            }
            else
            {
                if (nextUpdateIsForce)
                    updateType += " (ForceWipe)";
                msg = $"[FeedMeUpdates] Starting update: {updateType}\n" +
                    (string.IsNullOrEmpty(oxideMsg) ? "" : oxideMsg + "\n") +
                    (string.IsNullOrEmpty(protocolMsg) ? "" : protocolMsg);
            }

            _ = SendDiscordNotification(msg);
        }

        // Composes and sends a Discord notification after an update/wipe ends.
        private void NotifyDiscordUpdateResult(string result, string updateId, bool pluginsUpdated, List<string> pluginsList, string failureReason, string wiped, string wipe_info)
        {
            if (configData?.DiscordNotificationsEnabled != true) return;
            var msg = "";
            if (updateId == "wipe")
            {
                msg = $"[FeedMeUpdates] wipe finished: {(string.IsNullOrEmpty(result) ? "UNKNOWN" : result.Trim().ToUpper())} (ID: {updateId ?? "unknown"})\n";
                if (!string.IsNullOrEmpty(result) && result.ToLower().Trim() == "success")
                {
                    msg += "Wipe executed SUCCESSFULLY.";
                }
                else
                {
                    msg += "Wipe FAILED.";
                    if (!string.IsNullOrEmpty(failureReason))
                    {
                        msg += $"\nFailure reason: {failureReason}";
                    }
                }
                if (wiped == "yes")
                {
                    msg += $"\nServer wiped.";
                }
                else
                {
                    msg += $"\nServer not wiped";
                }
                if (wipe_info != "")
                {
                    msg += $"\nWipe ALERTS reported:";
                    string[] wipewarnings = wipe_info.Split("]", StringSplitOptions.RemoveEmptyEntries);
                    foreach (string warning in wipewarnings)
                    {
                        var w = warning.TrimStart('[').Trim();
                        msg += $"\n - {w}";
                    }
                }
            }
            else
            {
                msg = $"[FeedMeUpdates] Update finished: {(string.IsNullOrEmpty(result) ? "UNKNOWN" : result.Trim().ToUpper())} (ID: {updateId ?? "unknown"})\n";
                if (!string.IsNullOrEmpty(result) && result.ToLower().Trim() == "success")
                {
                    msg += "Update executed SUCCESSFULLY.";
                    if (pluginsUpdated && pluginsList != null && pluginsList.Count > 0)
                    {
                        msg += $"\nUpdated plugins: {string.Join(", ", pluginsList)}";
                    }
                }
                else
                {
                    msg += "Update FAILED.";
                    if (!string.IsNullOrEmpty(failureReason))
                    {
                        msg += $"\nFailure reason: {failureReason}";
                    }
                }
                if(wiped == "yes")
                {
                    msg += $"\nServer wiped.";
                }
                else
                {
                    msg += $"\nServer not wiped";
                }
                if (wipe_info != "")
                {
                    msg += $"\nWipe ALERTS reported:";
                    string[] wipewarnings = wipe_info.Split("]", StringSplitOptions.RemoveEmptyEntries);
                    foreach (string warning in wipewarnings)
                    {
                        var w = warning.TrimStart('[').Trim();
                        msg += $"\n - {w}";
                    }
                }
            }

            _ = SendDiscordNotification(msg);
        }

        #endregion

        #region Init / Unload

        // Initializes plugin state, reads config, registers commands and schedules tasks.
        private void Init()
        {
            permission.RegisterPermission("feedme.run", this);
            try { cachedPluginVersion = Version.ToString(); } catch { cachedPluginVersion = "unknown"; }

            AddCovalenceCommand("feedme.testrun", nameof(Cmd_TestRun));
            AddCovalenceCommand("feedme.status", nameof(Cmd_Status));
            AddCovalenceCommand("feedme.version", nameof(Cmd_Version));
            AddCovalenceCommand("feedme.set", nameof(Cmd_SetConfig));

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

            AutoUpdateTask();

            pluginDatafilesToRemoveString = BuildFileListSingleArg(ParseFileListStrictJsonArray(configData.NextWipeDeletePluginDatafiles));
            if(!string.IsNullOrEmpty(configData.NextWipeDeletePluginDatafiles))
            {
                if(string.IsNullOrEmpty(pluginDatafilesToRemoveString))
                    Puts("List of plugin datafiles to delete at wipe in invalid. Plugin datafile removal disabled");
            }
            else
            {
                configData.NextWipeDeletePluginDatafiles = "";
                pluginDatafilesToRemoveString = "";
            }
            int sizeValue = 0;
            if (!string.IsNullOrEmpty(configData.NextWipeMapsize))
            {
                if (int.TryParse(configData.NextWipeMapsize, out sizeValue))
                {
                    if (sizeValue == 0 || sizeValue < 1000 || sizeValue > 6000)
                    {
                        configData.NextWipeMapsize = "";
                        Puts("Map size specified for the next wipe is out of range (accepted range 1000-6000). In case of wipe map size won't be changed.");
                    }
                }
                else
                {
                    configData.NextWipeMapsize = "";
                    Puts("Map size specified for the next is invalid. In case of wipe map size won't be changed");
                }
            }
            else
            {
                configData.NextWipeMapsize = "";
            }
            if (configData.NextWipeRandomSeed)
            {
                var rndui = new System.Random();
                var _bytes = new byte[4];
                rndui.NextBytes(_bytes);
                uint _seed = BitConverter.ToUInt32(_bytes, 0);
                configData.NextWipeSeed = _seed.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                if (!string.IsNullOrEmpty(configData.NextWipeSeed))
                {
                    if (!TryParseSeedDecimal(configData.NextWipeSeed))
                    {
                        configData.NextWipeSeed = "";
                        Puts("Seed value specified for the next is invalid. In case of wipe the seed won't be changed");
                    }
                }
                else
                {
                    configData.NextWipeSeed = "";
                }
            }


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
            fwTimer = timer.Every(60f, () => WipeTimeCheck());
        }

        // Cleans up repeating timers and scheduled tasks when the plugin unloads.
        void Unload()
        {
            if (checkTimer != null)
            {
                checkTimer?.Destroy();
                checkTimer = null;
            }
            if (DailyRestartCheckRepeater != null)
            {
                DailyRestartCheckRepeater?.Destroy();
                DailyRestartCheckRepeater = null;
            }
            if (fwTimer != null)
            {
                fwTimer?.Destroy();
                fwTimer = null;
            }
        }

        #endregion

        #region Datafile Management

        // Ensures the try number is present in the datafile or initializes it.
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

        // Reads the try number value from the datafile.
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

        // Writes the current try number back to the datafile.
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

        // Handles steambid.temp detection, promotion to datafile, and cleanup.
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

        // Loads LocalSteamBuildID from the persisted datafile storage.
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

        // Saves LocalSteamBuildID to the datafile storage for later use.
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

        // Persists a remote build ID to the datafile (to promote after success).
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

        // Removes the remote build ID entry from the datafile after promotion.
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

        // Reads the plugin's dictionary-shaped datafile into memory.
        private Dictionary<string, string> ReadDataFileDict()
        {
            try
            {
                var dict = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, string>>(DataFileName);
                return dict ?? new Dictionary<string, string>();
            }
            catch (Exception ex) { Puts("Generic datafile read error: " + ex.Message); return new Dictionary<string, string>(); }
        }

        // Writes a dictionary back into the plugin's datafile storage.
        private void WriteDataFileDict(Dictionary<string, string> dict)
        {
            try { Interface.Oxide.DataFileSystem.WriteObject(DataFileName, dict); }
            catch (Exception ex) { Puts("Generic datafile write error: " + ex.Message); }
        }

        #endregion

        #region Marker & Lock Handling

        // Processes updater result marker file, promotes build IDs, archives marker, and notifies.
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
                string wiped = ExtractJsonString(content, "wiped") ?? ExtractJsonString(content, "wipe_result");
                string wipe_info = ExtractJsonString(content, "wipe_info") ?? ExtractJsonString(content, "wipeinfo");
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

                NotifyDiscordUpdateResult(result, update_id, pluginsUpdated != null && pluginsUpdated.Count > 0, pluginsUpdated, failureReason, wiped, wipe_info);

                Puts("=== Updater marker found ===");
                Puts($"result: {(result ?? "null")}");
                Puts($"update_id: {(update_id ?? "unknown")}");
                Puts("Full payload: " + content);
                Puts("=== End marker ===");

                try
                {
                    if (!string.IsNullOrEmpty(result) && string.Equals(result.Trim(), "success", StringComparison.OrdinalIgnoreCase))
                    {
                        if(!string.IsNullOrEmpty(wiped) && string.Equals(wiped.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
                            ResetNextWipeValues();
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
                    string dt = DateTime.Now.ToString("dd_MM_yy_HH-mm-ss");
                    var markersFolder = Path.Combine(configData.ServerDirectory, configData.MarkersSubfolder ?? "markers");
                    if (!Directory.Exists(markersFolder)) Directory.CreateDirectory(markersFolder);
                    var destName = "marker_" + (string.IsNullOrEmpty(update_id) ? Guid.NewGuid().ToString("N") : update_id) + "_" + dt + ".json";
                    var destPath = Path.Combine(markersFolder, destName);
                    File.Move(markerPath, destPath);
                    Puts("Marker moved to: " + destPath);
                }
                catch (Exception ex) { Puts("Error moving marker: " + ex.Message); }
            }
            catch (Exception ex) { Puts("Error reading marker: " + ex.Message); }
        }

        // Resets wipe-related configuration values after a successful wipe.
        private void ResetNextWipeValues()
        {
            Puts("Server successfully wiped. Resetting wipe-related configs");

            configData.CustomWipeDay = "";
            configData.CustomWipeTime = "";
            configData.NextWipeServerName = "";
            configData.NextWipeServerDescription = "";
            configData.NextWipeMapUrl = "";
            configData.NextWipeLevel = "";
            configData.NextWipeSeed = "";
            configData.NextWipeRandomSeed = false;
            configData.NextWipeMapsize = "";
            configData.NextWipeKeepBps = true;
            configData.NextWipeDeletePlayerData = false;
            configData.NextWipeDeletePluginDatafiles = "";

            Config.WriteObject(configData, true);

        }

        // Extracts a JSON field (string or simple token) value by key using regex.
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

        // Detects and removes an updater lock file if present, logging a warning.
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

        #region Update Detection

        // Computes update decisions based on the scheme file (if in use).
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
            if (!string.IsNullOrEmpty(remoteBuild))
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

        // Default update detection logic combining server and Oxide version checks.
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
                    if(debugging)
						Puts("[UpdateLogics Cycle] Remote Oxide: " + remoteOxide);
					
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
                if(debugging)
				{
					string DisplayRemOx = NormalizeVersionString(remoteOxide);
					string DisplayLocOx = NormalizeVersionString(localOxideVersion);
					Puts("[UpdateLogics Cycle] Oxide changed set to: " + OxideChanged.ToString() + " (" + DisplayRemOx + " vs " + DisplayLocOx + ")");
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
                        if(debugging)
							Puts("[UpdateLogics Cycle] Remote Server: " + remoteBuild);
                        
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

                    if(debugging)
						Puts("[UpdateLogics Cycle] Server changed set to: " + (remoteBuild != LocalSteamBuildID).ToString() + " (" + remoteBuild + " vs " + LocalSteamBuildID + ")");
                    
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

        // Starts the periodic repeating timer for update detection and triggers an initial check.
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

        // Executes the asynchronous update detection flow and manages pending flags.
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

        #region Remote Helpers

        // Invokes steamcmd to gather the latest Rust Dedicated Server public build ID.
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

        // Parses the build ID from steamcmd's app_info_print output.
        private string ParseBuildId(string steamOutput)
        {
            if (string.IsNullOrEmpty(steamOutput)) return null;

            int idxBranches = steamOutput.IndexOf("\"branches\"", StringComparison.OrdinalIgnoreCase);
            if (idxBranches < 0) return null;
            int openBranches = steamOutput.IndexOf('{', idxBranches);
            if (openBranches < 0) return null;
            string branchesBlock = ExtractBalancedBlock(steamOutput, openBranches);
            if (branchesBlock == null) return null;

            int idxPublic = branchesBlock.IndexOf("\"public\"", StringComparison.OrdinalIgnoreCase);
            if (idxPublic < 0) return null;
            int openPublic = branchesBlock.IndexOf('{', idxPublic);
            if (openPublic < 0) return null;
            string publicBlock = ExtractBalancedBlock(branchesBlock, openPublic);
            if (publicBlock == null) return null;

            var m = Regex.Match(publicBlock, "\"buildid\"\\s*\"([0-9]{5,12})\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        //Helper for ParseBuildId, used to extract a block from the output
        private string ExtractBalancedBlock(string text, int openIndex)
        {
            if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != '{') return null;

            int depth = 0;
            for (int i = openIndex; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return text.Substring(openIndex, i - openIndex + 1);
                }
            }
            return null;
        }

        // Queries GitHub API for the latest Oxide.Rust release tag name.
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

        #region Local Oxide Lookup

        // Attempts to read local Oxide version by inspecting Oxide.Rust.dll file metadata.
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

        // Normalizes a version string to the form X.Y.Z by stripping suffixes.
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

        #region Compatibility Check

        //Cleaning procedure for GetOxideCompatibilityInfo
        private string CleanRef(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var forbidden = new HashSet<char> { '\u200B', '\u200C', '\u200D', '\uFEFF', '\u2060' };
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsControl(ch)) continue;
                if (forbidden.Contains(ch)) continue;
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        //Determine if a remote oxide version is compatible with local server (returns false if protocol changed or true if not)
        private bool? GetOxideCompatibilityInfo(string oxideTag, out string localProto, out string oxideProto, out string note)
        {
            localProto = localProtocol;
            oxideProto = null;
            note = null;

            if (string.IsNullOrEmpty(oxideTag)) { note = "no oxide tag"; return null; }
            if (string.IsNullOrEmpty(localProto)) { note = "local protocol unknown"; return null; }

            oxideTag = CleanRef(oxideTag);

            if (!string.IsNullOrEmpty(cachedRemoteOxide) && oxideTag == cachedRemoteOxide && lastOxideCompat.HasValue)
            {
                oxideProto = lastOxideProto;
                note = lastCompatNote;
                return lastOxideCompat;
            }

            var variants = new List<string> { oxideTag };
            if (!oxideTag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) variants.Add("v" + oxideTag);
            else variants.Add(oxideTag.Substring(1));

            var prevBackend = currentHttpBackend;
            currentHttpBackend = HttpBackend.WebClient;
            int timeout = configData.HttpTimeoutMs;

            try
            {
                string commitJson = null;
                string usedVariant = null;

                foreach (var varTag in variants)
                {
                    try
                    {
                        string commitUrl = $"https://api.github.com/repos/OxideMod/Oxide.Rust/commits/{WebUtility.UrlEncode(varTag)}";
                        var r = ResilientHttpGetSync(commitUrl, null, timeout);
                        if (debugging)
                            Puts($"[Compat] GET {commitUrl} => {r.statusCode} len={(r.body?.Length ?? 0)}");
                        if (r.statusCode == 200 && !string.IsNullOrEmpty(r.body))
                        {
                            commitJson = r.body;
                            usedVariant = varTag;
                            break;
                        }


                        var relUrl = $"https://api.github.com/repos/OxideMod/Oxide.Rust/releases/tags/{WebUtility.UrlEncode(varTag)}";
                        var relR = ResilientHttpGetSync(relUrl, null, timeout);
                        if (debugging)
                            Puts($"[Compat] GET {relUrl} => {relR.statusCode} len={(relR.body?.Length ?? 0)}");
                        if (relR.statusCode == 200 && !string.IsNullOrEmpty(relR.body))
                        {
                            var tc = Regex.Match(relR.body, "\"target_commitish\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            var commitish = tc.Success ? tc.Groups[1].Value : null;
                            if (debugging)
                                Puts($"[Compat] releases/tags target_commitish='{commitish ?? "<null>"}'");
                            if (!string.IsNullOrEmpty(commitish))
                            {
                                commitUrl = $"https://api.github.com/repos/OxideMod/Oxide.Rust/commits/{WebUtility.UrlEncode(commitish)}";
                                var commitR = ResilientHttpGetSync(commitUrl, null, timeout);
                                if (debugging)
                                    Puts($"[Compat] GET {commitUrl} => {commitR.statusCode} len={(commitR.body?.Length ?? 0)}");
                                if (commitR.statusCode == 200 && !string.IsNullOrEmpty(commitR.body))
                                {
                                    commitJson = commitR.body;
                                    usedVariant = varTag;
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception exVar)
                    {
                        if (debugging)
                            Puts($"[Compat] Exception while trying variant '{varTag}': {exVar.Message}");
                    }
                }

                if (string.IsNullOrEmpty(commitJson))
                {
                    note = "no commit info";
                    if (debugging)
                    {
                        Puts($"[UpdateLogics Cycle] Remote Oxide Comp: note=\"{note}\" (tag tried: {oxideTag})");
                    }
                    lastOxideCompat = null;
                    lastOxideProto = null;
                    lastCompatNote = note;
                    return null;
                }

                if (debugging)
                    Puts($"[Compat] commitJson snippet (len {commitJson.Length}): {commitJson.Substring(0, Math.Min(300, commitJson.Length)).Replace("\n", "\\n")}");

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

                if (string.IsNullOrEmpty(oxideProto))
                {
                    note = "oxide protocol not found";
                    if (debugging)
                    {
                        Puts($"[UpdateLogics Cycle] Remote Oxide Comp: note=\"{note}\"");
                    }
                    lastOxideCompat = null;
                    lastOxideProto = null;
                    lastCompatNote = note;
                    return null;
                }

                if (oxideProto == localProto)
                {
                    note = "protocols match";
                    if (debugging)
                    {
                        Puts($"[UpdateLogics Cycle] Remote Oxide Comp: oxideProto=\"{oxideProto}\"");
                        Puts($"[UpdateLogics Cycle] Remote Oxide Comp: note=\"{note}\"");
                    }
                    lastOxideCompat = true;
                    lastOxideProto = oxideProto;
                    lastCompatNote = note;
                    return true;
                }

                note = "protocols differ";
                if (debugging)
                {
                    Puts($"[UpdateLogics Cycle] Remote Oxide Comp: oxideProto=\"{oxideProto}\"");
                    Puts($"[UpdateLogics Cycle] Remote Oxide Comp: note=\"{note}\"");
                }
                lastOxideCompat = false;
                lastOxideProto = oxideProto;
                lastCompatNote = note;
                return false;
            }
            catch (Exception ex)
            {
                note = "exception: " + ex.Message;
                if (debugging)
                {
                    Puts("[UpdateLogics Cycle] Remote Oxide Comp error: " + note);
                }
                lastOxideCompat = null;
                lastOxideProto = null;
                lastCompatNote = note;
                return null;
            }
            finally
            {
                currentHttpBackend = prevBackend;
            }
        }
        #endregion

        #region Countdown & Updater Launch

        // Starts a player-visible countdown and schedules updater + graceful quit.
        private void BeginUpdateCountdown(string remoteBuild, string remoteOxide)
        {
            if (countdownActive)
            {
                Puts("Countdown already active; skipping new countdown.");
                return;
            }
            if(SystemUpdating)
            {
                Puts("App update is not over. Skipping cycle.");
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
                remaining--;
                if (remaining <= 0)
                {
                    string remoteBuildArg = pendingServerUpdate ? remoteBuild : null;
                    string remoteOxideArg = pendingOxideUpdate ? remoteOxide : null;

                    
					if(debugging)
					{
							Puts("[Countdown] remoteBuildArg=" + remoteBuildArg + " pendingServerUpdate=" + pendingServerUpdate.ToString() + " remoteBuild=" + remoteBuild);
							Puts("[Countdown] remoteOxideArg=" + remoteOxideArg + " pendingOxideUpdate=" + pendingOxideUpdate.ToString() + " remoteOxide=" + remoteOxide);
                    }

                    StartUpdaterExecutable(updateServer: pendingServerUpdate, updateOxide: pendingOxideUpdate, remoteBuildArg: remoteBuildArg, remoteOxideArg: remoteOxideArg);

                    Puts("Countdown end: scheduling graceful save+quit...");
                    PerformSaveAndQuitGracefully();

                    countdownActive = false;
                    return;
                }
                if(!nextUpdateIsForce)
                    BroadcastToPlayers($"ATTENTION: server will restart for update in {remaining} minute(s).");
                else
                    BroadcastToPlayers($"ATTENTION: server will restart for monthly force wipe in {remaining} minute(s).");

                
				if(debugging)
				{
					Puts("[Countdown] pendingServerUpdate=" + pendingServerUpdate.ToString() + " remoteBuild=" + remoteBuild);
					Puts("[Countdown] pendingOxideUpdate=" + pendingOxideUpdate.ToString() + " remoteOxide=" + remoteOxide);
                }
                timer.Once(60f, tick);
            };

            timer.Once(60f, tick);
        }

        // Validates and launches the external updater executable with encoded arguments.
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

                var exePath = configData.UpdaterExecutablePath;

                bool protocolChanged = false;
                string remoteOxideVersion = remoteOxideArg;
				
                if(debugging)
				{
					string DumpHex(string s) => s == null ? "<null>" : string.Join(" ", s.Select(c => ((int)c).ToString("X2")));
					Puts($"[StartExe] remoteOxideArg len={remoteOxideArg?.Length ?? 0} hex=[{DumpHex(remoteOxideArg)}]");
					Puts($"[StartExe] encoded={System.Web.HttpUtility.UrlEncode(remoteOxideArg ?? "")}");
				}
                if (updateOxide && !string.IsNullOrEmpty(remoteOxideVersion))
                {
                    string _localProto, _oxideProto, _note;
                    var compat = GetOxideCompatibilityInfo(remoteOxideVersion, out _localProto, out _oxideProto, out _note);
                    protocolChanged = compat.HasValue && compat.Value == false;
                }

                
                if (debugging)
                {
                    Puts("[StartExe] protocolChanged=" + protocolChanged.ToString() + " remoteOxideVersion=" + remoteOxideVersion + " remoteOxideArg=" + remoteOxideArg);
					if(quitBeforeExecOnDebugging)
					{
						PerformSaveAndQuitGracefully();
						return;
					}
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

        // Enables/disables periodic checks and shows current status to caller.
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

        // Displays plugin and environment versions (Rust/Build/Protocol/Oxide).
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

        // Forces an immediate update test run (server + oxide) and restarts.
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

        void Cmd_SetConfig(IPlayer player, string command, string[] args)
        {
            if (!HasPermissionOrConsole(player)) return;

            if (args == null || args.Length < 2)
            {
                player?.Message("Usage: feedme.set <option> <value>");
                player?.Message("Example: feedme.set startupscan true");
                return;
            }

            var key = args[0].ToLowerInvariant();
            var value = string.Join(" ", args.Skip(1)); // allows values with spaces

            bool boolVal;
            int intVal;
            string changed = null;

            try
            {
                switch (key)
                {
                    // --- GENERAL / PATHS / SERVICE ---

                    case "serverdirectory":
                        configData.ServerDirectory = value;
                        changed = $"ServerDirectory = {value}";
                        break;

                    case "steamcmdpath":
                        configData.SteamCmdPath = value;
                        changed = $"SteamCmdPath = {value}";
                        break;

                    case "updaterexecutablepath":
                        configData.UpdaterExecutablePath = value;
                        changed = $"UpdaterExecutablePath = {value}";
                        break;

                    case "showupdaterconsole":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.ShowUpdaterConsole = boolVal;
                        changed = $"ShowUpdaterConsole = {boolVal}";
                        break;

                    case "serverstartscript":
                        configData.ServerStartScript = value;
                        changed = $"ServerStartScript = {value}";
                        break;

                    case "runserverscripthidden":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.RunServerScriptHidden = boolVal;
                        changed = $"RunServerScriptHidden = {boolVal}";
                        break;

                    case "servertmuxsession":
                        configData.ServerTmuxSession = value;
                        changed = $"ServerTmuxSession = {value}";
                        break;

                    case "rustonservice":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.RustOnService = boolVal;
                        changed = $"RustOnService = {boolVal}";
                        break;

                    case "servicename":
                        configData.ServiceName = value;
                        changed = $"ServiceName = {value}";
                        break;

                    case "servicetype":
                        configData.ServiceType = value;
                        changed = $"ServiceType = {value}";
                        break;

                    // --- TIMEOUTS & INTERVALS ---

                    case "httptimeoutms":
                        if (!int.TryParse(value, out intVal)) goto invalid_int;
                        configData.HttpTimeoutMs = intVal;
                        changed = $"HttpTimeoutMs = {intVal}";
                        break;

                    case "maxattempts":
                        if (!int.TryParse(value, out intVal)) goto invalid_int;
                        configData.MaxAttempts = intVal;
                        changed = $"MaxAttempts = {intVal}";
                        break;

                    case "checkintervalminutes":
                        if (!int.TryParse(value, out intVal)) goto invalid_int;
                        configData.CheckIntervalMinutes = intVal;
                        changed = $"CheckIntervalMinutes = {intVal}";
                        break;

                    case "countdownminutes":
                        if (!int.TryParse(value, out intVal)) goto invalid_int;
                        configData.CountdownMinutes = intVal;
                        changed = $"CountdownMinutes = {intVal}";
                        break;

                    // --- UPDATE BEHAVIOR ---

                    case "startupscan":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.StartupScan = boolVal;
                        changed = $"StartupScan = {boolVal}";
                        break;

                    case "updateplugins":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.UpdatePlugins = boolVal;
                        changed = $"UpdatePlugins = {boolVal}";
                        break;

                    case "onlyserverprotocolupdate":
                    case "protocolonly":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.OnlyServerProtocolUpdate = boolVal;
                        changed = $"OnlyServerProtocolUpdate = {boolVal}";
                        break;

                    case "usescheme":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.UseScheme = boolVal;
                        changed = $"UseScheme = {boolVal}";
                        break;

                    case "schemefile":
                        configData.SchemeFile = value;
                        changed = $"SchemeFile = {value}";
                        break;

                    // --- DAILY RESTART ---

                    case "dailyrestarttime":
                        configData.DailyRestartTime = value;
                        changed = $"DailyRestartTime = {value}";
                        break;

                    case "minutesbeforerestart":
                        if (!int.TryParse(value, out intVal)) goto invalid_int;
                        configData.MinutesBeforeRestart = intVal;
                        changed = $"MinutesBeforeRestart = {intVal}";
                        break;

                    // --- DISCORD ---

                    case "discordnotificationsenabled":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.DiscordNotificationsEnabled = boolVal;
                        changed = $"DiscordNotificationsEnabled = {boolVal}";
                        break;

                    case "discordwebhookurl":
                        configData.DiscordWebhookUrl = value;
                        changed = $"DiscordWebhookUrl = {value}";
                        break;

                    // --- FORCE WIPE / CUSTOM WIPE ---

                    case "beforeforcewiperange":
                        if (!int.TryParse(value, out intVal)) goto invalid_int;
                        configData.BeforeForceWipeRange = intVal;
                        changed = $"BeforeForceWipeRange = {intVal}";
                        break;

                    case "serveridentity":
                        configData.ServerIdentity = value;
                        changed = $"ServerIdentity = {value}";
                        break;

                    // --- NEXT WIPE: SCHEDULE & MAP / BRANDING ---

                    case "customwipeday":
                        configData.CustomWipeDay = value;
                        changed = $"CustomWipeDay = {value}";
                        break;

                    case "customwipetime":
                        configData.CustomWipeTime = value;
                        changed = $"CustomWipeTime = {value}";
                        break;

                    case "nextwipeservername":
                        configData.NextWipeServerName = value;
                        changed = $"NextWipeServerName = {value}";
                        break;

                    case "nextwipeserverdescription":
                        configData.NextWipeServerDescription = value;
                        changed = $"NextWipeServerDescription = {value}";
                        break;

                    case "nextwipemapurl":
                        configData.NextWipeMapUrl = value;
                        changed = $"NextWipeMapUrl = {value}";
                        break;

                    case "nextwipelevel":
                        configData.NextWipeLevel = value;
                        changed = $"NextWipeLevel = {value}";
                        break;

                    case "nextwipeseed":
                        configData.NextWipeSeed = value;
                        changed = $"NextWipeSeed = {value}";
                        break;

                    case "nextwiperandomseed":
                    case "randomseed":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.NextWipeRandomSeed = boolVal;
                        changed = $"NextWipeRandomSeed = {boolVal}";
                        break;

                    case "nextwipemapsize":
                        configData.NextWipeMapsize = value;
                        changed = $"NextWipeMapsize = {value}";
                        break;

                    case "nextwipekeepbps":
                    case "keepbps":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.NextWipeKeepBps = boolVal;
                        changed = $"NextWipeKeepBps = {boolVal}";
                        break;

                    case "nextwiperesetrustplus":
                    case "resetrustplus":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.NextWipeResetRustPlus = boolVal;
                        changed = $"NextWipeResetRustPlus = {boolVal}";
                        break;

                    case "nextwipedeleteplayerdata":
                    case "deleteplayerdata":
                        if (!TryParseBoolFlexible(value, out boolVal)) goto invalid_bool;
                        configData.NextWipeDeletePlayerData = boolVal;
                        changed = $"NextWipeDeletePlayerData = {boolVal}";
                        break;

                    case "nextwipedeleteplugindatafiles":
                        configData.NextWipeDeletePluginDatafiles = value;
                        changed = $"NextWipeDeletePluginDatafiles = {value}";
                        break;

                    // --- INTERNAL MARKERS ---

                    case "updatermarkerfilename":
                        configData.UpdaterMarkerFileName = value;
                        changed = $"UpdaterMarkerFileName = {value}";
                        break;

                    case "updaterlockfilename":
                        configData.UpdaterLockFileName = value;
                        changed = $"UpdaterLockFileName = {value}";
                        break;

                    case "markerssubfolder":
                        configData.MarkersSubfolder = value;
                        changed = $"MarkersSubfolder = {value}";
                        break;

                    default:
                        player?.Message($"Unknown config option: {key}");
                        return;
                }

                Config.WriteObject(configData, true);
                var who = player == null ? "Console" : player.Name;
                Puts($"[Config] {who} changed: {changed}");
                player?.Message($"FeedMeUpdates config updated: {changed}");
                return;

            invalid_bool:
                player?.Message("Value must be a boolean (true/false, on/off, yes/no, 1/0).");
                return;

            invalid_int:
                player?.Message("Value must be an integer.");
                return;
            }
            catch (Exception ex)
            {
                player?.Message("Error while changing config: " + ex.Message);
            }
        }


        #endregion

        #region Utilities

        private Dictionary<string, object> GetGuiConfigSnapshot()
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                // GENERAL / PATHS / SERVICE
                ["ServerDirectory"] = configData.ServerDirectory,
                ["SteamCmdPath"] = configData.SteamCmdPath,
                ["UpdaterExecutablePath"] = configData.UpdaterExecutablePath,
                ["ShowUpdaterConsole"] = configData.ShowUpdaterConsole,
                ["ServerStartScript"] = configData.ServerStartScript,
                ["RunServerScriptHidden"] = configData.RunServerScriptHidden,
                ["ServerTmuxSession"] = configData.ServerTmuxSession,
                ["RustOnService"] = configData.RustOnService,
                ["ServiceName"] = configData.ServiceName,
                ["ServiceType"] = configData.ServiceType,

                // TIMEOUTS / INTERVALS
                ["HttpTimeoutMs"] = configData.HttpTimeoutMs,
                ["MaxAttempts"] = configData.MaxAttempts,
                ["CheckIntervalMinutes"] = configData.CheckIntervalMinutes,
                ["CountdownMinutes"] = configData.CountdownMinutes,

                // UPDATE BEHAVIOR
                ["StartupScan"] = configData.StartupScan,
                ["UpdatePlugins"] = configData.UpdatePlugins,
                ["OnlyServerProtocolUpdate"] = configData.OnlyServerProtocolUpdate,
                ["UseScheme"] = configData.UseScheme,
                ["SchemeFile"] = configData.SchemeFile,

                // DAILY RESTART
                ["DailyRestartTime"] = configData.DailyRestartTime,
                ["MinutesBeforeRestart"] = configData.MinutesBeforeRestart,

                // DISCORD
                ["DiscordNotificationsEnabled"] = configData.DiscordNotificationsEnabled,
                ["DiscordWebhookUrl"] = configData.DiscordWebhookUrl,

                // FORCE WIPE / CUSTOM WIPE
                ["BeforeForceWipeRange"] = configData.BeforeForceWipeRange,

                // NEXT WIPE: SCHEDULE & MAP / BRANDING
                ["ServerIdentity"] = configData.ServerIdentity,
                ["CustomWipeDay"] = configData.CustomWipeDay,
                ["CustomWipeTime"] = configData.CustomWipeTime,
                ["NextWipeServerName"] = configData.NextWipeServerName,
                ["NextWipeServerDescription"] = configData.NextWipeServerDescription,
                ["NextWipeMapUrl"] = configData.NextWipeMapUrl,
                ["NextWipeLevel"] = configData.NextWipeLevel,
                ["NextWipeSeed"] = configData.NextWipeSeed,
                ["NextWipeRandomSeed"] = configData.NextWipeRandomSeed,
                ["NextWipeMapsize"] = configData.NextWipeMapsize,
                ["NextWipeKeepBps"] = configData.NextWipeKeepBps,
                ["NextWipeResetRustPlus"] = configData.NextWipeResetRustPlus,
                ["NextWipeDeletePlayerData"] = configData.NextWipeDeletePlayerData,
                ["NextWipeDeletePluginDatafiles"] = configData.NextWipeDeletePluginDatafiles,

                // MARKERS
                ["UpdaterMarkerFileName"] = configData.UpdaterMarkerFileName,
                ["UpdaterLockFileName"] = configData.UpdaterLockFileName,
                ["MarkersSubfolder"] = configData.MarkersSubfolder
            };

            return d;
        }

        bool TryParseBoolFlexible(string value, out bool result)
        {
            result = false;
            if (bool.TryParse(value, out result))
                return true;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim().ToLowerInvariant();

            switch (value)
            {
                case "1":
                case "yes":
                case "y":
                case "on":
                case "enable":
                case "enabled":
                    result = true;
                    return true;

                case "0":
                case "no":
                case "n":
                case "off":
                case "disable":
                case "disabled":
                    result = false;
                    return true;
            }

            return false;
        }


        // Attempts to set +x permission on a file under Linux and confirms executability.
        private bool TrySetExecutableUnix(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
            if (!File.Exists(path)) return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "-c " + QuoteForShell($"chmod +x \"{path.Replace("\"", "\\\"")}\""),
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit(2000);
                }

                var psiCheck = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "-c " + QuoteForShell($"test -x \"{path.Replace("\"", "\\\"")}\" && echo OK || echo FAIL"),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
                using (var p2 = Process.Start(psiCheck))
                {
                    string outp = p2?.StandardOutput.ReadToEnd()?.Trim();
                    p2?.WaitForExit(1000);
                    return outp == "OK";
                }
            }
            catch (Exception ex)
            {
                Puts("TrySetExecutableUnix error: " + ex.Message);
                return false;
            }
        }

        // Detects a self-update package for the updater app and applies it safely.
        private void AutoUpdateTask()
        {
            string newExePath = configData.ServerDirectory;
            string newExeShaPath = configData.ServerDirectory;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                newExePath += @"\FeedMeUpdates.exe.new";
                newExeShaPath += @"\FMU_CHECKSUMS.txt";
            }
            else
            {
                newExePath += "/FeedMeUpdates.new";
                newExeShaPath += "/FMU_CHECKSUMS.txt";
            }
            Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(configData.ServerDirectory) && File.Exists(newExePath))
                    {
                        timer.Once(0f, () =>
                        {
                            Puts("App update process started.");
                            SystemUpdating = true;
                        });
                        string _error = "";
                        bool result = AutoUpdateProcess(newExePath, newExeShaPath, out _error);

                        timer.Once(0f, () =>
                        {
                            if (result)
                            {
                                Puts("FeedMeUpdates app updated and temporary files removed.");
                                SystemUpdating = false;
                            }
                            else
                            {
                                Puts(_error);
                                SystemUpdating = false;
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    timer.Once(0f, () => Puts("Error in autoupdate process: " + ex.Message));
                    enablePlugin = false;
                    SystemUpdating = false;
                    Puts("Plugin has been automatically disabled. Please consider reinstalling the executable app.");
                }
            });
        }

        // Verifies checksum, promotes the new updater binary, and cleans up temp files.
        private bool AutoUpdateProcess(string newExePath, string newExeShaPath, out string _error)
        {
            _error = "";

            try
            {
                if (!File.Exists(newExePath))
                {
                    _error = $"New executable not found at: {newExePath}";
                    return false;
                }
                if (!File.Exists(newExeShaPath))
                {
                    try { File.Delete(newExePath); } catch { }
                    _error = newExePath + " found but no sha file found. New file deleted and app update aborted.";
                    return false;
                }

                byte[] shaFileBytes = null;
                string shaText = null;
                try
                {
                    shaFileBytes = File.ReadAllBytes(newExeShaPath);
                    shaText = Encoding.ASCII.GetString(shaFileBytes);
                }
                catch (Exception exRead)
                {
                    try { File.Delete(newExePath); } catch { }
                    try { File.Delete(newExeShaPath); } catch { }
                    _error = $"Error reading checksum file: {exRead.Message}";
                    return false;
                }

                string cleaned = (shaText ?? "").Trim()
                                                 .Replace(" ", "")
                                                 .Replace("-", "")
                                                 .Replace(":", "");

                Puts($"[FMU DEBUG] checksum file bytes={shaFileBytes?.Length ?? 0} textLen={(shaText ?? "").Length} cleanedLen={cleaned.Length}");

                if (!TryParseHexOrBase64(cleaned, out byte[] expectedHash) || expectedHash == null || expectedHash.Length != 32)
                {
                    try
                    {
                        using (var s = File.Open(newExePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                        {
                            var comp = sha.ComputeHash(s);
                            var compHex = BitConverter.ToString(comp).Replace("-", "");
                            Puts($"[FMU DEBUG] computed(from .new): {compHex}");
                        }
                    }
                    catch { }

                    try { File.Delete(newExePath); } catch { }
                    try { File.Delete(newExeShaPath); } catch { }
                    _error = newExePath + " found but no sha data found inside " + newExeShaPath + ". New and sha files deleted and app update aborted.";
                    return false;
                }

                byte[] computedHash;
                try
                {
                    using (var stream = File.Open(newExePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    {
                        computedHash = sha.ComputeHash(stream);
                    }
                }
                catch (Exception exHash)
                {
                    try { File.Delete(newExePath); } catch { }
                    try { File.Delete(newExeShaPath); } catch { }
                    _error = "Error computing SHA256 of .new file: " + exHash.Message;
                    return false;
                }

                string expectedHex = BitConverter.ToString(expectedHash).Replace("-", "");
                string computedHex = BitConverter.ToString(computedHash).Replace("-", "");
                Puts($"[FMU DEBUG] expected (from FMU_CHECKSUMS.txt): {expectedHex}");
                Puts($"[FMU DEBUG] computed (from .new):             {computedHex}");

                bool matches = (computedHash.Length == expectedHash.Length);
                if (matches)
                {
                    int diff = 0;
                    for (int i = 0; i < computedHash.Length; i++)
                        diff |= computedHash[i] ^ expectedHash[i];
                    matches = (diff == 0);
                }

                if (!matches)
                {
                    Puts($"[FMU ERROR] SHA mismatch: expected {expectedHex} but computed {computedHex}. Deleting .new and checksum.");
                    try { File.Delete(newExePath); } catch { }
                    try { File.Delete(newExeShaPath); } catch { }
                    _error = "Sha not matching. Aborting app update.";
                    return false;
                }

                try
                {
                    try { File.Delete(configData.UpdaterExecutablePath); } catch { }
                    File.Move(newExePath, configData.UpdaterExecutablePath);

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        if (!TrySetExecutableUnix(configData.UpdaterExecutablePath))
                        {
                            throw new Exception("WARNING: chmod +x failed on Linux target.");
                        }
                    }

                    try { File.Delete(newExeShaPath); } catch { }

                    return true;
                }
                catch (Exception exMove)
                {
                    try { if (File.Exists(newExePath)) File.Delete(newExePath); } catch { }
                    try { if (File.Exists(newExeShaPath)) File.Delete(newExeShaPath); } catch { }
                    _error = "Error promoting new executable: " + exMove.Message;
                    return false;
                }
            }
            catch (Exception ex)
            {
                try { if (File.Exists(newExePath)) File.Delete(newExePath); } catch { }
                try { if (File.Exists(newExeShaPath)) File.Delete(newExeShaPath); } catch { }
                _error = "An error occurred while comparing sha256. " + ex.Message + " " + newExePath + " and " + newExeShaPath + " files deleted and app update aborted.";
                return false;
            }
        }

        // Parses a 32-byte SHA256 from hex or base64 strings; returns bytes on success.
        private bool TryParseHexOrBase64(string input, out byte[] result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            string cleaned = input.Trim()
                                  .Replace(" ", "")
                                  .Replace("-", "")
                                  .Replace(":", "");

            bool IsHexChar(char c)
            {
                return (c >= '0' && c <= '9') ||
                       (c >= 'a' && c <= 'f') ||
                       (c >= 'A' && c <= 'F');
            }

            if (cleaned.Length % 2 == 0)
            {
                bool allHex = true;
                for (int i = 0; i < cleaned.Length; i++)
                {
                    if (!IsHexChar(cleaned[i]))
                    {
                        allHex = false;
                        break;
                    }
                }

                if (allHex)
                {
                    try
                    {
                        int len = cleaned.Length / 2;
                        var bytes = new byte[len];
                        for (int i = 0; i < len; i++)
                        {
                            string pair = cleaned.Substring(i * 2, 2);
                            bytes[i] = Convert.ToByte(pair, 16);
                        }

                        if (bytes.Length == 32)
                        {
                            result = bytes;
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            string b64 = cleaned.Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
            }

            try
            {
                var bytes = Convert.FromBase64String(b64);
                if (bytes.Length == 32)
                {
                    result = bytes;
                    return true;
                }
            }
            catch
            {
            }

            result = null;
            return false;
        }

        // Compares a file's SHA-256 to an expected hash using constant-time comparison.
        public static bool FileSha256Matches(string filePath, byte[] expectedHash)
        {
            if (expectedHash == null) return false;

            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (var sha = SHA256.Create())
                {
                    byte[] computedHash = sha.ComputeHash(stream);

                    if (computedHash == null || computedHash.Length != expectedHash.Length)
                        return false;

                    return CryptographicOperations.FixedTimeEquals(computedHash, expectedHash);
                }
            }
        }

        // Validates Rust map seed string as a decimal within uint range.
        private static bool TryParseSeedDecimal(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            raw = raw.Trim();

            if (!System.Text.RegularExpressions.Regex.IsMatch(raw, @"^[0-9]+$"))
                return false;

            if (raw.Length > 10)
                return false;

            if (!ulong.TryParse(raw, out var big))
                return false;

            if (big > uint.MaxValue)
                return false;

            return true;
        }

        // Strictly parses a JSON array of strings and returns a list; returns empty on invalid.
        private List<string> ParseFileListStrictJsonArray(string input)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(input)) return result;

            int i = 0;
            int n = input.Length;

            void SkipWs()
            {
                while (i < n && char.IsWhiteSpace(input[i])) i++;
            }

            SkipWs();
            if (i >= n || input[i] != '[') return result;
            i++;

            for (; ; )
            {
                SkipWs();
                if (i < n && input[i] == ']')
                {
                    i++;
                    break;
                }

                if (i >= n || input[i] != '"') return new List<string>();
                i++;

                var sb = new StringBuilder();
                while (i < n)
                {
                    char c = input[i++];
                    if (c == '"') break;

                    if (c == '\\')
                    {
                        if (i >= n) return new List<string>();
                        char e = input[i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 > n) return new List<string>();
                                string hex = input.Substring(i, 4);
                                if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                                    return new List<string>();
                                sb.Append((char)code);
                                i += 4;
                                break;
                            default:
                                return new List<string>();
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                result.Add(sb.ToString());

                SkipWs();
                if (i < n && input[i] == ',')
                {
                    i++;
                    continue;
                }
                else if (i < n && input[i] == ']')
                {
                    i++;
                    break;
                }
                else
                {
                    return new List<string>();
                }
            }

            SkipWs();
            if (i != n)
            {
                return new List<string>();
            }

            return result;
        }

        // Validates DailyRestartTime and prepares internal target hour/minute variables.
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

        // Periodically checks proximity to daily restart and disables update checks if close.
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

        // Tries to parse "HH:mm" or "H:mm" into hour and minute components.
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

        // Checks if a tmux session exists by name by calling tmux CLI.
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

        // Detects whether gnome-terminal and/or tmux are available on Linux.
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

        // Checks if caller is console/admin or has the required permission.
        private bool HasPermissionOrConsole(IPlayer player)
        {
            if (player == null) return true;
            if (player.IsServer) return true;
            if (player.IsAdmin) return true;
            if (permission.UserHasPermission(player.Id, "feedme.run")) return true;
            player.Message("You do not have permission to run this command.");
            return false;
        }

        // Sends a chat message to all connected players and logs it to console.
        private void BroadcastToPlayers(string message)
        {
            try
            {
                foreach (var pl in players.Connected) pl.Message(message);
                Puts($"Broadcast: {message}");
            }
            catch (Exception ex) { Puts("Broadcast error: " + ex.Message); }
        }

        // Sends a chat message only to admins/console-permitted users and logs it.
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

        // Performs a minimal save/quit sequence to allow the external updater to proceed.
        private void PerformSaveAndQuitGracefully()
        {
            try
            {
                RunSequence(
                    (0f, () =>
                    {
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

        // Runs a sequence of delayed actions in order using the Oxide timer.
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

        // Entry point after server initializes; may run startup scan or immediate init update.
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

            if (!string.IsNullOrEmpty(LocalSteamBuildID) && configData != null && configData.StartupScan && !SystemUpdating)
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

                remoteBuild = cachedRemoteBuild;
                remoteOxide = cachedRemoteOxide;

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
            else
            {
                if(SystemUpdating)
                {
                    Puts("App update is not over. Skipping startup scan.");
                }
            }

            SetWipeUnixTimestampOverrideFromConfig();

            if (checkTimer == null) StartPeriodicCheck();
        }

        // Hook for server save; currently not used by the plugin.
        void OnServerSave() { }

        #endregion

        #region Validation & Argument Building

        // Adds base64 encoding for selected arguments to avoid shell/CLI issues.
        private string EncodeIfSpecialArg(string key, string rawValue)
        {
            if (string.IsNullOrEmpty(key)) return rawValue;
            var base64Keys = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "-nextwipeurl",
                "-servername",
                "-serverdescription"
            };

            if (!base64Keys.Contains(key))
                return rawValue;

            if (rawValue == null)
                return "b64:";

            var bytes = System.Text.Encoding.UTF8.GetBytes(rawValue);
            var b64 = System.Convert.ToBase64String(bytes);
            return "b64:" + b64;
        }

        // Validates updater path at init (exists, file, size>0, and +x on Linux).
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

        // Builds and validates a safe argument string for launching the updater process.
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
                    { "-servertmux", configData.ServerTmuxSession ?? "" },
                    { "-isforce", nextUpdateIsForce ? "1" : "0" },
                    { "-nextwipelevel", configData.NextWipeLevel ?? "" },
                    { "-nextwipeurl", configData.NextWipeMapUrl ?? "" },
                    { "-nextwipeseed", configData.NextWipeSeed ?? "" },
                    { "-nextwipemapsize", configData.NextWipeMapsize ?? "" },
                    { "-nextwipekeepbps", configData.NextWipeKeepBps ? "1" : "0" },
                    { "-nextwiperesetrustplus", configData.NextWipeResetRustPlus ? "1" : "0" },
                    { "-nextwipedelplayerdata", configData.NextWipeDeletePlayerData ? "1" : "0" },
                    { "-nextwipedelpluginsdata", pluginDatafilesToRemoveString ?? "" },
                    { "-serveridentity", configData.ServerIdentity ?? "" },
                    { "-servername", configData.NextWipeServerName ?? "" },
                    { "-serverdescription", configData.NextWipeServerDescription ?? "" }
                };

                var keys = new System.Collections.Generic.List<string>(argMap.Keys);
                int counter = 0;
                string unconvertedK = "";
                foreach (var k in keys)
                {
                    unconvertedK = k;
                    argMap[k] = EncodeIfSpecialArg(k, argMap[k]);
                    int desclen = (k.Length + argMap[k].Length);
                    counter += desclen;
                    if(counter > 32500 && unconvertedK == "-serverdescription")
                    {
                        Puts("WARINING: ServerDescription too long, skipping it to avoid command line length limit. Please change it manually after wipe!");
                        argMap.Remove(k);
                        argMap[unconvertedK] = "";
                        counter = counter - desclen + argMap[unconvertedK].Length + unconvertedK.Length;
                        if(counter > 32500)
                        {
                            failReason = "Params too long";
                            return false;
                        }
                    }
                }

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

        // Checks an argument value for unsafe characters; allows only a strict whitelist.
        private bool IsSafeArgValue(string v)
        {
            if (v == null) return true;
            if (v.IndexOfAny(new char[] { '\0', '\r', '\n' }) >= 0) return false;
            if (v.IndexOfAny(new char[] { '"', '\'', '`', ';', '|', '&', '$', '<', '>', '(', ')', '{', '}', '[', ']', '*', '?', '~', '%' }) >= 0) return false;
            return Regex.IsMatch(v, @"^[A-Za-z0-9 _\.\-\/:\@\+\=,\\]*$");
        }

        // Verifies a file is executable on Unix using 'test -x' shell command.
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

        // Attempts to resolve a symlink target path using readlink -f on Unix.
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

        // Quotes a string so it can be safely embedded in single-quoted shell contexts.
        private string QuoteForShell(string s)
        {
            return "'" + (s ?? "").Replace("'", "'\\''") + "'";
        }

        #endregion

        #region Wipe Helpers

        //compute unix timestamp is UTC and apply it to wipeUnixTimestampOverride, then calls server.writecfg and server.readcfg.
        private void SetWipeUnixTimestampOverrideFromConfig()
        {
            try
            {
                var dayRaw = configData?.CustomWipeDay;
                var timeRaw = configData?.CustomWipeTime;

                if (string.IsNullOrWhiteSpace(dayRaw) || string.IsNullOrWhiteSpace(timeRaw))
                {
                    Puts("SetWipeUnixTimestampOverrideStrict: CustomWipeDay or CustomWipeTime missing/empty; aborted.");
                    return;
                }

                string[] dateFormats = { "d/M/yyyy", "dd/MM/yyyy", "d/MM/yyyy", "dd/M/yyyy" };
                dayRaw = dayRaw.Trim().Replace('-', '/');

                if (!DateTime.TryParseExact(dayRaw, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime datePart))
                {
                    Puts($"SetWipeUnixTimestampOverrideStrict: CustomWipeDay '{configData.CustomWipeDay}' does not match required formats.");
                    return;
                }

                string[] timeFormats = { "H:mm", "HH:mm" };
                if (!DateTime.TryParseExact(timeRaw.Trim(), timeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime timePart))
                {
                    Puts($"SetWipeUnixTimestampOverrideStrict: CustomWipeTime '{configData.CustomWipeTime}' does not match required formats.");
                    return;
                }

                var targetLocalUnspecified = new DateTime(
                    datePart.Year, datePart.Month, datePart.Day,
                    timePart.Hour, timePart.Minute, 0,
                    DateTimeKind.Unspecified
                );

                DateTime targetUtc;
                try
                {
                    targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetLocalUnspecified, TimeZoneInfo.Local);
                }
                catch (Exception convEx)
                {
                    Puts($"SetWipeUnixTimestampOverrideStrict: UTC conversion error: {convEx.Message}. Aborted.");
                    return;
                }

                long unixTs = (long)(targetUtc - DateTime.UnixEpoch).TotalSeconds;
                if (unixTs <= 0)
                {
                    Puts($"SetWipeUnixTimestampOverrideStrict: computed timestamp '{unixTs}' is invalid; aborted.");
                    return;
                }

                Puts($"SetWipeUnixTimestampOverrideStrict: setting wipeUnixTimestampOverride={unixTs} (UTC={targetUtc:yyyy-MM-dd HH:mm:ss}).");
                ConsoleSystem.Run(ConsoleSystem.Option.Server, $"wipeUnixTimestampOverride {unixTs}");
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.writecfg");
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.readcfg");
            }
            catch (Exception ex)
            {
                Puts("SetWipeUnixTimestampOverrideStrict: unexpected error: " + ex.Message);
            }
        }

        // Calculates minutes until the official monthly force wipe (first Thursday 19:00 UTC).
        private (int minutesRemaining, DateTime nextWipeUtc, DateTime nextWipeInServerTz) MinutesUntilMonthlyForceWipe(TimeZoneInfo tz = null)
        {
            if (tz == null) tz = TimeZoneInfo.Local;

            DateTime utcNow = DateTime.UtcNow;

            int FirstThursdayDay(int year, int month)
            {
                var firstOfMonth = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
                int daysToThursday = ((int)DayOfWeek.Thursday - (int)firstOfMonth.DayOfWeek + 7) % 7;
                return 1 + daysToThursday;
            }

            DateTime BuildCandidateUtc(int year, int month)
            {
                int day = FirstThursdayDay(year, month);
                return new DateTime(year, month, day, 19, 0, 0, DateTimeKind.Utc);
            }

            int year = utcNow.Year;
            int month = utcNow.Month;

            DateTime candidateUtc = BuildCandidateUtc(year, month);

            if (candidateUtc <= utcNow)
            {
                month++;
                if (month > 12) { month = 1; year++; }
                candidateUtc = BuildCandidateUtc(year, month);
            }

            double minutesDouble = (candidateUtc - utcNow).TotalMinutes;
            int minutesRemaining = (int)Math.Ceiling(Math.Max(0.0, minutesDouble));

            DateTime wipeInServerTz;
            try
            {
                wipeInServerTz = TimeZoneInfo.ConvertTimeFromUtc(candidateUtc, tz);
            }
            catch
            {
                wipeInServerTz = TimeZoneInfo.ConvertTimeFromUtc(candidateUtc, TimeZoneInfo.Local);
            }

            return (minutesRemaining, candidateUtc, wipeInServerTz);
        }

        // Periodic wipe-time checks; triggers force/custom wipe workflows near target time.
        private void WipeTimeCheck()
        {
            if (minutesBeforeForceWipe == null)
                minutesBeforeForceWipe = MinutesUntilMonthlyForceWipe().minutesRemaining;
            else
                minutesBeforeForceWipe--;

            if (minutesBeforeCustomWipe == null)
                minutesBeforeCustomWipe = MinutesUntilCustomWipe(configData.CustomWipeDay, configData.CustomWipeTime);
            else
                minutesBeforeCustomWipe--;

            if (minutesBeforeForceWipe != null)
            {
                if (minutesBeforeForceWipe.Value == 0)
                {
                    nextUpdateIsForce = true;
                    fwTimer.Destroy();
                    fwTimer = null;
                    return;
                }
                else
                {
                    if (configData.BeforeForceWipeRange <= 0)
                        configData.BeforeForceWipeRange = 15;
                    if (minutesBeforeForceWipe.Value <= configData.BeforeForceWipeRange)
                    {
                        nextUpdateIsForce = true;
                        fwTimer.Destroy();
                        fwTimer = null;
                        return;
                    }
                }
            }
            if(minutesBeforeCustomWipe != null)
            {
                if(minutesBeforeCustomWipe.Value <= 0)
                {
                    BroadcastToPlayers($"ATTENTION: server is wiping.");
                    fwTimer.Destroy();
                    fwTimer = null;
                    StartUpdaterExecutable(updateServer: false, updateOxide: false, overrideUpdateId: "wipe", overrideWhat: "none", remoteBuildArg: "no", remoteOxideArg: "no");

                    Puts("Custom wipe countdown end: scheduling graceful save+quit...");
                    PerformSaveAndQuitGracefully();
                }
                else
                {
                    if(minutesBeforeCustomWipe.Value <= configData.CountdownMinutes)
                    {
                        BroadcastToPlayers($"ATTENTION: server will restart for wipe in {minutesBeforeCustomWipe} minute(s).");
                        if(checkTimer != null)
                        {
                            checkTimer.Destroy();
                            checkTimer = null;
                        }
                        if(DailyRestartCheckRepeater != null)
                        {
                            DailyRestartCheckRepeater.Destroy();
                            DailyRestartCheckRepeater = null;
                        }
                    }
                }
            }

        }

        // Returns minutes remaining until a custom wipe date/time; null if invalid/missing.
        private int? MinutesUntilCustomWipe(string dateStr, string timeStr, TimeZoneInfo tz = null)
        {
            if (tz == null) tz = TimeZoneInfo.Local;
            if (string.IsNullOrWhiteSpace(dateStr) || string.IsNullOrWhiteSpace(timeStr)) return null;

            var dateNormalized = dateStr.Trim().Replace('-', '/');
            var dateFormats = new[] { "d/M/yyyy", "dd/MM/yyyy", "d/MM/yyyy", "dd/M/yyyy" };
            if (!DateTime.TryParseExact(dateNormalized, dateFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime datePart))
            {
                if (!DateTime.TryParse(dateNormalized, System.Globalization.CultureInfo.GetCultureInfo("it-IT"), System.Globalization.DateTimeStyles.None, out datePart))
                    return null;
            }

            if (!DateTime.TryParseExact(timeStr.Trim(), new[] { "H:mm", "HH:mm" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime timePart))
            {
                if (!DateTime.TryParse(timeStr.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.NoCurrentDateDefault, out timePart))
                    return null;
            }

            try
            {
                var targetInTz = new DateTime(datePart.Year, datePart.Month, datePart.Day, timePart.Hour, timePart.Minute, 0, DateTimeKind.Unspecified);
                DateTime targetUtc;
                try { targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetInTz, tz); }
                catch { targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetInTz, TimeZoneInfo.Local); }

                double minutesDouble = (targetUtc - DateTime.UtcNow).TotalMinutes;
                return (int)Math.Ceiling(Math.Max(0.0, minutesDouble));
            }
            catch
            {
                return null;
            }
        }

        // Builds a safe single-argument representation (base64 JSON) of files to delete.
        private string BuildFileListSingleArg(List<string> items)
        {
            if (items == null || items.Count == 0) return "";

            var cleaned = new List<string>(items.Count);
            foreach (var it in items)
            {
                var t = it?.Trim();
                if (!string.IsNullOrEmpty(t))
                    cleaned.Add(t);
            }
            if (cleaned.Count == 0) return "";

            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < cleaned.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"');
                sb.Append(JsonEscape(cleaned[i]));
                sb.Append('"');
            }
            sb.Append(']');
            string json = sb.ToString();

            var bytes = Encoding.UTF8.GetBytes(json);
            var b64 = Convert.ToBase64String(bytes);
            var b64url = b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var candidate = b64url;
            if (!IsSafeArgValue(candidate))
            {
                candidate = b64;
                if (!IsSafeArgValue(candidate))
                    return "";
            }

            return candidate;
        }

        // Escapes a string for JSON contexts (quotes, control characters, and unicode).
        private string JsonEscape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        #endregion
    }
}

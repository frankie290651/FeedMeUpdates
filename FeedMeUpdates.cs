using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("FeedMeUpdates", "frankie290651", "1.2.5")]
    [Description("Highly configurable plugin for Oxide framework to orchestrate Server/Oxide/Plugins updates.")]
    public class FeedMeUpdates : CovalencePlugin
    {
        #region Config minimal

        private class ConfigData
        {
            public string ServerDirectory { get; set; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\rust-server" : "/home/rust/rust-server";
            public string SteamCmdPath { get; set; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\steamcmd\steamcmd.exe" : "/usr/games/steamcmd";
            public string UpdaterExecutablePath { get; set; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\rust-server\FeedMeUpdates.exe" : "/home/rust/rust-server/FeedMeUpdates";
            public bool ShowUpdaterConsole { get; set; } = false;

            public string ServerStartScript { get; set; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\rust-server\start_server.bat" : "/home/rust/rust-server/start_server.sh";
            public bool RunServerScriptHidden { get; set; } = false;
            public string ServerTmuxSession { get; set; } = "";

            public bool RustOnService { get; set; } = false;
            public string ServiceName { get; set; } = "";
            public string ServiceType { get; set; } = "";

            public bool StartupScan { get; set; } = true;
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
            // Initializes default configuration object.
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

        // Parses a single scheme instruction line into structured data.
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
                                if (c == 'e')
                                {
                                    instr.OxideProtocolErrorEvent = true;
                                    instr.OxideUpdateEvent = true;
                                    oxideSet = true;
                                }
                                else if (c == 's')
                                {
                                    instr.OxideProtocolMatchEvent = true;
                                    instr.OxideUpdateEvent = true;
                                    oxideSet = true;
                                }
                                else if (c == 'c')
                                {
                                    instr.OxideProtocolMismatchEvent = true;
                                    instr.OxideUpdateEvent = true;
                                    oxideSet = true;
                                }
                                else if (c == 'u')
                                {
                                    instr.OxideProtocolUnknownEvent = true;
                                    instr.OxideUpdateEvent = true;
                                    oxideSet = true;
                                }
                                else if (char.IsWhiteSpace(c))
                                {
                                }
                                else
                                {
                                    instr.ErrorText = $"Unknown oxide protocol event flag: '{c}'";
                                    return instr;
                                }
                            }
                        }
                        continue;
                    }

                    instr.ErrorText = $"Unknown event specifier: '{ev}' (expected 'server' or 'oxide(...)')";
                    return instr;
                }

                if ((instr.ServerEvent || instr.OxideUpdateEvent) && string.IsNullOrEmpty(instr.ErrorText))
                {
                    instr.isValid = true;
                }
                else
                {
                    instr.ErrorText = "No valid events parsed";
                }
            }
            catch (Exception ex)
            {
                instr.ErrorText = "Error parsing instruction: " + ex.Message;
            }

            return instr;
        }

        // Reads and parses the entire scheme file into memory.
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
                if (!sIn.isValid)
                {
                    scheme.isValid = false;
                }
                scheme.instructions.Add(sIn);
                x++;
            }
            if (scheme.isValid)
            {
                Puts("Scheme correctly parsed. Now running on scheme behaviour.");
            }
            else
            {
                Puts("Unable to parse scheme file. The following instructions are invalid:");
                foreach (schemeInstruction si in scheme.instructions)
                {
                    if (!si.isValid)
                    {
                        Puts("(" + si.RowNum.ToString() + ") " + si.ErrorText);
                    }
                }
                Puts("UseScheme has been disabled. Plugin is going back to default behaviour.");
                configData.UseScheme = false;
            }
        }

        #endregion

        #region Discord Notification Helper

        // Sends a Discord webhook notification if enabled.
        private async void SendDiscordNotification(string message)
        {
            if (configData?.DiscordNotificationsEnabled != true) return;
            if (string.IsNullOrEmpty(configData?.DiscordWebhookUrl)) return;

            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add(HttpRequestHeader.ContentType, "application/json");
                    string safeMsg = message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
                    string payload = "{\"content\": \"" + safeMsg + "\"}";
                    await client.UploadStringTaskAsync(configData.DiscordWebhookUrl, "POST", payload);
                }
            }
            catch (Exception ex)
            {
                Puts("Error sending Discord notification: " + ex.Message);
            }
        }

        // Sends a Discord notification indicating update start.
        private void NotifyDiscordUpdateStart(bool updateServer, bool updateOxide, string remoteOxideVersion, bool protocolChanged)
        {
            if (configData?.DiscordNotificationsEnabled != true) return;

            var updateType = (updateServer && updateOxide) ? "Server & Oxide" : (updateServer ? "Server" : (updateOxide ? "Oxide" : "No update"));
            var protocolMsg = updateOxide ? (protocolChanged ? "Protocol NUMBER CHANGE" : "No protocol change") : "";
            var oxideMsg = updateOxide ? $"Remote Oxide version: {remoteOxideVersion ?? "unknown"}" : "";

            var msg = $"[FeedMeUpdates] Starting update: {updateType}\n" +
                (string.IsNullOrEmpty(oxideMsg) ? "" : oxideMsg + "\n") +
                (string.IsNullOrEmpty(protocolMsg) ? "" : protocolMsg);

            SendDiscordNotification(msg);
        }

        // Sends a Discord notification indicating update result.
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

            SendDiscordNotification(msg);
        }

        #endregion

        #region Init / Unload

        // Initializes plugin state and performs initial detection/setup.
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

            Puts($"FeedMeUpdates initialized (v{cachedPluginVersion}).");

            try { ProcessSteamTempAndDataFile(); } catch (Exception ex) { Puts("Error ProcessSteamTempAndDataFile: " + ex.Message); }
            try { ProcessTryNumber(); } catch (Exception ex) { Puts("Error ProcessTryNumber: " + ex.Message); }
            try { ProcessUpdaterMarkerIfPresent(); } catch (Exception ex) { Puts("Marker processing error: " + ex.Message); }
            try { ProcessUpdaterLockIfPresent(); } catch (Exception ex) { Puts("Lock processing error: " + ex.Message); }

            if (configData.MaxAttempts != 0 && trynumber >= configData.MaxAttempts) { enablePlugin = false; }
            else { enablePlugin = true; }

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

        // Cleans up timers and runtime resources on unload.
        void Unload()
        {
            checkTimer?.Destroy();
            checkTimer = null;
            DailyRestartCheckRepeater?.Destroy();
            DailyRestartCheckRepeater = null;
        }

        #endregion

        #region steambid.temp + datafile

        // Ensures trynumber is loaded or initialized in data file.
        private void ProcessTryNumber()
        {
            if (!ReadTryNumber())
            {
                if (!WriteTryNumber())
                {
                    Puts("Unable to define try number in datafile.");
                }
            }
            return;
        }

        // Reads attempt counter from data file if present.
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

        // Writes current attempt counter to data file.
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

        // Processes temporary steam build file and persists values.
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

        // Loads local steam build ID from data file.
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

        // Saves local steam build ID into data file.
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

        // Saves remote steam build ID into data file.
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

        // Removes remote steam build ID from data file.
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
            catch (Exception ex) { Puts("Error removing RemoteSteamBuildID from datafile: " + ex.Message); }
        }

        // Reads plugin data file into dictionary.
        private Dictionary<string, string> ReadDataFileDict()
        {
            try
            {
                var dict = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, string>>(DataFileName);
                return dict ?? new Dictionary<string, string>();
            }
            catch (Exception ex) { Puts("Generic datafile read error: " + ex.Message); return new Dictionary<string, string>(); }
        }

        // Writes dictionary into plugin data file.
        private void WriteDataFileDict(Dictionary<string, string> dict)
        {
            try { Interface.Oxide.DataFileSystem.WriteObject(DataFileName, dict); }
            catch (Exception ex) { Puts("Generic datafile write error: " + ex.Message); }
        }

        #endregion

        #region Marker & Lock

        // Processes updater marker file and promotes remote build ID if success.
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

        // Extracts a simple JSON string value from raw JSON content.
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

        // Processes lock file left by updater and removes it.
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

        // Applies scheme-based decision logic and returns update flags.
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

        // Applies default update logic to determine pending updates.
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
                        return new bool[] { false, false, shithappens };
                    }
                }
            }
            catch
            {
                shithappens = true;
                throw;
            }
        }

        // Starts periodic scheduled update checks.
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

        // Executes asynchronous detection logic for updates.
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

        // Queries SteamCMD for remote Rust build ID.
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

        // Parses the build ID from SteamCMD output.
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

        // Retrieves latest remote Oxide release version.
        private string GetRemoteOxideVersion()
        {
            try
            {
                var url = "https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest";
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "FeedMeUpdates/1.2.5");
                    var content = client.DownloadString(url);
                    if (string.IsNullOrEmpty(content)) return null;
                    var tagMatch = Regex.Match(content, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"", RegexOptions.IgnoreCase);
                    string tag = tagMatch.Success ? tagMatch.Groups[1].Value : null;
                    return tag;
                }
            }
            catch (Exception ex)
            {
                Puts("GetRemoteOxideVersion error: " + ex.Message);
                throw;
            }
        }

        #endregion

        #region Local Oxide via FileVersionInfo

        // Attempts to resolve installed Oxide version from DLL metadata.
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

        // Normalizes version string to a standard numeric form.
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

        // Determines if a remote Oxide tag matches local protocol version.
        private bool? GetOxideCompatibilityInfo(string oxideTag, out string localProto, out string oxideProto, out string note)
        {
            localProto = localProtocol;
            oxideProto = null;
            note = null;

            if (string.IsNullOrEmpty(oxideTag)) { note = "no oxide tag"; return null; }
            if (string.IsNullOrEmpty(localProto)) { note = "local protocol unknown"; return null; }

            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "FeedMeUpdates/1.2.5");
                    var commitUrl = $"https://api.github.com/repos/OxideMod/Oxide.Rust/commits/{WebUtility.UrlEncode(oxideTag)}";
                    string commitJson = null;
                    try { commitJson = client.DownloadString(commitUrl); }
                    catch (WebException)
                    {
                        try
                        {
                            var relUrl = $"https://api.github.com/repos/OxideMod/Oxide.Rust/releases/tags/{WebUtility.UrlEncode(oxideTag)}";
                            var relJson = client.DownloadString(relUrl);
                            var tc = Regex.Match(relJson, "\"target_commitish\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            var commitish = tc.Success ? tc.Groups[1].Value : null;
                            if (!string.IsNullOrEmpty(commitish))
                            {
                                commitUrl = $"https://api.github.com/repos/OxideMod/Oxide.Rust/commits/{WebUtility.UrlEncode(commitish)}";
                                commitJson = client.DownloadString(commitUrl);
                            }
                        }
                        catch (Exception ex2) { note = "commit/release lookup failed: " + ex2.Message; return null; }
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
            }
            catch (Exception ex) { note = "exception: " + ex.Message; return null; }
        }

        // Logs compatibility of Oxide release with local Rust protocol.
        private bool IsOxideReleaseCompatibleWithLocalRust(string oxideTag)
        {
            try
            {
                string lp, op, note;
                var res = GetOxideCompatibilityInfo(oxideTag, out lp, out op, out note);
                Puts($"Compatibility: local={lp ?? "n/a"} oxide={op ?? "n/a"} note={note ?? "none"} result={(res==true?"COMPATIBLE":(res==false?"INCOMPATIBLE":"UNKNOWN"))}");
                return res == true;
            }
            catch (Exception ex) { Puts("Compatibility error: " + ex.Message); return false; }
        }

        #endregion

        #region Countdown & Updater invocation

        // Starts a countdown before invoking updater and restarting server.
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
                    PerformSaveAndQuitGracefully(saveAlreadyDone: true);

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

        // Launches the external updater process with configured arguments.
        private void StartUpdaterExecutable(bool updateServer, bool updateOxide, string overrideUpdateId = null, string overrideWhat = null, string remoteBuildArg = null, string remoteOxideArg = null)
        {
            try
            {
                string updateId = !string.IsNullOrEmpty(overrideUpdateId) ? overrideUpdateId : new System.Random().Next(0, 100000000).ToString("D8");
                string what = !string.IsNullOrEmpty(overrideWhat) ? overrideWhat : (updateServer && updateOxide ? "both" : (updateServer ? "server" : (updateOxide ? "oxide" : "none")));
                string updatePluginsArg = configData.UpdatePlugins ? "yes" : "no";

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
                string tempIsService = configData.RustOnService ? "1" : "0";
                string showserverconsole = configData.RunServerScriptHidden ? "0" : "1";

                string args = $"-update_id \"{updateId}\" -what \"{what}\" -update_plugins \"{updatePluginsArg}\" -server_dir \"{configData.ServerDirectory}\" -steamcmd \"{configData.SteamCmdPath}\" -start_script \"{configData.ServerStartScript}\" -remote_build \"{remoteBuildArgEsc}\" -remote_oxide \"{remoteOxideArgEsc}\" -isService \"{tempIsService}\" -serviceType \"{configData.ServiceType}\" -serviceName \"{configData.ServiceName}\" -showserverconsole \"{showserverconsole}\" -servertmux \"{configData.ServerTmuxSession}\"";

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

        // Command: shows or toggles periodic check status.
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
            player?.Message($"FeedMeUpdates status: {status}. pendingServerUpdate={pendingServerUpdate} pendingOxideUpdate={pendingOxideUpdate} LocalSteamBuildID={(string.IsNullOrEmpty(LocalSteamBuildID) ? "<empty>" : LocalSteamBuildID)}");
            Puts($"Status requested. {status}. pendingServerUpdate={pendingServerUpdate} pendingOxideUpdate={pendingOxideUpdate}");
        }

        // Command: shows plugin and environment version details.
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

        // Command: forces immediate test update and shutdown.
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
                PerformSaveAndQuitGracefully(saveAlreadyDone: false);
            });
        }

        #endregion

        #region Utilities

        // Validates the configured daily restart time format.
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

        // Disables periodic checks near scheduled daily restart time.
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

        // Parses an HH:mm time string into hour and minute.
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

        // Checks whether a tmux session exists.
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

        // Detects availability of GNOME terminal and tmux on Linux.
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

        // Checks permissions for a player or console.
        private bool HasPermissionOrConsole(IPlayer player)
        {
            if (player == null) return true;
            if (player.IsServer) return true;
            if (player.IsAdmin) return true;
            if (permission.UserHasPermission(player.Id, "feedme.run")) return true;
            player.Message("You do not have permission to run this command.");
            return false;
        }

        // Broadcasts a message to all connected players.
        private void BroadcastToPlayers(string message)
        {
            try
            {
                foreach (var pl in players.Connected) pl.Message(message);
                Puts($"Broadcast: {message}");
            }
            catch (Exception ex) { Puts("Broadcast error: " + ex.Message); }
        }

        // Broadcasts a message only to admins or privileged users.
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

        // Performs a graceful save and shutdown sequence.
        private void PerformSaveAndQuitGracefully(bool saveAlreadyDone = false, float delayBeforeQuitAfterSave = 2f)
        {
            try
            {
                if (!saveAlreadyDone)
                {
                    Puts($"FeedMeUpdates: scheduling server.save in 0.1s...");
                    timer.Once(0.1f, () =>
                    {
                        try
                        {
                            Puts("FeedMeUpdates: issuing server.writecfg now...");
                            ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.writecfg");
                            Puts("FeedMeUpdates: server.writecfg issued.");
                            Thread.Sleep(500);
                            Puts("FeedMeUpdates: issuing server.save now...");
                            ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.save");
                            Thread.Sleep(500);
                            Puts("FeedMeUpdates: server.save issued.");
                        }
                        catch (Exception ex)
                        {
                            Puts("FeedMeUpdates: server.save error: " + ex.Message);
                        }

                        Puts($"FeedMeUpdates: scheduling quit in {delayBeforeQuitAfterSave}s...");
                        timer.Once(delayBeforeQuitAfterSave, () =>
                        {
                            try
                            {
                                Puts("FeedMeUpdates: issuing quit...");
                                ConsoleSystem.Run(ConsoleSystem.Option.Server, "quit");
                                Puts("FeedMeUpdates: quit command issued.");
                            }
                            catch (Exception ex)
                            {
                                Puts("FeedMeUpdates: quit command error: " + ex.Message);
                            }

                            timer.Once(1f, () =>
                            {
                                try
                                {
                                    Puts("FeedMeUpdates: issuing server.shutdown (fallback)...");
                                    ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.shutdown");
                                    Puts("FeedMeUpdates: server.shutdown issued.");
                                }
                                catch (Exception ex)
                                {
                                    Puts("FeedMeUpdates: server.shutdown error: " + ex.Message);
                                }
                            });
                        });
                    });
                }
                else
                {
                    Puts($"FeedMeUpdates: scheduling quit in {delayBeforeQuitAfterSave}s (saveAlreadyDone=true)...");
                    timer.Once(delayBeforeQuitAfterSave, () =>
                    {
                        try
                        {
                            Puts("FeedMeUpdates: issuing quit...");
                            ConsoleSystem.Run(ConsoleSystem.Option.Server, "quit");
                            Puts("FeedMeUpdates: quit command issued.");
                        }
                        catch (Exception ex)
                        {
                            Puts("FeedMeUpdates: quit command error: " + ex.Message);
                        }

                        timer.Once(1f, () =>
                        {
                            try
                            {
                                Puts("FeedMeUpdates: issuing server.shutdown (fallback)...");
                                ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.shutdown");
                                Puts("FeedMeUpdates: server.shutdown issued.");
                            }
                            catch (Exception ex)
                            {
                                Puts("FeedMeUpdates: server.shutdown error: " + ex.Message);
                            }
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                Puts("PerformSaveAndQuitGracefully error: " + ex.Message);
            }
        }

        #endregion

        #region Hooks

        // Hook: runs after server initialization to trigger startup logic.
        void OnServerInitialized()
        {
            if (string.IsNullOrEmpty(LocalSteamBuildID))
            {
                Puts("OnServerInitialized: LocalSteamBuildID is empty -> starting updater immediately (init flow).");
                StartUpdaterExecutable(updateServer: true, updateOxide: true, overrideUpdateId: "init", overrideWhat: "both", remoteBuildArg: "no", remoteOxideArg: "no");
                Puts("OnServerInitialized: scheduled save+quit after updater start (init).");
                PerformSaveAndQuitGracefully(saveAlreadyDone: false);
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
                    PerformSaveAndQuitGracefully(saveAlreadyDone: false);
                    return;
                }
                else
                {
                    Puts("StartupScan: no updates available at startup.");
                }
            }

            if (checkTimer == null) StartPeriodicCheck();
        }

        // Hook: server save handled explicitly elsewhere.
        void OnServerSave() { }

        #endregion
    }
}
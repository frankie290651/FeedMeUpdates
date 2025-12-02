using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace FeedMeUpdates
{
    #region Logger and RunState

    internal static class Logger
    {
        private static StreamWriter? writer;
        private static readonly object sync = new();
        private const long MaxSizeBytes = 5L * 1024L * 1024L;

        // Initialize logger and rotate log file if needed.
        public static void Init(string serverDir)
        {
            lock (sync)
            {
                try
                {
                    Directory.CreateDirectory(serverDir);
                    var logPath = Path.Combine(serverDir, "updater.log");
                    FileMode mode = FileMode.Append;
                    if (File.Exists(logPath))
                    {
                        try
                        {
                            var fi = new FileInfo(logPath);
                            if (fi.Length > MaxSizeBytes)
                            {
                                mode = FileMode.Create;
                            }
                        }
                        catch
                        {
                            mode = FileMode.Append;
                        }
                    }
                    else
                    {
                        mode = FileMode.Create;
                    }

                    writer?.Flush();
                    writer?.Dispose();
                    var fs = new FileStream(logPath, mode, FileAccess.Write, FileShare.Read);
                    writer = new StreamWriter(fs) { AutoFlush = true };
                }
                catch
                {
                    writer = null;
                }
            }
        }

        // Return timestamp for log entries.
        private static string Timestamp()
        {
            var now = DateTime.Now;
            return $"[{now:dd/MM/yy HH:mm:ss}]";
        }

        // Write a line to console and log file with level tag.
        private static void Write(string level, string message)
        {
            var line = $"{Timestamp()} [{level}] {message}";
            lock (sync)
            {
                try { Console.WriteLine(line); } catch { }
                try { writer?.WriteLine(line); } catch { }
            }
        }

        // Convenience log level methods.
        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        // Close and release logger resources.
        public static void Close()
        {
            lock (sync)
            {
                try { writer?.Flush(); writer?.Dispose(); } catch { }
                writer = null;
            }
        }
    }

    internal static class RunState
    {
        public static string UpdateId = "";
        public static string What = "";
        public static string UpdatePluginsFlag = "";
        public static string ServerDir = "";
        public static string SteamCmd = "";
        public static string StartScript = "";
        public static string RemoteBuild = "";
        public static string RemoteOxide = "";
        public static List<string> UpdatedPlugins = new();
        public static bool IsService = false;
        public static string ServiceType = "";
        public static string ServiceName = "";
        public static bool ShowServerConsole = false;
        public static string ServerTmux = "";

        public static bool IsForce = false;
        public static string NextWipeLevel = "";
        public static string NextWipeUrl = "";
        public static string NextWipeSeed = "";
        public static string NextWipeMapsize = "";
        public static bool NextWipeKeepBps = false;
        public static bool NextWipeDeletePlayerData = false;
        public static string NextWipeDelPluginsData = "";
        public static List<string> PdataFilesToDelete = new List<string>();
        public static string ServerIdentity = "";
        public static string ServerName = "";
        public static string ServerDescription = "";
    }

    #endregion

    #region Program main and argument parsing

    internal static class Program
    {
        private static string FMU_VERSION = "";
        private static bool isStandalone = false;
        private static string Fails = "";
        private static readonly List<DateTime> __PluginRequestTimestamps = new();

        private enum FlowKind { Init, Testrun, Scheduled, Wipe }

        // Parse arguments and dispatch flow.
        static int Main(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                Console.WriteLine("No arguments provided. Exiting.");
                return 0;
            }

            var firstToken = args[0].Trim();
            if (!firstToken.StartsWith("-"))
            {
                Console.WriteLine("First argument is not a flag. Exiting.");
                return 0;
            }

            var flagPair = SplitFlagToken(firstToken);
            string firstKey = flagPair.key;
            string firstValFromToken = flagPair.value;

            if (!string.Equals(firstKey, "update_id", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("First flag is not -update_id. Exiting.");
                return 0;
            }

            string updateId;
            int consumed = 1;
            if (!string.IsNullOrEmpty(firstValFromToken))
            {
                updateId = firstValFromToken;
            }
            else
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Missing value for -update_id. Exiting.");
                    return 0;
                }
                updateId = args[1];
                consumed = 2;
            }

            var remaining = new List<string>();
            for (int i = consumed; i < args.Length; i++) remaining.Add(args[i]);
            var parsed = ParseKeyValueArgs(remaining.ToArray());

            parsed.TryGetValue("what", out var whatVal);
            parsed.TryGetValue("update_plugins", out var updatePluginsVal);
            parsed.TryGetValue("server_dir", out var serverDirVal);
            parsed.TryGetValue("steamcmd", out var steamCmdVal);
            parsed.TryGetValue("start_script", out var startScriptVal);
            parsed.TryGetValue("remote_build", out var remoteBuildVal);
            parsed.TryGetValue("remote_oxide", out var remoteOxideVal);
            parsed.TryGetValue("isservice", out var isServiceVal);
            parsed.TryGetValue("servicetype", out var serviceTypeVal);
            parsed.TryGetValue("servicename", out var serviceNameVal);
            parsed.TryGetValue("showserverconsole", out var showserverconsoleVal);
            parsed.TryGetValue("servertmux", out var servertmuxVal);

            parsed.TryGetValue("isforce", out var isForceVal);
            parsed.TryGetValue("nextwipelevel", out var nextWipeLevelVal);
            parsed.TryGetValue("nextwipeurl", out var nextWipeUrlVal);
            parsed.TryGetValue("nextwipeseed", out var nextWipeSeedVal);
            parsed.TryGetValue("nextwipemapsize", out var nextWipeMapsizeVal);
            parsed.TryGetValue("nextwipekeepbps", out var nextWipeKeepBpsVal);
            parsed.TryGetValue("nextwipedelplayerdata", out var nextWipeDelPlayerDataVal);
            parsed.TryGetValue("nextwipedelpluginsdata", out var nextWipeDelPluginsDataVal);
            parsed.TryGetValue("serveridentity", out var serverIdentityVal);
            parsed.TryGetValue("servername", out var serverNameVal);
            parsed.TryGetValue("serverdescription", out var serverDescriptionVal);

            RunState.UpdateId = updateId;
            RunState.What = whatVal ?? "";
            RunState.UpdatePluginsFlag = updatePluginsVal ?? "";
            RunState.ServerDir = serverDirVal ?? Directory.GetCurrentDirectory();
            RunState.SteamCmd = steamCmdVal ?? "";
            RunState.StartScript = startScriptVal ?? "";
            RunState.RemoteBuild = remoteBuildVal ?? "";
            RunState.RemoteOxide = remoteOxideVal ?? "";
            RunState.IsService = ParseBool(isServiceVal);
            RunState.ServiceType = serviceTypeVal ?? "";
            RunState.ServiceName = serviceNameVal ?? "";
            RunState.UpdatedPlugins = new List<string>();
            RunState.ShowServerConsole = ParseBool(showserverconsoleVal);
            RunState.ServerTmux = servertmuxVal ?? "";

            RunState.IsForce = ParseBool(isForceVal);
            RunState.NextWipeLevel = nextWipeLevelVal ?? "";
            RunState.NextWipeUrl = RevertFromb64(nextWipeUrlVal) ?? "";
            RunState.NextWipeSeed = nextWipeSeedVal ?? "";
            RunState.NextWipeMapsize = nextWipeMapsizeVal ?? "";
            RunState.NextWipeKeepBps = ParseBool(nextWipeKeepBpsVal);
            RunState.NextWipeDeletePlayerData = ParseBool(nextWipeDelPlayerDataVal);
            RunState.NextWipeDelPluginsData = nextWipeDelPluginsDataVal ?? "";
            RunState.ServerIdentity = serverIdentityVal ?? "";
            RunState.ServerName = RevertFromb64(serverNameVal) ?? "";
            RunState.ServerDescription = RevertFromb64(serverDescriptionVal) ?? "";

            if (RunState.IsService && RunState.ShowServerConsole)
                RunState.ShowServerConsole = false;

            if (string.Equals(updateId, "init", StringComparison.OrdinalIgnoreCase))
                return HandleFlow(FlowKind.Init);
            if (string.Equals(updateId, "testrun", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(RunState.What)) RunState.What = "both";
                return HandleFlow(FlowKind.Testrun);
            }
            if (string.Equals(updateId, "wipe", StringComparison.OrdinalIgnoreCase))
            {
                return HandleFlow(FlowKind.Wipe);
            }
            if (Regex.IsMatch(updateId, @"^\d{8}$"))
            {
                if (string.IsNullOrEmpty(RunState.RemoteBuild)) RunState.RemoteBuild = "no";
                if (string.IsNullOrEmpty(RunState.RemoteOxide)) RunState.RemoteOxide = "no";
                return HandleFlow(FlowKind.Scheduled);
            }

            Console.WriteLine($"Unrecognized update_id value: '{updateId}'. Exiting without actions.");
            return 0;
        }

        // Decode optional base64-encoded argument values.
        private static string RevertFromb64(string? argValue)
        {
            if (argValue == null)
                return "";
            var original = argValue;
            if (argValue.StartsWith("b64:"))
            {
                var b64 = argValue.Substring(4);
                var rawBytes = Convert.FromBase64String(b64);
                original = Encoding.UTF8.GetString(rawBytes);
            }
            return original;
        }

    #endregion

    #region Flow handler and validation

        // Coordinate the selected flow including backup, update and restart.
        private static int HandleFlow(FlowKind kind)
        {
            Logger.Init(RunState.ServerDir);
            Logger.Info($"{kind.ToString().ToUpper()} flow started. Args: update_id={RunState.UpdateId} what={RunState.What} update_plugins={RunState.UpdatePluginsFlag} server_dir={RunState.ServerDir} steamcmd={RunState.SteamCmd} start_script={RunState.StartScript} isService={RunState.IsService} serviceName={RunState.ServiceName}");
            var updatingLockPath = Path.Combine(RunState.ServerDir, "updating.lock");

            string wipeResult = "no";
            string wipeingErrors = "";

            if (!ValidateFlow(kind))
            {
                Logger.Close();
                return 0;
            }

            Logger.Info($"{kind.ToString().ToUpper()} validation passed.");

            WaitForRustDedicatedAndLog();

            if (File.Exists(updatingLockPath))
            {
                Logger.Warn($"updating.lock file present in {RunState.ServerDir}");
                CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Blocked by updating.lock file", RunState.StartScript);
                Logger.Info("Program exits (lock present).");
                Logger.Close();
                return 0;
            }
            else
            {
                File.Create(updatingLockPath).Dispose();
            }

            if (kind == FlowKind.Wipe)
            {
                try
                {
                    Logger.Info("CUSTOM WIPE request detected.");
                    if (RunState.NextWipeDelPluginsData != "")
                    {
                        RunState.PdataFilesToDelete = DecodeFileListArgToList(RunState.NextWipeDelPluginsData);
                        if (RunState.PdataFilesToDelete.Count == 0)
                        {
                            Logger.Warn("Plugins datafile argument interpretation failed. No plugin data will be deleted.");
                        }
                    }
                    Logger.Info("Invoking WipeCycle.");
                    wipeResult = WipeCycle(out wipeingErrors);
                    Logger.Info("CUSTOM WIPE: WipeCycle completed.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"CUSTOM WIPE: WipeCycle error: {ex.Message}");
                    CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, ex.Message, RunState.StartScript);
                    Logger.Close();
                    return 0;
                }
                try
                {
                    var marker = new Dictionary<string, object?>();
                    marker["result"] = "success";
                    marker["fail_reason"] = null;
                    marker["timestamp"] = DateTime.UtcNow.ToString("o");
                    marker["update_id"] = "wipe";
                    marker["wiped"] = wipeResult;
                    marker["wipe_info"] = wipeingErrors;

                    var markerPath = Path.Combine(RunState.ServerDir, "updateresult.json");
                    var json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(markerPath, json);
                    Logger.Info($"{kind.ToString().ToUpper()}: Wrote success marker to {markerPath}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"{kind.ToString().ToUpper()}: Error writing success marker: {ex.Message}");
                }
                try
                {
                    if (!RunState.IsService)
                    {
                        Logger.Info($"{kind.ToString().ToUpper()}: Starting start_script: {RunState.StartScript}");
                        StartServerScript(RunState.StartScript, RunState.ServerDir);
                        Logger.Info($"{kind.ToString().ToUpper()}: start_script launched.");
                    }
                    else
                    {
                        Logger.Info($"{kind.ToString().ToUpper()}: Starting service: {RunState.ServiceName}");
                        RestartRustService(RunState.ServiceName);
                        Logger.Info($"{kind.ToString().ToUpper()}: service start invoked.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"{kind.ToString().ToUpper()}: Failed to start {(RunState.IsService ? "service" : "start_script")}: {ex.Message}");
                }

                Logger.Info($"{kind.ToString().ToUpper()}: Flow complete. Exiting.");
                Logger.Close();
                return 0;
            }

            if (!CreateTempBackup(RunState.ServerDir, RunState.UpdateId, out var backupPath))
            {
                Logger.Error($"{kind.ToString().ToUpper()}: Failed to create temporary backup.");
                CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Failed backup creation", RunState.StartScript);
                Logger.Close();
                return 0;
            }
            Logger.Info($"{kind.ToString().ToUpper()}: temp backup created at: {backupPath}");

            if (kind == FlowKind.Scheduled)
            {
                if (IsWhatServerOrBoth())
                {
                    Logger.Info("SCHEDULED: Server update requested. Determining remote build id...");
                    var steamBuildId = GetRemoteRustBuild();
                    Logger.Info($"SCHEDULED: SteamCMD reported build id: {steamBuildId}");

                    if (string.IsNullOrEmpty(RunState.RemoteBuild) || string.Equals(RunState.RemoteBuild, "no", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Error("SCHEDULED: remote_build argument missing or 'no' while server update requested.");
                        Fails = "server update (different version detected)";
                        Logger.Info("SCHEDULED: Calling restore due to missing remote_build...");
                        RestoreFromBackupOrFail(RunState.ServerDir, backupPath, RunState.UpdateId, RunState.StartScript);
                        Logger.Close();
                        return 0;
                    }

                    if (!string.Equals(steamBuildId, RunState.RemoteBuild, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Error($"SCHEDULED: Remote build mismatch: steamcmd_build='{steamBuildId}' expected_remote_build='{RunState.RemoteBuild}'. Treating as failure.");
                        Fails = "server update (different version detected)";
                        Logger.Info("SCHEDULED: Calling restore due to remote build mismatch...");
                        RestoreFromBackupOrFail(RunState.ServerDir, backupPath, RunState.UpdateId, RunState.StartScript);
                        Logger.Close();
                        return 0;
                    }

                    Logger.Info("SCHEDULED: Remote build matches requested build. Proceeding with UpdateServer...");
                    if (!RunUpdateServerWithRestoreOnFail(kind, backupPath)) return 0;
                }

                if (IsWhatOxideOrBoth())
                {
                    Logger.Info("SCHEDULED: Oxide update requested. Determining latest Oxide tag...");
                    var (tag, url) = GetRemoteOxideRelease();
                    Logger.Info($"SCHEDULED: Oxide latest tag reported: '{tag}' url='{url}'");

                    if (string.IsNullOrEmpty(RunState.RemoteOxide) || string.Equals(RunState.RemoteOxide, "no", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Error("SCHEDULED: remote_oxide argument missing or 'no' while oxide update requested.");
                        Fails = "oxide update (different version detected)";
                        Logger.Info("SCHEDULED: Calling restore due to missing remote_oxide...");
                        RestoreFromBackupOrFail(RunState.ServerDir, backupPath, RunState.UpdateId, RunState.StartScript);
                        Logger.Close();
                        return 0;
                    }

                    if (!string.Equals(tag, RunState.RemoteOxide, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Error($"SCHEDULED: Oxide tag mismatch: latest_tag='{tag}' expected_remote_oxide='{RunState.RemoteOxide}'. Treating as failure.");
                        Fails = "oxide update (different version detected)";
                        Logger.Info("SCHEDULED: Calling restore due to oxide tag mismatch...");
                        RestoreFromBackupOrFail(RunState.ServerDir, backupPath, RunState.UpdateId, RunState.StartScript);
                        Logger.Close();
                        return 0;
                    }

                    Logger.Info("SCHEDULED: Remote oxide tag matches requested tag. Proceeding with UpdateOxide...");
                    if (!RunUpdateOxideWithRestoreOnFail(kind, backupPath)) return 0;
                }
            }
            else
            {
                var steamBuildId = GetRemoteRustBuild();
                Logger.Info($"{kind.ToString().ToUpper()}: Steam build id determined: {steamBuildId}");

                if (!RunUpdateServerWithRestoreOnFail(kind, backupPath)) return 0;
                if (!RunUpdateOxideWithRestoreOnFail(kind, backupPath)) return 0;

                if (kind == FlowKind.Init)
                {
                    try
                    {
                        var steambidPath = Path.Combine(RunState.ServerDir, "steambid.temp");
                        File.WriteAllText(steambidPath, steamBuildId);
                        Logger.Info($"INIT: Wrote steambid.temp with build id '{steamBuildId}'");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"INIT: Failed writing steambid.temp: {ex.Message}");
                    }
                }
            }

            if (string.Equals(RunState.UpdatePluginsFlag, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"{kind.ToString().ToUpper()}: Starting plugin update pass...");
                var pluginsOk = UpdatePlugins();
                if (!pluginsOk)
                    Logger.Warn($"{kind.ToString().ToUpper()}: Plugin update pass reported errors. Proceeding.");
                else
                    Logger.Info($"{kind.ToString().ToUpper()}: Plugin update pass completed.");
            }
            else
            {
                Logger.Info($"{kind.ToString().ToUpper()}: update_plugins flag is 'no' -> skipping plugin updates.");
            }

            if (kind == FlowKind.Scheduled && RunState.IsForce)
            {
                try
                {
                    Logger.Info("FORCE WIPE detected.");
                    if (RunState.NextWipeDelPluginsData != "")
                    {
                        RunState.PdataFilesToDelete = DecodeFileListArgToList(RunState.NextWipeDelPluginsData);
                        if (RunState.PdataFilesToDelete.Count == 0)
                        {
                            Logger.Warn("Plugins datafile argument interpretation failed. No plugin data will be deleted.");
                        }
                    }
                    Logger.Info("Invoking WipeCycle.");
                    wipeResult = WipeCycle(out wipeingErrors);
                    Logger.Info("SCHEDULED: WipeCycle completed.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"SCHEDULED: WipeCycle error: {ex.Message}");
                    Logger.Warn("SCHEDULED: IMPORTANT! SERVER IS UPDATED BUT MUST BE WIPED MANUALLY.");
                    Logger.Close();
                    return 0;
                }
            }

            try
            {
                var marker = new Dictionary<string, object?>();
                marker["result"] = "success";
                marker["fail_reason"] = null;
                if (string.Equals(RunState.UpdatePluginsFlag, "yes", StringComparison.OrdinalIgnoreCase))
                    marker["updated_plugins"] = RunState.UpdatedPlugins;
                marker["backup_cycle"] = true;
                marker["server_restored"] = "no";
                marker["timestamp"] = DateTime.UtcNow.ToString("o");
                marker["update_id"] = kind == FlowKind.Init ? "init"
                                  : kind == FlowKind.Testrun ? "testrun"
                                  : RunState.UpdateId;
                marker["wiped"] = wipeResult;
                marker["wipe_info"] = wipeingErrors;

                var markerPath = Path.Combine(RunState.ServerDir, "updateresult.json");
                var json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(markerPath, json);
                Logger.Info($"{kind.ToString().ToUpper()}: Wrote success marker to {markerPath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"{kind.ToString().ToUpper()}: Error writing success marker: {ex.Message}");
            }

            CleanTempBackup(RunState.ServerDir);

            try
            {
                if (!RunState.IsService)
                {
                    Logger.Info($"{kind.ToString().ToUpper()}: Starting start_script: {RunState.StartScript}");
                    StartServerScript(RunState.StartScript, RunState.ServerDir);
                    Logger.Info($"{kind.ToString().ToUpper()}: start_script launched.");
                }
                else
                {
                    Logger.Info($"{kind.ToString().ToUpper()}: Starting service: {RunState.ServiceName}");
                    RestartRustService(RunState.ServiceName);
                    Logger.Info($"{kind.ToString().ToUpper()}: service start invoked.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"{kind.ToString().ToUpper()}: Failed to start {(RunState.IsService ? "service" : "start_script")}: {ex.Message}");
            }

            Logger.Info($"{kind.ToString().ToUpper()}: Flow complete. Exiting.");
            Logger.Close();
            return 0;
        }

        // Validate arguments and environment for the selected flow.
        private static bool ValidateFlow(FlowKind kind)
        {
            if(kind == FlowKind.Wipe)
            {
                if(string.IsNullOrEmpty(RunState.ServerDir))
                {
                    Logger.Error($"{kind.ToString().ToUpper()} validation failed: server dir not specified");
                    CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Wipe validation failed: server dir not specified", RunState.StartScript);
                    return false;
                }
                else
                {
                    if(!Directory.Exists(RunState.ServerDir))
                    {
                        Logger.Error($"{kind.ToString().ToUpper()} validation failed: server directory doesn't exist");
                        CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Wipe validation failed: server directory doesn't exist", RunState.StartScript);
                        return false;
                    }
                }
                if (string.IsNullOrEmpty(RunState.ServerIdentity))
                    RunState.ServerIdentity = "my_server_identity";

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (!string.IsNullOrEmpty(RunState.NextWipeDelPluginsData))
                    {
                        if (!Directory.Exists(RunState.ServerDir + "\\oxide\\data"))
                        {
                            Logger.Error($"{kind.ToString().ToUpper()} validation failed: plugin data directory doesn't exist");
                            CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Wipe validation failed: plugin data directory doesn't exist", RunState.StartScript);
                            return false;
                        }
                    }
                    if(!Directory.Exists(RunState.ServerDir + "\\server\\" + RunState.ServerIdentity))
                    {
                        Logger.Error($"{kind.ToString().ToUpper()} validation failed: server identity directory doesn't exist");
                        CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Wipe validation failed: server identity directory doesn't exist", RunState.StartScript);
                        return false;

                    }
                    else
                    {
                        if (!Directory.Exists(RunState.ServerDir + "\\server\\" + RunState.ServerIdentity + "\\cfg"))
                        {
                            Logger.Error($"{kind.ToString().ToUpper()} validation failed: server cfg directory inside server identity doesn't exist");
                            CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Wipe validation failed: server cfg directory inside server identity doesn't exist", RunState.StartScript);
                            return false;
                        }
                        else
                        {
                            if(!File.Exists(RunState.ServerDir + "\\server\\" + RunState.ServerIdentity + "\\cfg" + "\\server.cfg"))
                            {
                                Logger.Error($"{kind.ToString().ToUpper()} validation failed: server.cfg not found");
                                CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Wipe validation failed: server.cfg not found", RunState.StartScript);
                                return false;
                            }

                        }
                    }
                }
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (!string.IsNullOrEmpty(RunState.NextWipeDelPluginsData))
                    {
                        if (!Directory.Exists(RunState.ServerDir + "/oxide/data"))
                        {
                            Logger.Error($"{kind.ToString().ToUpper()} validation failed: plugin data directory doesn't exist");
                            CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Wipe validation failed: plugin data directory doesn't exist", RunState.StartScript);
                            return false;
                        }
                    }
                    if (!Directory.Exists(RunState.ServerDir + "/server/" + RunState.ServerIdentity))
                    {
                        Logger.Error($"{kind.ToString().ToUpper()} validation failed: server identity directory doesn't exist");
                        CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Wipe validation failed: server identity directory doesn't exist", RunState.StartScript);
                        return false;

                    }
                    else
                    {
                        if (!Directory.Exists(RunState.ServerDir + "/server/" + RunState.ServerIdentity + "/cfg"))
                        {
                            Logger.Error($"{kind.ToString().ToUpper()} validation failed: server cfg directory inside server identity doesn't exist");
                            CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Wipe validation failed: server cfg directory inside server identity doesn't exist", RunState.StartScript);
                            return false;
                        }
                        else
                        {
                            if (!File.Exists(RunState.ServerDir + "/server/" + RunState.ServerIdentity + "/cfg" + "/server.cfg"))
                            {
                                Logger.Error($"{kind.ToString().ToUpper()} validation failed: server.cfg not found");
                                CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Wipe validation failed: server.cfg not found", RunState.StartScript);
                                return false;
                            }

                        }
                    }
                }
                return true;
            }
            else if (kind == FlowKind.Init || kind == FlowKind.Testrun)
            {
                if (!string.Equals(RunState.What, "both", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error($"{kind.ToString().ToUpper()} validation failed: what must be 'both' but is '{RunState.What}'");
                    CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, $"{(kind == FlowKind.Init ? "Init" : "Testrun")} validation failed: what!='both' ({RunState.What})", RunState.StartScript);
                    return false;
                }
            }
            else
            {
                if(RunState.IsForce)
                {
                    if (string.IsNullOrEmpty(RunState.ServerDir))
                    {
                        Logger.Error($"{kind.ToString().ToUpper()} validation failed: server dir not specified");
                        CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "ForceWipe validation failed: server dir not specified", RunState.StartScript);
                        return false;
                    }
                    else
                    {
                        if (!Directory.Exists(RunState.ServerDir))
                        {
                            Logger.Error($"{kind.ToString().ToUpper()} validation failed: server directory doesn't exist");
                            CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "ForceWipe validation failed: server directory doesn't exist", RunState.StartScript);
                            return false;
                        }
                    }
                    if (string.IsNullOrEmpty(RunState.ServerIdentity))
                        RunState.ServerIdentity = "my_server_identity";

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        if (!string.IsNullOrEmpty(RunState.NextWipeDelPluginsData))
                        {
                            if (!Directory.Exists(RunState.ServerDir + "\\oxide\\data"))
                            {
                                Logger.Error($"{kind.ToString().ToUpper()} validation failed: plugin data directory doesn't exist");
                                CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "ForceWipe validation failed: plugin data directory doesn't exist", RunState.StartScript);
                                return false;
                            }
                        }
                        if (!Directory.Exists(RunState.ServerDir + "\\server\\" + RunState.ServerIdentity))
                        {
                            Logger.Error($"{kind.ToString().ToUpper()} validation failed: server identity directory doesn't exist");
                            CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "ForceWipe validation failed: server identity directory doesn't exist", RunState.StartScript);
                            return false;

                        }
                        else
                        {
                            if (!Directory.Exists(RunState.ServerDir + "\\server\\" + RunState.ServerIdentity + "\\cfg"))
                            {
                                Logger.Error($"{kind.ToString().ToUpper()} validation failed: server cfg directory inside server identity doesn't exist");
                                CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "ForceWipe validation failed: server cfg directory inside server identity doesn't exist", RunState.StartScript);
                                return false;
                            }
                            else
                            {
                                if (!File.Exists(RunState.ServerDir + "\\server\\" + RunState.ServerIdentity + "\\cfg" + "\\server.cfg"))
                                {
                                    Logger.Error($"{kind.ToString().ToUpper()} validation failed: server.cfg not found");
                                    CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "ForceWipe validation failed: server.cfg not found", RunState.StartScript);
                                    return false;
                                }

                            }
                        }
                    }
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        if (!string.IsNullOrEmpty(RunState.NextWipeDelPluginsData))
                        {
                            if (!Directory.Exists(RunState.ServerDir + "/oxide/data"))
                            {
                                Logger.Error($"{kind.ToString().ToUpper()} validation failed: plugin data directory doesn't exist");
                                CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "ForceWipe validation failed: plugin data directory doesn't exist", RunState.StartScript);
                                return false;
                            }
                        }
                        if (!Directory.Exists(RunState.ServerDir + "/server/" + RunState.ServerIdentity))
                        {
                            Logger.Error($"{kind.ToString().ToUpper()} validation failed: server identity directory doesn't exist");
                            CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "ForceWipe validation failed: server identity directory doesn't exist", RunState.StartScript);
                            return false;

                        }
                        else
                        {
                            if (!Directory.Exists(RunState.ServerDir + "/server/" + RunState.ServerIdentity + "/cfg"))
                            {
                                Logger.Error($"{kind.ToString().ToUpper()} validation failed: server cfg directory inside server identity doesn't exist");
                                CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "ForceWipe validation failed: server cfg directory inside server identity doesn't exist", RunState.StartScript);
                                return false;
                            }
                            else
                            {
                                if (!File.Exists(RunState.ServerDir + "/server/" + RunState.ServerIdentity + "/cfg" + "/server.cfg"))
                                {
                                    Logger.Error($"{kind.ToString().ToUpper()} validation failed: server.cfg not found");
                                    CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "ForceWipe validation failed: server.cfg not found", RunState.StartScript);
                                    return false;
                                }

                            }
                        }
                    }
                }
                if (!string.Equals(RunState.What, "both", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(RunState.What, "server", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(RunState.What, "oxide", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error($"SCHEDULED validation failed: what must be 'both'|'server'|'oxide' but is '{RunState.What}'");
                    CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, $"Scheduled validation failed: invalid what ({RunState.What})", RunState.StartScript);
                    return false;
                }
            }

            if (!string.Equals(RunState.UpdatePluginsFlag, "yes", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(RunState.UpdatePluginsFlag, "no", StringComparison.OrdinalIgnoreCase))
            {
                var label = kind == FlowKind.Init ? "Init" : kind == FlowKind.Testrun ? "Testrun" : "Scheduled";
                Logger.Error($"{label} validation failed: update_plugins must be 'yes' or 'no' but is '{RunState.UpdatePluginsFlag}'");
                CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, $"{label} validation failed: invalid update_plugins ({RunState.UpdatePluginsFlag})", RunState.StartScript);
                return false;
            }

            var rdWin = Path.Combine(RunState.ServerDir, "RustDedicated.exe");
            var rdUnix = Path.Combine(RunState.ServerDir, "RustDedicated");
            if (!File.Exists(rdWin) && !File.Exists(rdUnix))
            {
                var label = kind == FlowKind.Init ? "Init" : kind == FlowKind.Testrun ? "Testrun" : "Scheduled";
                Logger.Error($"{label} validation failed: RustDedicated executable not found in server_dir '{RunState.ServerDir}'");
                CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, $"{label} validation failed: RustDedicated missing", RunState.StartScript);
                return false;
            }

            if (kind == FlowKind.Init || kind == FlowKind.Testrun || IsWhatServerOrBoth())
            {
                if (string.IsNullOrEmpty(RunState.SteamCmd) || !(File.Exists(RunState.SteamCmd)))
                {
                    var label = kind == FlowKind.Init ? "Init" : kind == FlowKind.Testrun ? "Testrun" : "Scheduled";
                    Logger.Error($"{label} validation failed: steamcmd not found at '{RunState.SteamCmd}'");
                    CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, $"{label} validation failed: steamcmd missing", RunState.StartScript);
                    return false;
                }
            }

            if (!RunState.IsService)
            {
                if (string.IsNullOrEmpty(RunState.StartScript) || !File.Exists(RunState.StartScript))
                {
                    Logger.Error($"INIT validation failed: start_script not found at '{RunState.StartScript}'");
                    CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Init validation failed: start_script missing", RunState.StartScript);
                    return false;
                }
            }
            else
            {
                if (string.IsNullOrEmpty(RunState.ServiceName))
                {
                    Logger.Error("INIT validation failed: serviceName not passed to executable");
                    CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Init validation failed: serviceName missing", RunState.StartScript);
                    return false;
                }
            }

            if (kind == FlowKind.Init)
            {
                if (!string.Equals(RunState.RemoteBuild, "no", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(RunState.RemoteOxide, "no", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error($"INIT validation failed: remote_build and remote_oxide must be 'no' (got remote_build='{RunState.RemoteBuild}' remote_oxide='{RunState.RemoteOxide}')");
                    CreateUpdateresultAndStartScript(RunState.ServerDir, RunState.UpdateId, "Init validation failed: remote args must be 'no'", RunState.StartScript);
                    return false;
                }
            }

            return true;
        }

    #endregion

    #region Helpers and utilities

        // Return true if server update requested or both.
        private static bool IsWhatServerOrBoth() =>
            string.Equals(RunState.What, "both", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(RunState.What, "server", StringComparison.OrdinalIgnoreCase);

        // Return true if oxide update requested or both.
        private static bool IsWhatOxideOrBoth() =>
            string.Equals(RunState.What, "both", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(RunState.What, "oxide", StringComparison.OrdinalIgnoreCase);

        // Run server update and restore on failure.
        private static bool RunUpdateServerWithRestoreOnFail(FlowKind kind, string backupPath)
        {
            Logger.Info($"{kind.ToString().ToUpper()}: Starting server update...");
            var ok = UpdateServer();
            if (!ok)
            {
                Logger.Error($"{kind.ToString().ToUpper()}: UpdateServer failed.");
                Fails = "server update";
                Logger.Info($"{kind.ToString().ToUpper()}: Calling restore due to server update failure...");
                RestoreFromBackupOrFail(RunState.ServerDir, backupPath, RunState.UpdateId, RunState.StartScript);
                Logger.Close();
                return false;
            }
            Logger.Info($"{kind.ToString().ToUpper()}: UpdateServer succeeded.");
            return true;
        }

        // Run oxide update and restore on failure.
        private static bool RunUpdateOxideWithRestoreOnFail(FlowKind kind, string backupPath)
        {
            Logger.Info($"{kind.ToString().ToUpper()}: Starting Oxide update...");
            var ok = UpdateOxide();
            if (!ok)
            {
                Logger.Error($"{kind.ToString().ToUpper()}: UpdateOxide failed.");
                Fails = "oxide update";
                Logger.Info($"{kind.ToString().ToUpper()}: Calling restore due to oxide update failure...");
                RestoreFromBackupOrFail(RunState.ServerDir, backupPath, RunState.UpdateId, RunState.StartScript);
                Logger.Close();
                return false;
            }
            Logger.Info($"{kind.ToString().ToUpper()}: UpdateOxide succeeded.");
            return true;
        }

        // Split a flag token "-key=val" into key and value.
        private static (string key, string value) SplitFlagToken(string token)
        {
            var t = token.TrimStart('-');
            var idx = t.IndexOf('=');
            if (idx >= 0)
            {
                var key = t.Substring(0, idx);
                var val = t.Substring(idx + 1);
                return (key, val);
            }
            return (t, "");
        }

        // Parse command-line key/value tokens into dictionary.
        private static Dictionary<string, string> ParseKeyValueArgs(string[] tokens)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tokens.Length; i++)
            {
                var tok = tokens[i];
                if (!tok.StartsWith("-")) continue;
                var (key, valFromToken) = SplitFlagToken(tok);
                string value;
                if (!string.IsNullOrEmpty(valFromToken))
                {
                    value = valFromToken;
                }
                else
                {
                    if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("-"))
                    {
                        value = tokens[i + 1];
                        i++;
                    }
                    else
                    {
                        value = "";
                    }
                }
                d[key.ToLowerInvariant()] = value;
            }
            return d;
        }

        // Parse flexible boolean argument forms.
        private static bool ParseBool(string? s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (bool.TryParse(s, out var b)) return b;
            if (s == "1") return true;
            if (s == "0") return false;
            return false;
        }

        // Wait for Rust server (process or service) to stop and run autoupdate task.
        private static void WaitForRustDedicatedAndLog()
        {
            if (!RunState.IsService)
            {
                var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                var procLabel = isWin ? "RustDedicated.exe" : "RustDedicated";
                Logger.Info($"Waiting for {procLabel} to stop...");
                while (IsProcessRunning("RustDedicated"))
                {
                    Thread.Sleep(2000);
                }
                Logger.Info($"{procLabel} stopped.");
            }
            else
            {
                Logger.Info($"Waiting for service {RunState.ServiceName} to stop...");
                while (IsServiceRunning(RunState.ServiceName))
                {
                    Thread.Sleep(2000);
                }
                Logger.Info($"{RunState.ServiceName} service stopped.");
            }
            bool result = AutoUpdateTask();
            if (result)
            {
                Logger.Info("Autoupdate process completed. System updated.");
            }
            else
            {
                Logger.Info("Autoupdate process completed.");
            }
        }

    #endregion

    #region Auto-update and remote release helpers

        // Perform plugin self-update (FMU) if a newer release exists.
        private static bool AutoUpdateTask()
        {
            string pFile = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                pFile = RunState.ServerDir + @"\oxide\plugins\FeedMeUpdates.cs";
            }
            else
            {
                pFile = RunState.ServerDir + "/oxide/plugins/FeedMeUpdates.cs";
            }
            var meta = ParseInfoAttribute(pFile);
            if (meta == null)
            {
                Logger.Warn($"Unable to get local FMU version from plugin file.");
                return false;
            }

            FMU_VERSION = meta.Version;

            Logger.Info("Current FeedMeUpdates version is: " + FMU_VERSION);
            Logger.Info("Scanning Github repository for newer version.");
            var (rTag, dUrl) = GetRemoteFMURelease();
            if (rTag != "no" && dUrl != "")
            {
                int comp = CompareVersionStrings(rTag, FMU_VERSION);
                if (comp <= 0)
                {
                    Logger.Info("No FMU updates required.");
                    return true;
                }
                else
                {
                    string extractRoot = Path.Combine(Path.GetTempPath(), "FMU_extracted_" + Guid.NewGuid().ToString("N"));
                    string tempZip = Path.Combine(Path.GetTempPath(), "FMU_update_" + Guid.NewGuid().ToString("N") + ".zip");
                    try
                    {
                        Logger.Info("A newer version of FMU has been found. Proceeding with update.");
                        Logger.Info($"Downloading FMU to temporary file: {tempZip}");

                        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                        {
                            http.DefaultRequestHeaders.UserAgent.ParseAdd("FeedMeUpdates/1.0");
                            HttpResponseMessage resp;
                            try
                            {
                                resp = http.GetAsync(dUrl).GetAwaiter().GetResult();
                                resp.EnsureSuccessStatusCode();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Download failed: {ex.Message}");
                                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                                return false;
                            }

                            try
                            {
                                using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error writing temp file: {ex.Message}");
                                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                                return false;
                            }
                        }

                        var fi = new FileInfo(tempZip);
                        if (!fi.Exists || fi.Length == 0)
                        {
                            Logger.Error("Downloaded file is missing or zero-length.");
                            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                            return false;
                        }

                        Directory.CreateDirectory(extractRoot);
                        Logger.Info($"Extracting '{tempZip}' to '{extractRoot}'");
                        try
                        {
                            ZipFile.ExtractToDirectory(tempZip, extractRoot, overwriteFiles: true);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Extraction failed: {ex.Message}");
                            try { if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true); } catch { }
                            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                            return false;
                        }

                        var origin = FindExtractOrigin(extractRoot) ?? extractRoot;
                        Logger.Info($"Using origin folder: {origin}");

                        try
                        {
                            foreach(string file in Directory.GetFiles(origin, "*", SearchOption.AllDirectories))
                            {
                                if(file.EndsWith("FeedMeUpdates.cs"))
                                {
                                    File.Delete(pFile);
                                    File.Move(file, pFile);
                                }
                                if(file.EndsWith("FeedMeUpdates") || file.EndsWith("FeedMeUpdates.exe"))
                                {
                                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                    {
                                        File.Delete(RunState.ServerDir + @"\FeedMeUpdates.exe.new");
                                        File.Move(file, RunState.ServerDir + @"\FeedMeUpdates.exe.new");
                                    }
                                    else
                                    {
                                        File.Delete(RunState.ServerDir + "/FeedMeUpdates.new");
                                        File.Move(file, RunState.ServerDir + "/FeedMeUpdates.new");
                                    }
                                }
                                if (file.EndsWith("FMU_CHECKSUMS.txt"))
                                {
                                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                    {
                                        File.Delete(RunState.ServerDir + @"\FMU_CHECKSUMS.txt");
                                        File.Move(file, RunState.ServerDir + @"\FMU_CHECKSUMS.txt");
                                    }
                                    else
                                    {
                                        File.Delete(RunState.ServerDir + "/FMU_CHECKSUMS.txt");
                                        File.Move(file, RunState.ServerDir + "/FMU_CHECKSUMS.txt");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error copying FMU files: {ex.Message}");
                            try { if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true); } catch { }
                            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                            return false;
                        }

                        try { if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true); } catch { }
                        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }

                        Logger.Info("FMU plugin successfully updated, .new file created and checksum file created.");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Unexpected exception: {ex.Message}");
                        try { if (!string.IsNullOrEmpty(extractRoot) && Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true); } catch { }
                        try { if (!string.IsNullOrEmpty(tempZip) && File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                        return false;
                    }

                }
            }
            return false;
        }

        // Detect if running against shared framework or self-contained.
        private static bool IsFrameworkDependent()
        {
            var fxDepsFile = AppContext.GetData("FX_DEPS_FILE") as string;
            var fxDepsFolder = AppContext.GetData("FX_DEPS_FOLDER") as string;

            if (!string.IsNullOrEmpty(fxDepsFile) || !string.IsNullOrEmpty(fxDepsFolder))
                return true;

            var appContextDeps = AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string;

            return false;
        }

        // Query GitHub releases for FMU and pick suitable asset URL.
        private static (string tag, string url) GetRemoteFMURelease()
        {
            try
            {
                var api = "https://api.github.com/repos/frankie290651/FeedMeUpdates/releases/latest";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("FeedMeUpdates/1.0");
                var resp = http.GetAsync(api).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    Logger.Warn($"GetRemoteFMURelease: GitHub API returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    return ("no", "");
                }

                var content = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                string tag = "";
                if (root.TryGetProperty("tag_name", out var tagElem) && tagElem.ValueKind == JsonValueKind.String)
                {
                    tag = tagElem.GetString() ?? "";
                }
                else if (root.TryGetProperty("name", out var nameElem) && nameElem.ValueKind == JsonValueKind.String)
                {
                    tag = nameElem.GetString() ?? "";
                }

                string chosenUrl = "";
                var assets = new List<(string name, string url)>();
                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assetsElem.EnumerateArray())
                    {
                        try
                        {
                            string name = a.GetProperty("name").GetString() ?? "";
                            string url = a.GetProperty("browser_download_url").GetString() ?? "";
                            if (!string.IsNullOrEmpty(url))
                                assets.Add((name.ToLowerInvariant(), url));
                        }
                        catch { }
                    }
                }

                bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                isStandalone = !IsFrameworkDependent();

                if (assets.Count > 0)
                {

                    foreach (var a in assets)
                    {
                        if(a.name.Contains("win"))
                        {
                            if(a.name.Contains("standalone"))
                            {
                                if(isWindows && isStandalone)
                                {
                                    chosenUrl = a.url;
                                    break;
                                }
                            }
                            else
                            {
                                if (isWindows && !isStandalone)
                                {
                                    chosenUrl = a.url;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            if (a.name.Contains("standalone"))
                            {
                                if (!isWindows && isStandalone)
                                {
                                    chosenUrl = a.url;
                                    break;
                                }
                            }
                            else
                            {
                                if (!isWindows && !isStandalone)
                                {
                                    chosenUrl = a.url;
                                    break;
                                }
                            }
                        }
                    }
                    Logger.Info($"GetRemoteFMURelease: tag='{tag}' url='{chosenUrl}'");
                    return (tag, chosenUrl);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"GetRemoteFMURelease: exception: {ex.Message}");
                return ("no", "");
            }
            return ("no", "");
        }

    #endregion

    #region Process and system helpers

        // Check if a service is running.
        private static bool IsServiceRunning(string srvName)
        {
            if (string.IsNullOrWhiteSpace(srvName))
                return false;

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "sc",
                        Arguments = $"query \"{srvName}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using var p = Process.Start(psi);
                    if (p == null) return false;
                    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(4000);

                    var m = Regex.Match(output, @"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int code))
                    {
                        return code == 4 || code == 2 || code == 3;
                    }

                    return output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                else
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "systemctl",
                        Arguments = $"is-active --quiet {EscapeArg(srvName)}",
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p == null) return false;
                    p.WaitForExit(4000);
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }

            static string EscapeArg(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;
                if (s.IndexOf(' ') >= 0 || s.IndexOf('"') >= 0)
                    return $"'{s.Replace("'", "'\"'\"'")}'";
                return s;
            }
        }

        // Check if a process with the specified name is running.
        private static bool IsProcessRunning(string procName)
        {
            try
            {
                var procs = Process.GetProcessesByName(procName);
                return procs != null && procs.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        // Start the server start script using platform-aware methods.
        private static void StartServerScript(string scriptPath, string workingDirectory)
        {
            Logger.Info($"[StartServerScript] Enter: scriptPath='{scriptPath}' workDir='{workingDirectory}' ShowServerConsole={RunState.ShowServerConsole} IsService={RunState.IsService} ServerTmux='{RunState.ServerTmux}'");

            if (string.IsNullOrWhiteSpace(scriptPath))
                throw new ArgumentException("start script is empty");

            if (!Path.IsPathRooted(scriptPath))
                scriptPath = Path.Combine(workingDirectory, scriptPath);

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("Start script not found", scriptPath);

            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            try
            {
                if (isWindows)
                {
                    var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
                    ProcessStartInfo psi;

                    if (RunState.ShowServerConsole)
                    {
                        string launcher;
                        string args;
                        if (ext == ".ps1")
                        {
                            launcher = "powershell.exe";
                            args = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
                        }
                        else
                        {
                            launcher = "cmd.exe";
                            args = $"/c start \"RustServer\" \"{scriptPath}\"";
                        }

                        psi = new ProcessStartInfo
                        {
                            FileName = launcher,
                            Arguments = args,
                            UseShellExecute = true,
                            CreateNoWindow = false,
                            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? workingDirectory
                        };
                    }
                    else
                    {
                        string launcher;
                        string args;
                        if (ext == ".ps1")
                        {
                            launcher = "powershell.exe";
                            args = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
                        }
                        else
                        {
                            launcher = "cmd.exe";
                            args = $"/c \"{scriptPath}\"";
                        }

                        psi = new ProcessStartInfo
                        {
                            FileName = launcher,
                            Arguments = args,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = false,
                            RedirectStandardError = false,
                            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? workingDirectory
                        };
                    }

                    Logger.Info($"[StartServerScript] Windows launch: {psi.FileName} {psi.Arguments} (UseShellExecute={psi.UseShellExecute}, CreateNoWindow={psi.CreateNoWindow})");
                    var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        Logger.Error("[StartServerScript] Failed to start process (returned null)");
                    }
                    else
                    {
                        Logger.Info($"[StartServerScript] Started. PID={proc.Id}");
                    }
                    return;
                }

                try
                {
                    var chmod = new ProcessStartInfo
                    {
                        FileName = "/bin/chmod",
                        Arguments = $"+x \"{scriptPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        CreateNoWindow = true
                    };
                    Process.Start(chmod)?.WaitForExit(2000);
                }
                catch { }

                bool[] available = DetectGnomeAndTmux();
                bool gnomeAvailable = available[0];
                bool tmuxAvailable = available[1];
                Logger.Info($"[StartServerScript] Linux: gnomeAvailable={gnomeAvailable} tmuxAvailable={tmuxAvailable}");

                if (!RunState.ShowServerConsole)
                {
                    StartWithNoHup(scriptPath, workingDirectory);
                    return;
                }

                if (!string.IsNullOrEmpty(RunState.ServerTmux) && tmuxAvailable)
                {
                    if (!SafeLaunchTmuxSession(scriptPath, RunState.ServerTmux))
                    {
                        Logger.Warn("[StartServerScript] tmux specific session failed, proceeding to next method...");
                    }
                    else
                    {
                        return;
                    }
                }

                if (gnomeAvailable)
                {
                    if (!SafeLaunchGnomeTerminal(scriptPath))
                    {
                        Logger.Warn("[StartServerScript] gnome-terminal failed, proceeding to next method...");
                    }
                    else
                    {
                        return;
                    }
                }

                if (tmuxAvailable)
                {
                    var defaultSession = string.IsNullOrEmpty(RunState.ServerTmux) ? "rust-server" : RunState.ServerTmux;
                    if (!SafeLaunchTmuxSession(scriptPath, defaultSession))
                    {
                        Logger.Warn("[StartServerScript] tmux default session failed, proceeding to fallback nohup...");
                    }
                    else
                    {
                        return;
                    }
                }

                StartWithNoHup(scriptPath, workingDirectory);
            }
            catch (Exception ex)
            {
                throw new Exception($"StartServerScript error: {ex.Message}", ex);
            }

            void StartWithNoHup(string sp, string wd)
            {
                var bashArgs = $"-c \"nohup '{sp}' >/dev/null 2>&1 &\"";
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = bashArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                    WorkingDirectory = wd
                };
                Logger.Info($"[StartServerScript] nohup launch: {psi.FileName} {psi.Arguments}");
                var p = Process.Start(psi);
                if (p == null) Logger.Error("[StartServerScript] nohup failed (null process).");
            }

            bool SafeLaunchGnomeTerminal(string sp)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "gnome-terminal",
                        Arguments = $"-- bash -lc \"exec '{sp}'\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        CreateNoWindow = false
                    };
                    Logger.Info($"[StartServerScript] gnome-terminal launch: {psi.FileName} {psi.Arguments}");
                    var p = Process.Start(psi);
                    if (p == null) { Logger.Error("[StartServerScript] gnome-terminal returned null."); return false; }
                    Thread.Sleep(500);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[StartServerScript] gnome-terminal exception: {ex.Message}");
                    return false;
                }
            }

            bool SafeLaunchTmuxSession(string sp, string session)
            {
                try
                {
                    var killPsi = new ProcessStartInfo
                    {
                        FileName = "tmux",
                        Arguments = $"kill-session -t {session}",
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        CreateNoWindow = true
                    };
                    Process.Start(killPsi)?.WaitForExit(500);

                    var newPsi = new ProcessStartInfo
                    {
                        FileName = "tmux",
                        Arguments = $"new-session -d -s {session} \"bash -lc 'exec {sp}'\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        CreateNoWindow = true
                    };
                    Logger.Info($"[StartServerScript] tmux new-session: {newPsi.FileName} {newPsi.Arguments}");
                    var p = Process.Start(newPsi);
                    if (p == null) { Logger.Error("[StartServerScript] tmux new-session failed (null)."); return false; }
                    Thread.Sleep(300);
                    return TmuxSessionExists(session);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[StartServerScript] tmux exception: {ex.Message}");
                    return false;
                }
            }
        }

    #endregion

    #region Backup and restore

        // Create temporary backup of server folder excluding updater artifacts.
        private static bool CreateTempBackup(string serverDir, string update_id, out string backupPath)
        {
            backupPath = "";
            try
            {
                Logger.Info("Starting temp_backup creation...");

                var backupsRoot = Path.Combine(serverDir, "temp_backup");
                if (!Directory.Exists(backupsRoot))
                {
                    Directory.CreateDirectory(backupsRoot);
                    Logger.Info($"Created temp_backup directory: {backupsRoot}");
                }
                else
                {
                    Logger.Info($"temp_backup already exists: {backupsRoot} (files inside will be overwritten)");
                }

                var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                excludes.Add(Path.GetFullPath(backupsRoot).TrimEnd(Path.DirectorySeparatorChar));
                excludes.Add(Path.GetFullPath(Path.Combine(serverDir, "updater.log")));

                try
                {
                    var exeNames = new[] { "FeedMeUpdates.exe", "FeedMeUpdates" };
                    foreach (var nm in exeNames)
                    {
                        var p = Path.Combine(serverDir, nm);
                        excludes.Add(Path.GetFullPath(p));
                    }
                }
                catch { }

                var srcRoot = Path.GetFullPath(serverDir).TrimEnd(Path.DirectorySeparatorChar);
                var dstRoot = Path.GetFullPath(backupsRoot).TrimEnd(Path.DirectorySeparatorChar);

                foreach (var dir in Directory.GetDirectories(srcRoot, "*", SearchOption.AllDirectories))
                {
                    var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                    if (IsUnderAny(full, excludes)) continue;
                    var rel = Path.GetRelativePath(srcRoot, full);
                    var target = Path.Combine(dstRoot, rel);
                    try { Directory.CreateDirectory(target); } catch (Exception ex) { Logger.Warn($"Unable to create directory {target}: {ex.Message}"); }
                }

                foreach (var file in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    var full = Path.GetFullPath(file);
                    if (IsUnderAny(full, excludes)) continue;
                    var rel = Path.GetRelativePath(srcRoot, full);
                    var target = Path.Combine(dstRoot, rel);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? dstRoot);
                        File.Copy(full, target, overwrite: true);
                        Logger.Info($"Copied: {rel}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error copying '{full}' -> '{target}': {ex.Message}");
                        return false;
                    }
                }

                backupPath = backupsRoot;
                Logger.Info("temp_backup creation completed successfully.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"CreateTempBackup: general exception: {ex.Message}");
                return false;
            }
        }

        // Restore server files from temporary backup and write failure marker.
        private static bool RestoreFromBackupOrFail(string serverDir, string backupFolder, string update_id, string start_script)
        {
            try
            {
                Logger.Info($"Starting restore from backup: '{backupFolder}' -> '{serverDir}'");

                if (string.IsNullOrWhiteSpace(backupFolder) || !Directory.Exists(backupFolder))
                {
                    Logger.Error($"Restore: backupFolder invalid or missing: {backupFolder}");
                    HandleRestoreFailure(serverDir, update_id, start_script);
                    return false;
                }

                var srcRoot = Path.GetFullPath(backupFolder).TrimEnd(Path.DirectorySeparatorChar);
                var dstRoot = Path.GetFullPath(serverDir).TrimEnd(Path.DirectorySeparatorChar);

                foreach (var dir in Directory.GetDirectories(srcRoot, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(srcRoot, dir);
                    var targetDir = Path.Combine(dstRoot, rel);
                    try
                    {
                        if (!Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                            Logger.Info($"Created directory: {targetDir}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Restore: unable to create directory '{targetDir}': {ex.Message}");
                        HandleRestoreFailure(serverDir, update_id, start_script);
                        return false;
                    }
                }

                foreach (var file in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(srcRoot, file);
                    var target = Path.Combine(dstRoot, rel);
                    try
                    {
                        var parent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        if (File.Exists(target) && Path.GetFileName(target).ToLower().Contains("feedmeupdates") == false)
                        {
                            try
                            {
                                File.SetAttributes(target, FileAttributes.Normal);
                                File.Delete(target);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"Restore: unable to delete existing file '{target}': {ex.Message}");
                            }
                        }

                        File.Copy(file, target, overwrite: true);
                        Logger.Info($"Restored file: {rel}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Restore: error copying '{file}' -> '{target}': {ex.Message}");
                        HandleRestoreFailure(serverDir, update_id, start_script);
                        return false;
                    }
                }

                Logger.Info("Restore completed successfully.");

                try
                {
                    var resultPath = Path.Combine(serverDir, "updateresult.json");
                    var failReason = "Failed " + (string.IsNullOrEmpty(Fails) ? "" : Fails);
                    var payload = new Dictionary<string, object?>
                    {
                        ["result"] = "failed",
                        ["fail_reason"] = failReason,
                        ["backup_cycle"] = true,
                        ["server_restored"] = "yes",
                        ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        ["update_id"] = update_id
                    };
                    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(resultPath, json);
                    Logger.Info($"Restore: Wrote restore marker to {resultPath} with fail_reason='{failReason}'");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Restore: Error writing restore marker: {ex.Message}");
                }

                CleanTempBackup(serverDir);

                if (!string.IsNullOrWhiteSpace(start_script))
                {
                    try
                    {
                        Logger.Info($"Restore: Starting start_script: {start_script}");
                        StartServerScript(start_script, serverDir);
                        Logger.Info("Restore: start_script launched.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Restore: Failed to start start_script: {ex.Message}");
                    }
                }
                else
                {
                    Logger.Warn("Restore: start_script not specified; skipping start.");
                }

                Logger.Info("Restore: Flow complete. Exiting process after successful restore.");
                Logger.Close();
                Environment.Exit(0);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"RestoreFromBackupOrFail: exception: {ex.Message}");
                HandleRestoreFailure(serverDir, update_id, start_script);
                return false;
            }
        }

        // Handle failed restore attempts and write failure markers.
        private static void HandleRestoreFailure(string server_dir, string update_id, string start_script)
        {
            try
            {
                var resultPath = Path.Combine(server_dir, "updateresult.json");
                var failReason = "Failed restore after failed " + (string.IsNullOrEmpty(Fails) ? "" : Fails);
                var payload = new Dictionary<string, object?>
                {
                    ["result"] = "failed",
                    ["fail_reason"] = failReason,
                    ["backup_cycle"] = true,
                    ["server_restored"] = false,
                    ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["update_id"] = update_id
                };
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(resultPath, json);
                Logger.Info($"Created {resultPath} with fail_reason='{failReason}'");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error creating updateresult.json during failed restore: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(start_script))
            {
                try
                {
                    CleanTempBackup(server_dir);
                    StartServerScript(start_script, server_dir);
                    Logger.Info($"Started start_script: {start_script}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error starting start_script after failed restore: {ex.Message}");
                }
            }
            else
            {
                Logger.Warn("start_script not specified; skipping start after failed restore.");
            }

            Logger.Info("Program exits (restore failed).");
            Logger.Close();
            Environment.Exit(0);
        }

    #endregion

    #region Process execution helpers

        // Run a process and capture combined stdout/stderr.
        private static string RunProcessCaptureOutput(string exe, string args, TimeSpan timeout)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi)!;
                if (proc == null) return "";

                var output = proc.StandardOutput.ReadToEnd();
                var err = proc.StandardError.ReadToEnd();

                if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { proc.Kill(true); } catch { }
                    Logger.Warn($"Process '{exe}' timed out after {timeout.TotalSeconds}s");
                    return output + "\n" + err;
                }

                return (output + "\n" + err).Trim();
            }
            catch (Exception ex)
            {
                Logger.Error($"RunProcessCaptureOutput error running '{exe} {args}': {ex.Message}");
                return "";
            }
        }

        // Run a process and return its exit code while logging output.
        private static int RunProcessCaptureExitCode(string exe, string args, TimeSpan timeout)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi)!;
                if (proc == null) return -1;

                proc.OutputDataReceived += (s, e) => { if (e.Data != null) Logger.Info(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Logger.Warn(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { proc.Kill(true); } catch { }
                    Logger.Warn($"Process '{exe}' did not exit within timeout {timeout.TotalSeconds}s and was killed.");
                    return -1;
                }

                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                Logger.Error($"RunProcessCaptureExitCode error running '{exe} {args}': {ex.Message}");
                return -2;
            }
        }

    #endregion

    #region Remote queries (SteamCMD and Oxide)

        // Retrieve remote Rust build id via steamcmd output heuristics.
        private static string GetRemoteRustBuild()
        {
            try
            {
                var steamCmd = RunState.SteamCmd;
                if (string.IsNullOrWhiteSpace(steamCmd) || !File.Exists(steamCmd))
                {
                    Logger.Warn($"GetRemoteRustBuild: steamcmd path not set or not found: '{steamCmd}'");
                    return "no";
                }

                Logger.Info("Querying SteamCMD for Rust app info to determine remote build id...");
                var args = $"+login anonymous +app_info_print 258550 +quit";
                var outp = RunProcessCaptureOutput(steamCmd, args, TimeSpan.FromSeconds(30));
                if (string.IsNullOrEmpty(outp))
                {
                    Logger.Warn("GetRemoteRustBuild: steamcmd returned no output.");
                    return "no";
                }

                try
                {
                    var publicBuildMatch = Regex.Match(outp, @"public\s*\{[\s\S]*?""buildid""\s*""([0-9]{5,12})""", RegexOptions.IgnoreCase);
                    if (publicBuildMatch.Success && publicBuildMatch.Groups.Count >= 2)
                    {
                        var build = publicBuildMatch.Groups[1].Value;
                        Logger.Info($"GetRemoteRustBuild: detected build id in branches->public: {build}");
                        return build;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"GetRemoteRustBuild: error while parsing public buildid: {ex.Message}");
                }

                try
                {
                    var matches = Regex.Matches(outp, @"""buildid""\s*""([0-9]{5,12})""", RegexOptions.IgnoreCase);
                    if (matches.Count > 0)
                    {
                        for (int i = 0; i < matches.Count; i++)
                        {
                            var idx = matches[i].Index;
                            var windowStart = Math.Max(0, idx - 200);
                            var len = Math.Min(200, outp.Length - windowStart);
                            var ctx = outp.Substring(windowStart, len);
                            if (ctx.IndexOf("branches", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var cand = matches[i].Groups[1].Value;
                                Logger.Info($"GetRemoteRustBuild: detected build id (fallback, near 'branches'): {cand}");
                                return cand;
                            }
                        }

                        var first = matches[0].Groups[1].Value;
                        Logger.Info($"GetRemoteRustBuild: detected build id (fallback any buildid): {first}");
                        return first;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"GetRemoteRustBuild: fallback parsing error: {ex.Message}");
                }

                try
                {
                    var anyNum = Regex.Matches(outp, @"\b([0-9]{6,10})\b");
                    if (anyNum.Count > 0)
                    {
                        for (int i = 0; i < anyNum.Count; i++)
                        {
                            var idx = anyNum[i].Index;
                            var windowStart = Math.Max(0, idx - 200);
                            var len = Math.Min(200, outp.Length - windowStart);
                            var ctx = outp.Substring(windowStart, len).ToLowerInvariant();
                            if (ctx.Contains("buildid") || ctx.Contains("branches") || ctx.Contains("release"))
                            {
                                var cand = anyNum[i].Groups[1].Value;
                                Logger.Info($"GetRemoteRustBuild: detected build id (last-resort heuristic): {cand}");
                                return cand;
                            }
                        }

                        var f = anyNum[0].Groups[1].Value;
                        Logger.Info($"GetRemoteRustBuild: detected build id (last-resort numeric): {f}");
                        return f;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"GetRemoteRustBuild: final numeric fallback error: {ex.Message}");
                }

                Logger.Warn("GetRemoteRustBuild: could not determine build id from steamcmd output.");
                return "no";
            }
            catch (Exception ex)
            {
                Logger.Error($"GetRemoteRustBuild: exception: {ex.Message}");
                return "no";
            }
        }

        // Fetch latest Oxide release tag and select an asset URL.
        private static (string tag, string url) GetRemoteOxideRelease()
        {
            try
            {
                var api = "https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest";
                Logger.Info($"Querying GitHub API for Oxide.Rust latest release: {api}");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("FeedMeUpdates/1.0");
                var resp = http.GetAsync(api).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    Logger.Warn($"GetRemoteOxideRelease: GitHub API returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    return ("no", "");
                }

                var content = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                string tag = "";
                if (root.TryGetProperty("tag_name", out var tagElem) && tagElem.ValueKind == JsonValueKind.String)
                {
                    tag = tagElem.GetString() ?? "";
                }
                else if (root.TryGetProperty("name", out var nameElem) && nameElem.ValueKind == JsonValueKind.String)
                {
                    tag = nameElem.GetString() ?? "";
                }

                string chosenUrl = "";
                var assets = new List<(string name, string url)>();
                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assetsElem.EnumerateArray())
                    {
                        try
                        {
                            string name = a.GetProperty("name").GetString() ?? "";
                            string url = a.GetProperty("browser_download_url").GetString() ?? "";
                            if (!string.IsNullOrEmpty(url))
                                assets.Add((name.ToLowerInvariant(), url));
                        }
                        catch { }
                    }
                }

                bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

                if (assets.Count > 0)
                {
                    (string name, string url)? firstZip = null;
                    (string name, string url)? firstNonLinuxZip = null;

                    foreach (var a in assets)
                    {
                        if (firstZip == null && (a.url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                        {
                            firstZip = (a.name, a.url);
                        }

                        if (firstNonLinuxZip == null &&
                            (a.url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) &&
                            a.name.IndexOf("linux", StringComparison.OrdinalIgnoreCase) < 0 &&
                            a.name.IndexOf("mac", StringComparison.OrdinalIgnoreCase) < 0 &&
                            a.name.IndexOf("darwin", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            firstNonLinuxZip = (a.name, a.url);
                        }

                        if (isWindows)
                        {
                            if (a.name.Contains("windows") && !a.name.Contains("linux") && !a.name.Contains("mac"))
                            {
                                chosenUrl = a.url; break;
                            }
                            if (a.name.Contains("win") && !a.name.Contains("linux") && !a.name.Contains("mac") && string.IsNullOrEmpty(chosenUrl))
                            {
                                chosenUrl = a.url;
                            }
                        }
                        else
                        {
                            if (a.name.Contains("linux") || a.name.Contains("ubuntu") || a.name.Contains("x86_64") || a.name.Contains("x64"))
                            {
                                chosenUrl = a.url; break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(chosenUrl) && isWindows && firstNonLinuxZip != null)
                    {
                        chosenUrl = firstNonLinuxZip.Value.url;
                    }

                    if (string.IsNullOrEmpty(chosenUrl) && firstZip != null)
                        chosenUrl = firstZip.Value.url;

                    if (string.IsNullOrEmpty(chosenUrl) && assets.Count > 0)
                        chosenUrl = assets[0].url;
                }

                if (string.IsNullOrEmpty(tag)) tag = "no";
                if (string.IsNullOrEmpty(chosenUrl)) chosenUrl = "";

                Logger.Info($"GetRemoteOxideRelease: tag='{tag}' url='{chosenUrl}'");
                return (tag, chosenUrl);
            }
            catch (Exception ex)
            {
                Logger.Error($"GetRemoteOxideRelease: exception: {ex.Message}");
                return ("no", "");
            }
        }

    #endregion

    #region Update operations

        // Update Rust server via steamcmd with retries and error heuristics.
        private static bool UpdateServer()
        {
            try
            {
                Logger.Info("[UpdateServer] invoked.");
                var steamCmd = RunState.SteamCmd;
                var serverDir = RunState.ServerDir;

                if (string.IsNullOrWhiteSpace(steamCmd))
                {
                    Logger.Error("[UpdateServer] steamcmd path is empty.");
                    return false;
                }

                if (!File.Exists(steamCmd))
                {
                    Logger.Error($"[UpdateServer] steamcmd not found at '{steamCmd}'.");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(serverDir))
                {
                    Logger.Error("[UpdateServer] server_dir is empty.");
                    return false;
                }

                Logger.Info($"[UpdateServer] Running steamcmd to update/install Rust server into '{serverDir}'");

                try
                {
                    Directory.CreateDirectory(serverDir);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[UpdateServer] Cannot create server_dir '{serverDir}': {ex.Message}");
                    return false;
                }

                var args = $"+force_install_dir \"{serverDir}\" +login anonymous +app_update 258550 validate +quit";

                int maxAttempts = 3;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    Logger.Info($"[UpdateServer] Attempt {attempt}/{maxAttempts}: running steamcmd with args: {args}");

                    var psi = new ProcessStartInfo
                    {
                        FileName = steamCmd,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(steamCmd) ?? serverDir
                    };

                    string outp;
                    try
                    {
                        using var proc = Process.Start(psi);
                        if (proc == null)
                        {
                            Logger.Error("[UpdateServer] Failed to start steamcmd process.");
                            return false;
                        }

                        var stdOut = proc.StandardOutput.ReadToEnd();
                        var stdErr = proc.StandardError.ReadToEnd();

                        if (!proc.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
                        {
                            try { proc.Kill(true); } catch { }
                            Logger.Warn($"[UpdateServer] steamcmd timed out and was killed.");
                        }

                        outp = (stdOut + "\n" + stdErr).Trim();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[UpdateServer] Exception running steamcmd: {ex.Message}");
                        outp = ex.ToString();
                    }

                    if (string.IsNullOrEmpty(outp)) outp = "";

                    try
                    {
                        var lines = outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        var tailLines = lines.Length <= 200 ? lines : lines.Skip(Math.Max(0, lines.Length - 200)).ToArray();
                        var tail = string.Join("\n", tailLines);
                        Logger.Info($"[UpdateServer] steamcmd output (tail):\n{tail}");
                    }
                    catch { Logger.Info("[UpdateServer] steamcmd output logged."); }

                    if (outp.IndexOf("Success! App '258550'", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        outp.IndexOf("Success! app '258550'", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (outp.IndexOf("installed", StringComparison.OrdinalIgnoreCase) >= 0 && outp.IndexOf("258550", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        Logger.Info("[UpdateServer] Completed successfully (detected success string).");
                        return true;
                    }

                    var sawForceInstallWarning = outp.IndexOf("Please use force_install_dir before logon", StringComparison.OrdinalIgnoreCase) >= 0;
                    var sawState0x6 = outp.IndexOf("state is 0x6", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      outp.IndexOf("Error! App '258550' state is 0x6", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (sawForceInstallWarning)
                    {
                        Logger.Warn("[UpdateServer] steamcmd warned about force_install_dir before logon. Arguments already correct; will retry.");
                    }

                    if (sawState0x6)
                    {
                        Logger.Warn("[UpdateServer] Detected state 0x6 (possible appmanifest issue). Attempting remedial actions before retry.");
                        try
                        {
                            var appmanifest = Path.Combine(serverDir, "steamapps", "appmanifest_258550.acf");
                            if (File.Exists(appmanifest))
                            {
                                try
                                {
                                    var bak = appmanifest + ".bak";
                                    Logger.Info($"[UpdateServer] Renaming appmanifest to {bak} to allow recreation.");
                                    if (File.Exists(bak))
                                    {
                                        try { File.Delete(bak); Logger.Info("[UpdateServer] Existing backup appmanifest deleted."); } catch { }
                                    }
                                    File.Move(appmanifest, bak);
                                    Logger.Info("[UpdateServer] appmanifest renamed successfully.");
                                }
                                catch (IOException ioex)
                                {
                                    Logger.Warn($"[UpdateServer] Could not rename appmanifest (likely locked): {ioex.Message}. Will wait and retry.");
                                }
                                catch (Exception ex2)
                                {
                                    Logger.Warn($"[UpdateServer] Unexpected error renaming appmanifest: {ex2.Message}");
                                }
                            }
                            else
                            {
                                Logger.Info("[UpdateServer] appmanifest not present; nothing to rename.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[UpdateServer] Error during manifest rename: {ex.Message}");
                        }
                    }

                    if (attempt == maxAttempts)
                    {
                        Logger.Error("[UpdateServer] steamcmd failed after retries. Last output:");
                        try
                        {
                            var lines = outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            var excerptLines = lines.Length <= 200 ? lines : lines.Skip(Math.Max(0, lines.Length - 200)).ToArray();
                            var excerpt = string.Join("\n", excerptLines);
                            Logger.Error(excerpt);
                        }
                        catch { Logger.Error("[UpdateServer] (failed to extract last output)"); }
                        return false;
                    }

                    var delayMs = 3000 * attempt;
                    Logger.Info($"[UpdateServer] Waiting {delayMs}ms before retry...");
                    Thread.Sleep(delayMs);
                }

                Logger.Error("[UpdateServer] Unexpected exit path.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"[UpdateServer] Unexpected exception: {ex.Message}");
                return false;
            }
        }

        // Update Oxide by downloading and copying release files.
        private static bool UpdateOxide()
        {
            string tempZip = "";
            string extractRoot = "";
            try
            {
                Logger.Info("[UpdateOxide] invoked.");
                var serverDir = RunState.ServerDir;
                if (string.IsNullOrWhiteSpace(serverDir))
                {
                    Logger.Error("[UpdateOxide] server_dir is empty.");
                    return false;
                }

                var (tag, url) = GetRemoteOxideRelease();
                if (tag == "no" || string.IsNullOrEmpty(url))
                {
                    Logger.Error("[UpdateOxide] Could not determine remote Oxide release or download URL.");
                    return false;
                }

                Logger.Info($"[UpdateOxide] Latest Oxide release tag: {tag}");
                Logger.Info($"[UpdateOxide] Download URL chosen: {url}");

                tempZip = Path.Combine(Path.GetTempPath(), "oxide_update_" + Guid.NewGuid().ToString("N") + ".zip");
                Logger.Info($"[UpdateOxide] Downloading Oxide to temporary file: {tempZip}");

                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("FeedMeUpdates/1.0");
                    HttpResponseMessage resp;
                    try
                    {
                        resp = http.GetAsync(url).GetAwaiter().GetResult();
                        resp.EnsureSuccessStatusCode();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[UpdateOxide] Download failed: {ex.Message}");
                        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                        return false;
                    }

                    try
                    {
                        using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                        resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[UpdateOxide] Error writing temp file: {ex.Message}");
                        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                        return false;
                    }
                }

                var fi = new FileInfo(tempZip);
                if (!fi.Exists || fi.Length == 0)
                {
                    Logger.Error("[UpdateOxide] Downloaded file is missing or zero-length.");
                    try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                    return false;
                }

                extractRoot = Path.Combine(Path.GetTempPath(), "oxide_extracted_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(extractRoot);
                Logger.Info($"[UpdateOxide] Extracting '{tempZip}' to '{extractRoot}'");
                try
                {
                    ZipFile.ExtractToDirectory(tempZip, extractRoot, overwriteFiles: true);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[UpdateOxide] Extraction failed: {ex.Message}");
                    try { if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true); } catch { }
                    try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                    return false;
                }

                var origin = FindExtractOrigin(extractRoot) ?? extractRoot;
                Logger.Info($"[UpdateOxide] Using origin folder: {origin}");

                try
                {
                    var candidate1 = Path.Combine(origin, "RustDedicated_Data", "Managed");
                    var candidate2 = Path.Combine(origin, "Managed");

                    if (Directory.Exists(candidate1))
                    {
                        var dst = Path.Combine(serverDir, "RustDedicated_Data", "Managed");
                        Logger.Info($"[UpdateOxide] Copying Managed from '{candidate1}' -> '{dst}'");
                        CopyDirectoryOverwrite(candidate1, dst);
                    }
                    else if (Directory.Exists(candidate2))
                    {
                        var dst = Path.Combine(serverDir, "RustDedicated_Data", "Managed");
                        Logger.Info($"[UpdateOxide] Copying Managed from '{candidate2}' -> '{dst}'");
                        CopyDirectoryOverwrite(candidate2, dst);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[UpdateOxide] Error copying Managed assemblies: {ex.Message}");
                    try { if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true); } catch { }
                    try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                    return false;
                }

                try
                {
                    var srcOxide = Path.Combine(origin, "Oxide");
                    if (Directory.Exists(srcOxide))
                    {
                        var dstOxide = Path.Combine(serverDir, "oxide");
                        Logger.Info($"[UpdateOxide] Copying Oxide folder from '{srcOxide}' -> '{dstOxide}'");
                        CopyDirectoryOverwrite(srcOxide, dstOxide);
                    }
                    else
                    {
                        Logger.Info($"[UpdateOxide] No explicit 'Oxide' folder found; copying origin contents into server_dir '{serverDir}'");
                        CopyDirectoryOverwrite(origin, serverDir);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[UpdateOxide] Error copying Oxide files: {ex.Message}");
                    try { if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true); } catch { }
                    try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                    return false;
                }

                try { if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true); } catch { }
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }

                Logger.Info("[UpdateOxide] Completed successfully.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[UpdateOxide] Unexpected exception: {ex.Message}");
                try { if (!string.IsNullOrEmpty(extractRoot) && Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true); } catch { }
                try { if (!string.IsNullOrEmpty(tempZip) && File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                return false;
            }
        }

    #endregion

    #region Plugin update pass

        // Update installed uMod plugins by checking uMod API and replacing .cs files if newer.
        private static bool UpdatePlugins()
        {
            bool hadError = false;
            RunState.UpdatedPlugins = new List<string>();

            try
            {
                Logger.Info("[UpdatePlugins] Start");

                if (!string.Equals(RunState.UpdatePluginsFlag, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info("[UpdatePlugins] update_plugins flag is not 'yes'; skipping plugin update pass.");
                    return true;
                }

                var pluginsFolder = Path.Combine(RunState.ServerDir, "oxide", "plugins");
                if (!Directory.Exists(pluginsFolder))
                {
                    Logger.Info($"[UpdatePlugins] plugins folder not found: {pluginsFolder} -> nothing to do.");
                    return true;
                }

                var pluginFiles = Directory.GetFiles(pluginsFolder, "*.cs", SearchOption.TopDirectoryOnly);
                if (pluginFiles == null || pluginFiles.Length == 0)
                {
                    Logger.Info($"[UpdatePlugins] no .cs plugin files found in {pluginsFolder}");
                    return true;
                }

                Logger.Info($"[UpdatePlugins] Found {pluginFiles.Length} plugin(s) to check in {pluginsFolder}");

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("FeedMeUpdates/1.0");

                int requestsPerMinute = 30;
                foreach (var f in pluginFiles)
                {
                    if (!f.EndsWith("FeedMeUpdates.cs"))
                    {
                        try
                        {
                            Logger.Info($"[UpdatePlugins] Checking plugin file: {Path.GetFileName(f)}");
                            var meta = ParseInfoAttribute(f);
                            if (meta == null)
                            {
                                Logger.Warn($"[UpdatePlugins] Skipping {Path.GetFileName(f)}: [Info(...)] attribute not found or unparsable.");
                                continue;
                            }

                            var localTitle = meta.Title;
                            var localAuthor = meta.Author;
                            var localVersion = meta.Version;
                            var baseLocalName = Path.GetFileNameWithoutExtension(f);
                            var className = GetClassNameFromCs(f);

                            if (string.IsNullOrEmpty(localVersion))
                            {
                                Logger.Warn($"[UpdatePlugins] {Path.GetFileName(f)}: local version missing in [Info(...)] -> skipping.");
                                continue;
                            }

                            Logger.Info($"[UpdatePlugins] {Path.GetFileName(f)} meta: title='{localTitle}' author='{localAuthor}' version='{localVersion}' class='{className}'");

                            ThrottleIfNeeded(requestsPerMinute);

                            var encodedTitle = Uri.EscapeDataString(localTitle ?? "");
                            var searchUrl = $"https://umod.org/plugins/search.json?query={encodedTitle}&page=1&sort=title&sortdir=asc";

                            Logger.Info($"[UpdatePlugins] Searching uMod for '{localTitle}' via: {searchUrl}");
                            string respContent;
                            try
                            {
                                respContent = http.GetStringAsync(searchUrl).GetAwaiter().GetResult();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[UpdatePlugins] HTTP search error for {Path.GetFileName(f)}: {ex.Message}");
                                hadError = true;
                                continue;
                            }

                            using var doc = JsonDocument.Parse(respContent);
                            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                            {
                                Logger.Warn($"[UpdatePlugins] No results from uMod search for '{localTitle}'.");
                                continue;
                            }

                            var candidates = new List<JsonElement>();
                            foreach (var item in data.EnumerateArray())
                            {
                                try
                                {
                                    if (!item.TryGetProperty("download_url", out var downloadUrlElem)) continue;
                                    var downloadUrl = downloadUrlElem.GetString() ?? "";
                                    var remoteFile = GetFileNameFromUrl(downloadUrl);
                                    var baseRemote = Path.GetFileNameWithoutExtension(remoteFile);
                                    if (string.Equals(baseRemote, baseLocalName, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(baseRemote, className, StringComparison.OrdinalIgnoreCase))
                                    {
                                        candidates.Add(item);
                                    }
                                }
                                catch { }
                            }

                            if (candidates.Count == 0)
                            {
                                Logger.Warn($"[UpdatePlugins] No candidate found for {Path.GetFileName(f)} in uMod search results.");
                                continue;
                            }

                            JsonElement match;
                            if (candidates.Count > 1)
                            {
                                var filteredByVersion = new List<JsonElement>();
                                foreach (var cnd in candidates)
                                {
                                    try
                                    {
                                        if (cnd.TryGetProperty("latest_release_version", out var v) && v.ValueKind == JsonValueKind.String)
                                        {
                                            var rv = v.GetString();
                                            var cmp = CompareVersionStrings(rv, localVersion);
                                            if (cmp >= 0) filteredByVersion.Add(cnd);
                                        }
                                    }
                                    catch { }
                                }

                                if (filteredByVersion.Count == 1)
                                {
                                    match = filteredByVersion[0];
                                    Logger.Info($"[UpdatePlugins] Disambiguated by version (single candidate remaining).");
                                }
                                else
                                {
                                    Logger.Warn($"[UpdatePlugins] Ambiguous candidates for {Path.GetFileName(f)} ({candidates.Count}) -> skipping update for this plugin.");
                                    continue;
                                }
                            }
                            else
                            {
                                match = candidates[0];
                            }

                            string remoteVersion = "";
                            if (match.TryGetProperty("latest_release_version", out var rvElem) && rvElem.ValueKind == JsonValueKind.String)
                                remoteVersion = rvElem.GetString() ?? "";

                            if (string.IsNullOrEmpty(remoteVersion))
                            {
                                Logger.Warn($"[UpdatePlugins] Remote candidate has no version for {Path.GetFileName(f)} -> skipping.");
                                continue;
                            }

                            var cmpFinal = CompareVersionStrings(remoteVersion, localVersion);
                            if (cmpFinal <= 0)
                            {
                                Logger.Info($"[UpdatePlugins] {Path.GetFileName(f)} is up-to-date (remote {remoteVersion} <= local {localVersion}).");
                                continue;
                            }

                            Logger.Info($"[UpdatePlugins] Newer version found for {Path.GetFileName(f)}: remote={remoteVersion} local={localVersion}. Preparing download...");

                            if (!match.TryGetProperty("download_url", out var downloadElem))
                            {
                                Logger.Error($"[UpdatePlugins] Download URL missing on selected match for {Path.GetFileName(f)}.");
                                hadError = true;
                                continue;
                            }
                            var downloadUrlFinal = downloadElem.GetString() ?? "";
                            if (string.IsNullOrEmpty(downloadUrlFinal))
                            {
                                Logger.Error($"[UpdatePlugins] Download URL empty for {Path.GetFileName(f)}.");
                                hadError = true;
                                continue;
                            }

                            ThrottleIfNeeded(requestsPerMinute);

                            var tempFileName = Guid.NewGuid().ToString() + ".temp";
                            var tempFilePath = Path.Combine(pluginsFolder, tempFileName);
                            Logger.Info($"[UpdatePlugins] Downloading update to temp file {tempFileName} from {downloadUrlFinal}");

                            try
                            {
                                using var resp = http.GetAsync(downloadUrlFinal).GetAwaiter().GetResult();
                                resp.EnsureSuccessStatusCode();
                                using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[UpdatePlugins] Download failed for {Path.GetFileName(f)}: {ex.Message}");
                                try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
                                hadError = true;
                                continue;
                            }

                            var fi = new FileInfo(tempFilePath);
                            if (!fi.Exists || fi.Length == 0)
                            {
                                Logger.Error($"[UpdatePlugins] Temp download missing or zero-length for {Path.GetFileName(f)}.");
                                try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
                                hadError = true;
                                continue;
                            }

                            var finalPath = Path.Combine(pluginsFolder, Path.GetFileName(f));
                            try
                            {
                                if (File.Exists(finalPath))
                                {
                                    Logger.Info($"[UpdatePlugins] Removing existing plugin file before replace: {finalPath}");
                                    File.Delete(finalPath);
                                }
                                File.Move(tempFilePath, finalPath);
                                Logger.Info($"[UpdatePlugins] Plugin {Path.GetFileName(f)} updated successfully to {remoteVersion}");
                                RunState.UpdatedPlugins.Add(Path.GetFileName(finalPath));
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[UpdatePlugins] Error replacing plugin file {Path.GetFileName(f)}: {ex.Message}");
                                try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
                                hadError = true;
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[UpdatePlugins] Unexpected exception checking {Path.GetFileName(f)}: {ex.Message}");
                            hadError = true;
                            continue;
                        }
                    }
                }

                Logger.Info("[UpdatePlugins] Completed plugin update pass.");
                return !hadError;
            }
            catch (Exception ex)
            {
                Logger.Error($"[UpdatePlugins] Unexpected error: {ex.Message}");
                return false;
            }
        }

    #endregion

    #region Misc helpers (systemctl resolution, throttling, parsing)

        // Resolve systemctl path on Linux.
        private static string ResolveSystemctlPath()
        {
            try
            {
                var psiWhich = new ProcessStartInfo("which", "systemctl")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psiWhich))
                {
                    if (p != null)
                    {
                        var outp = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit(2000);
                        if (!string.IsNullOrEmpty(outp) && File.Exists(outp))
                            return outp;
                    }
                }
            }
            catch { }

            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var cand = Path.Combine(dir, "systemctl");
                        if (File.Exists(cand))
                            return cand;
                    }
                    catch { }
                }
            }
            catch { }

            return "systemctl";
        }

        // Simple rate limiting for plugin HTTP requests.
        private static void ThrottleIfNeeded(int RequestsPerMinute)
        {
            while (true)
            {
                var now = DateTime.UtcNow;
                var cut = now.AddSeconds(-60);
                __PluginRequestTimestamps.RemoveAll(t => t < cut);
                if (__PluginRequestTimestamps.Count < RequestsPerMinute) break;
                Logger.Warn($"[UpdatePlugins] rate limit reached ({RequestsPerMinute}/min). Waiting 5s...");
                Thread.Sleep(5000);
            }
            __PluginRequestTimestamps.Add(DateTime.UtcNow);
        }

        private record PluginInfo(string Title, string Author, string Version);

        // Parse [Info("Title","Author","Version")] attribute from plugin file.
        private static PluginInfo? ParseInfoAttribute(string file)
        {
            try
            {
                var lines = File.ReadLines(file).Take(200);
                var text = string.Join("\n", lines);
                var pattern = @"\[Info\(\s*""(.*?)""\s*,\s*""(.*?)""\s*,\s*""(.*?)""\s*\)\]";
                var m = Regex.Match(text, pattern);
                if (m.Success)
                {
                    var title = m.Groups[1].Value;
                    var author = m.Groups[2].Value;
                    var version = m.Groups[3].Value;
                    return new PluginInfo(title, author, version);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"ParseInfoAttribute error for {file}: {ex.Message}");
            }
            return null;
        }

        // Extract a class name from a C# file for plugin matching.
        private static string GetClassNameFromCs(string file)
        {
            try
            {
                var lines = File.ReadLines(file).Take(400);
                var text = string.Join("\n", lines);
                var m = Regex.Match(text, @"class\s+([A-Za-z_][A-Za-z0-9_]*)\s*[:\{]");
                if (m.Success) return m.Groups[1].Value;
            }
            catch { }
            return Path.GetFileNameWithoutExtension(file);
        }

        // Get filename from a download URL.
        private static string GetFileNameFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try { var u = new Uri(url); return Path.GetFileName(u.AbsolutePath); } catch { var parts = url.Split('/'); return parts.LastOrDefault() ?? ""; }
        }

        // Compare version strings using Version parsing with fallbacks.
        private static int CompareVersionStrings(string? a, string? b)
        {
            try
            {
                var va = ParseVersion(a);
                var vb = ParseVersion(b);
                if (va != null && vb != null) return va.CompareTo(vb);
                if (va != null && vb == null) return 1;
                if (va == null && vb != null) return -1;
                if (a != null && b != null) return string.Compare(a, b, StringComparison.Ordinal);
                return 0;
            }
            catch { return 0; }
        }

        // Parse flexible version strings into Version instances.
        private static Version? ParseVersion(string? s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                s = s.Trim();
                if (Version.TryParse(s, out var v)) return v;
                var m = Regex.Match(s, @"\d+(?:\.\d+){0,3}");
                if (m.Success)
                {
                    var ss = m.Value;
                    if (Version.TryParse(ss, out var v2)) return v2;
                    var parts = ss.Split('.');
                    while (parts.Length < 3) ss += ".0";
                    if (Version.TryParse(ss, out var v3)) return v3;
                }
                var digits = Regex.Split(s, @"\D+").Where(x => !string.IsNullOrEmpty(x)).ToArray();
                if (digits.Length > 0)
                {
                    var p = digits.Take(3).ToArray();
                    while (p.Length < 3) p = p.Append("0").ToArray();
                    var norm = string.Join('.', p);
                    if (Version.TryParse(norm, out var v4)) return v4;
                }
            }
            catch { }
            return null;
        }

    #endregion

    #region File and directory helpers

        // Determine if path is under any root in provided set.
        private static bool IsUnderAny(string path, HashSet<string> roots)
        {
            var pNorm = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            foreach (var r in roots)
            {
                var rNorm = Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(pNorm, rNorm, StringComparison.OrdinalIgnoreCase)) return true;
                if (pNorm.StartsWith(rNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Copy directory and overwrite destination contents.
        private static void CopyDirectoryOverwrite(string src, string dst)
        {
            var srcRoot = Path.GetFullPath(src).TrimEnd(Path.DirectorySeparatorChar);
            var dstRoot = Path.GetFullPath(dst).TrimEnd(Path.DirectorySeparatorChar);

            Directory.CreateDirectory(dstRoot);

            foreach (var dir in Directory.GetDirectories(srcRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcRoot, dir);
                var target = Path.Combine(dstRoot, rel);
                Directory.CreateDirectory(target);
            }

            foreach (var file in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcRoot, file);
                var target = Path.Combine(dstRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? dstRoot);
                File.Copy(file, target, true);
                Logger.Info($"Copied file: {rel} -> {target}");
            }
        }

        // Find origin directory inside extracted archive content.
        private static string? FindExtractOrigin(string extractRoot)
        {
            try
            {
                var candidate1 = Path.Combine(extractRoot, "RustDedicated_Data", "Managed");
                var candidate2 = Path.Combine(extractRoot, "Managed");
                if (Directory.Exists(candidate1) || Directory.Exists(candidate2)) return extractRoot;

                var candidates = Directory.GetDirectories(extractRoot, "*", SearchOption.AllDirectories)
                    .Where(d => Directory.Exists(Path.Combine(d, "RustDedicated_Data", "Managed")) ||
                                Directory.Exists(Path.Combine(d, "Managed")) ||
                                Directory.Exists(Path.Combine(d, "Oxide")))
                    .ToList();
                return candidates.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        // Check if tmux session exists.
        private static bool TmuxSessionExists(string sessionName, int timeoutMs = 2000)
        {
            if (string.IsNullOrWhiteSpace(sessionName)) return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "tmux",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                    Arguments = $"has-session -t {sessionName}"
                };

                using (var p = Process.Start(psi))
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

        // Detect availability of gnome-terminal and tmux on Linux.
        private static bool[] DetectGnomeAndTmux()
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

        // Remove the temporary backup directory if present.
        private static void CleanTempBackup(string serverDir)
        {
            try
            {
                var backupsRoot = Path.Combine(serverDir, "temp_backup");
                Logger.Info($"CleanTempBackup: attempting to remove '{backupsRoot}' if present...");
                if (!Directory.Exists(backupsRoot))
                {
                    Logger.Info("CleanTempBackup: temp_backup does not exist; nothing to do.");
                    return;
                }

                try
                {
                    Directory.Delete(backupsRoot, true);
                    Logger.Info("CleanTempBackup: Directory.Delete completed without exception, verifying...");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"CleanTempBackup: error deleting temp_backup: {ex.Message}");
                }

                if (!Directory.Exists(backupsRoot))
                {
                    Logger.Info("CleanTempBackup: temp_backup successfully removed.");
                }
                else
                {
                    Logger.Warn("CleanTempBackup: temp_backup still present after delete attempt.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"CleanTempBackup: unexpected exception: {ex.Message}");
            }
        }

    #endregion

    #region Service restart and updater result creation

        // Restart or start Rust service using platform-specific commands.
        private static bool RestartRustService(string servName)
        {
            if (string.IsNullOrWhiteSpace(servName))
            {
                Logger.Error("RestartRustService: service name empty");
                return false;
            }

            int RunAndLog(string exe, string args, int timeoutMs = 30000)
            {
                try
                {
                    var psi = new ProcessStartInfo(exe, args)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        Logger.Error($"RestartRustService: failed to start '{exe} {args}'");
                        return -1;
                    }

                    var exited = proc.WaitForExit(timeoutMs);
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();

                    if (!string.IsNullOrEmpty(stdout)) Logger.Info(stdout.Trim());
                    if (!string.IsNullOrEmpty(stderr)) Logger.Warn(stderr.Trim());

                    if (!exited)
                    {
                        try { proc.Kill(true); } catch { }
                        Logger.Warn($"RestartRustService: '{exe} {args}' timed out");
                        return -2;
                    }

                    return proc.ExitCode;
                }
                catch (Exception ex)
                {
                    Logger.Error($"RestartRustService: error running '{exe} {args}': {ex.Message}");
                    return -1;
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Logger.Info($"RestartRustService: Windows -> sc start \"{servName}\"");
                var codeWin = RunAndLog("sc", $"start \"{servName}\"", 15000);
                return codeWin == 0;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Logger.Warn("RestartRustService: unsupported platform");
                return false;
            }

            bool IsEffectiveRoot()
            {
                try
                {
                    var status = File.ReadAllLines("/proc/self/status");
                    foreach (var line in status)
                    {
                        if (line.StartsWith("Uid:"))
                        {
                            var parts = line.Split(new char[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 3 && int.TryParse(parts[2], out var euid))
                                return euid == 0;
                        }
                    }
                }
                catch { }

                try
                {
                    var user = Environment.GetEnvironmentVariable("USER") ?? Environment.UserName;
                    if (!string.IsNullOrEmpty(user) && user.Equals("root", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }

                return false;
            }

            var systemctl = ResolveSystemctlPath();
            Logger.Info($"RestartRustService: resolved systemctl: {systemctl}");

            var isRoot = IsEffectiveRoot();

            if (isRoot)
            {
                Logger.Info($"RestartRustService: running as root -> '{systemctl} start {servName}'");
                var code = RunAndLog(systemctl, $"start {servName}", 30000);
                if (code != 0) Logger.Error($"RestartRustService: systemctl exit code {code}");
                return code == 0;
            }
            else
            {
                Logger.Info($"RestartRustService: not root -> 'sudo -n {systemctl} start {servName}' (requires NOPASSWD)");
                var code = RunAndLog("sudo", $"-n {systemctl} start {servName}", 30000);
                if (code != 0)
                {
                    Logger.Error("RestartRustService: start failed. If you see 'a password is required' in logs, configure sudoers NOPASSWD for this user and command.");
                }
                return code == 0;
            }
        }

        // Create updateresult.json with failure details and start server/script/service.
        private static void CreateUpdateresultAndStartScript(string server_dir, string update_id, string failReason, string start_script)
        {
            try
            {
                var resultPath = Path.Combine(server_dir, "updateresult.json");
                if (!RunState.IsForce && update_id == "wipe")
                {
                    var payload = new Dictionary<string, object?>
                    {
                        ["result"] = "failed",
                        ["fail_reason"] = failReason,
                        ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        ["update_id"] = update_id,
                        ["wiped"] = "no",
                        ["wipe_info"] = ""
                    };
                    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(resultPath, json);
                    Logger.Info($"Created {resultPath} with fail_reason='{failReason}'");
                }
                else
                {
                    if (RunState.UpdatePluginsFlag == "yes" && RunState.UpdatedPlugins != null && RunState.UpdatedPlugins.Count > 0)
                    {
                        var payload = new Dictionary<string, object?>
                        {
                            ["result"] = "failed",
                            ["fail_reason"] = failReason,
                            ["updated_plugins"] = RunState.UpdatedPlugins,
                            ["backup_cycle"] = false,
                            ["server_restored"] = false,
                            ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            ["update_id"] = update_id,
                            ["wiped"] = "no",
                            ["wipe_info"] = ""
                        };

                        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(resultPath, json);
                        Logger.Info($"Created {resultPath} with fail_reason='{failReason}'");
                    }
                    else
                    {
                        var payload = new Dictionary<string, object?>
                        {
                            ["result"] = "failed",
                            ["fail_reason"] = failReason,
                            ["backup_cycle"] = false,
                            ["server_restored"] = false,
                            ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            ["update_id"] = update_id,
                            ["wiped"] = "no",
                            ["wipe_info"] = ""
                        };

                        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(resultPath, json);
                        Logger.Info($"Created {resultPath} with fail_reason='{failReason}'");
                    }
                }

            }
            catch (Exception ex)
            {
                Logger.Error($"Error creating updateresult.json: {ex.Message}");
            }
            if (!RunState.IsService)
            {
                if (!string.IsNullOrWhiteSpace(start_script))
                {
                    try
                    {
                        CleanTempBackup(server_dir);
                        StartServerScript(start_script, server_dir);
                        Logger.Info($"Started start_script: {start_script}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error starting start_script: {ex.Message}");
                    }
                }
                else
                {
                    Logger.Warn("start_script not specified; skipping start.");
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(RunState.ServiceName))
                {
                    try
                    {
                        CleanTempBackup(server_dir);
                        RestartRustService(RunState.ServiceName);
                        Logger.Info($"Started service: {RunState.ServiceName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error restarting service: {ex.Message}");
                    }
                }
                else
                {
                    Logger.Warn("serviceName not specified; skipping start.");
                }
            }
        }

    #endregion

    #region Wipe cycle and configuration editing

        // Perform force wipe operations: delete plugin data, BPs, player data, maps and update server.cfg keys.
        private static string WipeCycle(out string wipingInfo)
        {
            string WipeResult = "no";
            wipingInfo = "";
            string pluginDir = "";
            string cfgDir = "";
            string sDataDir = "";
            string sp = "";
            if (string.IsNullOrEmpty(RunState.ServerIdentity))
                RunState.ServerIdentity = "my_server_identity";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                pluginDir = RunState.ServerDir + "\\oxide\\data";
                sDataDir = RunState.ServerDir + "\\server\\" + RunState.ServerIdentity;
                cfgDir = RunState.ServerDir + "\\server\\" + RunState.ServerIdentity + "\\cfg";
                sp = "\\";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                pluginDir = RunState.ServerDir + "/oxide/data";
                sDataDir = RunState.ServerDir + "/server/" + RunState.ServerIdentity;
                cfgDir = RunState.ServerDir + "/server/" + RunState.ServerIdentity + "/cfg";
                sp = "/";
            }
            string[] localFiles;
            if (RunState.PdataFilesToDelete.Count > 0)
            {
                if (!Directory.Exists(pluginDir))
                {
                    Logger.Warn("WIPE-CYCLE: CAUTION! Plugin data directory  not found.");
                    wipingInfo += "[" + "Plugin data directory  not found." + "]";
                }
                else
                {
                    localFiles = Directory.GetFiles(pluginDir);
                    foreach (string filename in RunState.PdataFilesToDelete)
                    {
                        bool _found = false;
                        foreach (string file in localFiles)
                        {
                            if (file == pluginDir + sp + filename)
                            {
                                _found = true;
                                File.Delete(file);
                                Logger.Info("WIPE-CYCLE: Plugin Datafile " + filename + " deleted.");
                                break;
                            }
                        }
                        if (!_found)
                        {
                            Logger.Warn("WIPE-CYCLE: CAUTION! Plugin Datafile " + filename + " not found.");
                            wipingInfo += "[" + "Plugin Datafile " + filename + " not found." + "]";
                        }
                    }
                }
            }
            if (!RunState.NextWipeKeepBps)
            {
                Logger.Info("WIPE-CYCLE: Removing BP database.");
                localFiles = Directory.GetFiles(sDataDir);
                bool _found = false;
                foreach (string file in localFiles)
                {
                    if (file.Contains(".blueprints"))
                    {
                        _found = true;
                        File.Delete(file);
                        Logger.Info("WIPE-CYCLE: Players blueprint file " + file.Split(sp, StringSplitOptions.RemoveEmptyEntries)[file.Split(sp, StringSplitOptions.RemoveEmptyEntries).Length - 1] + " deleted.");
                    }
                }
                if (!_found)
                {
                    Logger.Warn("WIPE-CYCLE: CAUTION! No players blueprint db found. Please check manually.");
                    wipingInfo += "[" + "No players blueprint db found. Please check manually." + "]";
                }
            }
            if (RunState.NextWipeDeletePlayerData)
            {
                Logger.Info("WIPE-CYCLE: Removing players data.");
                localFiles = Directory.GetFiles(sDataDir);
                bool _found = false;
                foreach (string file in localFiles)
                {
                    if (file.Contains(".deaths") || file.Contains(".identities") || file.Contains(".states") || file.Contains(".tokens") || file.Contains("relationship") || file.Contains(".files"))
                    {
                        _found = true;
                        File.Delete(file);
                        Logger.Info("WIPE-CYCLE: Player data file " + file.Split(sp, StringSplitOptions.RemoveEmptyEntries)[file.Split(sp, StringSplitOptions.RemoveEmptyEntries).Length - 1] + " deleted.");
                    }
                }
                if (!_found)
                {
                    Logger.Warn("WIPE-CYCLE: CAUTION! No player data file found. Please check manually.");
                    wipingInfo += "[" + "No player data file found. Please check manually." + "]";
                }
            }
            Logger.Info("WIPE-CYCLE: Removing map data.");
            localFiles = Directory.GetFiles(sDataDir);
            bool found = false;
            foreach (string file in localFiles)
            {
                if (file.Contains(".map") || file.Contains(".sav") || file.Contains(".dat"))
                {
                    found = true;
                    File.Delete(file);
                    Logger.Info("WIPE-CYCLE: Map file " + file.Split(sp, StringSplitOptions.RemoveEmptyEntries)[file.Split(sp, StringSplitOptions.RemoveEmptyEntries).Length - 1] + " deleted.");
                }
            }
            if (!found)
            {
                Logger.Warn("WIPE-CYCLE: CAUTION! No map file found. Please check manually.");
                wipingInfo += "[" + "No map file found. Please check manually." + "]";
            }
            string cfgpath = cfgDir + sp + "server.cfg";
            string _errorText = "";
            bool kvchanged = false;
            if (!string.IsNullOrEmpty(RunState.ServerName))
            {
                kvchanged = TryUpdateConfigValue(cfgpath, "server.hostname", RunState.ServerName, true, out _errorText);
                if (kvchanged)
                {
                    Logger.Info("WIPE-CYCLE: Server hostname updated");
                }
                else
                {
                    if (!string.IsNullOrEmpty(_errorText))
                    {
                        Logger.Warn("WIPE-CYCLE: CAUTION! Server hostname not updated (error: " + _errorText + ")");
                        wipingInfo += "[" + "Server hostname not updated (error: " + _errorText + ")" + "]";
                    }
                }
            }
            kvchanged = false;
            _errorText = "";
            if (!string.IsNullOrEmpty(RunState.ServerDescription))
            {
                kvchanged = TryUpdateConfigValue(cfgpath, "server.description", RunState.ServerDescription, true, out _errorText);
                if (kvchanged)
                {
                    Logger.Info("WIPE-CYCLE: Server description updated");
                }
                else
                {
                    if (!string.IsNullOrEmpty(_errorText))
                    {
                        Logger.Warn("WIPE-CYCLE: CAUTION! Server description not updated (error: " + _errorText + ")");
                        wipingInfo += "[" + "Server description not updated (error: " + _errorText + ")" + "]";
                    }
                }
            }
            kvchanged = false;
            _errorText = "";
            if (!string.IsNullOrEmpty(RunState.NextWipeUrl))
            {
                kvchanged = TryRemoveConfigValue(cfgpath, "server.level", out _errorText);
                if (kvchanged)
                {
                    Logger.Info("WIPE-CYCLE: server.level removed");
                    kvchanged = false;
                }
                else
                {
                    if (!string.IsNullOrEmpty(_errorText))
                    {
                        Logger.Warn("WIPE-CYCLE: CAUTION! Error while removing server.level from cfg (error: " + _errorText + ")");
                        wipingInfo += "[" + "Error while removing server.level from cfg (error: " + _errorText + ")" + "]";
                        _errorText = "";
                    }
                    else
                        Logger.Info("WIPE-CYCLE: server.level was already unset");
                }
                kvchanged = TryRemoveConfigValue(cfgpath, "server.worldsize", out _errorText);
                if (kvchanged)
                {
                    Logger.Info("WIPE-CYCLE: server.worldsize removed");
                    kvchanged = false;
                }
                else
                {
                    if (!string.IsNullOrEmpty(_errorText))
                    {
                        Logger.Warn("WIPE-CYCLE: CAUTION! Error while removing server.worldsize from cfg (error: " + _errorText + ")");
                        wipingInfo += "[" + "Error while removing server.worldsize from cfg (error: " + _errorText + ")" + "]";
                        _errorText = "";
                    }
                    else
                        Logger.Info("WIPE-CYCLE: server.worldsize was already unset");
                }
                kvchanged = TryRemoveConfigValue(cfgpath, "server.seed", out _errorText);
                if (kvchanged)
                {
                    Logger.Info("WIPE-CYCLE: server.seed removed");
                    kvchanged = false;
                }
                else
                {
                    if (!string.IsNullOrEmpty(_errorText))
                    {
                        Logger.Warn("WIPE-CYCLE: CAUTION! Error while removing server.seed from cfg (error: " + _errorText + ")");
                        wipingInfo += "[" + "Error while removing server.seed from cfg (error: " + _errorText + ")" + "]";
                        _errorText = "";
                    }
                    else
                        Logger.Info("WIPE-CYCLE: server.seed was already unset");
                }
                if (!string.IsNullOrEmpty(RunState.NextWipeUrl))
                {
                    kvchanged = TryUpdateConfigValue(cfgpath, "server.levelurl", RunState.NextWipeUrl, true, out _errorText);
                    if (kvchanged)
                    {
                        Logger.Info("WIPE-CYCLE: servel.levelurl updated");
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(_errorText))
                        {
                            Logger.Warn("WIPE-CYCLE: CAUTION! server.levelurl not updated (error: " + _errorText + ")");
                            wipingInfo += "[" + "server.levelurl not updated (error: " + _errorText + ")" + "]";
                        }
                    }
                }
            }
            else
            {
                kvchanged = false;
                _errorText = "";
                kvchanged = TryRemoveConfigValue(cfgpath, "server.levelurl", out _errorText);
                if (kvchanged)
                {
                    Logger.Info("WIPE-CYCLE: server.levelurl removed");
                    kvchanged = false;
                }
                else
                {
                    if (!string.IsNullOrEmpty(_errorText))
                    {
                        Logger.Warn("WIPE-CYCLE: CAUTION! Error while removing server.levelurl from cfg (error: " + _errorText + ")");
                        wipingInfo += "[" + "Error while removing server.levelurl from cfg (error: " + _errorText + ")" + "]";
                        _errorText = "";
                    }
                    else
                        Logger.Info("WIPE-CYCLE: server.levelurl was already unset");
                }
                if (!string.IsNullOrEmpty(RunState.NextWipeLevel))
                {
                    kvchanged = TryUpdateConfigValue(cfgpath, "server.level", RunState.NextWipeLevel, true, out _errorText);
                    if (kvchanged)
                    {
                        Logger.Info("WIPE-CYCLE: server.level updated");
                        kvchanged = false;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(_errorText))
                        {
                            Logger.Warn("WIPE-CYCLE: CAUTION! server.level not updated (error: " + _errorText + ")");
                            wipingInfo += "[" + "server.level not updated (error: " + _errorText + ")" + "]";
                            _errorText = "";
                        }
                    }
                }
                if (!string.IsNullOrEmpty(RunState.NextWipeSeed))
                {
                    kvchanged = TryUpdateConfigValue(cfgpath, "server.seed", RunState.NextWipeSeed, true, out _errorText);
                    if (kvchanged)
                    {
                        Logger.Info("WIPE-CYCLE: server.seed updated");
                        kvchanged = false;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(_errorText))
                        {
                            Logger.Warn("WIPE-CYCLE: CAUTION! server.seed not updated (error: " + _errorText + ")");
                            wipingInfo += "[" + "server.seed not updated (error: " + _errorText + ")" + "]";
                            _errorText = "";
                        }
                    }
                }
                if (!string.IsNullOrEmpty(RunState.NextWipeMapsize))
                {
                    kvchanged = TryUpdateConfigValue(cfgpath, "server.worldsize", RunState.NextWipeMapsize, true, out _errorText);
                    if (kvchanged)
                    {
                        Logger.Info("WIPE-CYCLE: server.worldsize updated");
                        kvchanged = false;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(_errorText))
                        {
                            Logger.Warn("WIPE-CYCLE: CAUTION! server.worldsize not updated (error: " + _errorText + ")");
                            wipingInfo += "[" + "server.worldsize not updated (error: " + _errorText + ")" + "]";
                            _errorText = "";
                        }
                    }
                }
            }
            Logger.Info("WIPE-CYCLE: wiping task done");
            WipeResult = "yes";
            return WipeResult;
        }

        // Decode plugin datafile list argument, supporting base64 URL-safe encoding or raw JSON.
        private static List<string> DecodeFileListArgToList(string? arg)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(arg))
                return result;

            string? json = null;

            try
            {
                string b64 = arg.Replace('-', '+').Replace('_', '/');
                switch (b64.Length % 4)
                {
                    case 2: b64 += "=="; break;
                    case 3: b64 += "="; break;
                }
                var bytes = Convert.FromBase64String(b64);
                json = Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                json = arg;
            }

            if (string.IsNullOrWhiteSpace(json))
                return result;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrEmpty(s))
                            result.Add(s);
                    }
                }
            }
            catch
            {
                return new List<string>();
            }

            return result;
        }

        // Update or append a key/value pair inside server.cfg with optional quoting behavior.
        public static bool TryUpdateConfigValue(string path, string key, string value, bool forceWrite, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "'path' param is null or empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(key))
            {
                error = "'key' param is null or empty.";
                return false;
            }

            try
            {
                string content = File.Exists(path) ? File.ReadAllText(path) : "";
                var parts = Regex.Split(content, "(\r\n|\n)", RegexOptions.Compiled);

                string normalized = NormalizeControlChars(value);

                string BuildValue(string existingKey, string norm)
                {
                    if (string.Equals(existingKey, "server.description", StringComparison.OrdinalIgnoreCase))
                    {
                        norm = norm.Replace("\"", "").Replace("'", "");
                        return (norm.IndexOf(' ') >= 0) ? "\"" + norm + "\"" : norm;
                    }
                    if (NeedsQuoting(norm))
                        return "\"" + norm.Replace("\"", "") + "\"";
                    return norm;
                }

                var lineRegex = new Regex(@"^\s*(?<key>[^\s#;=]+)\s+(?<value>.*)$", RegexOptions.Compiled);
                bool updated = false;

                for (int i = 0; i < parts.Length; i += 2)
                {
                    var originalLine = parts[i];
                    if (string.IsNullOrEmpty(originalLine)) continue;
                    var trimmed = originalLine.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                        continue;

                    var match = lineRegex.Match(originalLine);
                    if (!match.Success) continue;

                    var existingKey = match.Groups["key"].Value;
                    if (!string.Equals(existingKey, key, StringComparison.Ordinal))
                        continue;

                    var newValueText = BuildValue(existingKey, normalized);
                    parts[i] = $"{existingKey} {newValueText}";
                    updated = true;
                    break;
                }

                if (updated)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < parts.Length; i++)
                        sb.Append(parts[i]);
                    WriteAllTextNoBomAtomic(path, sb.ToString());
                    return true;
                }

                if (forceWrite)
                {
                    string sep = content.Contains("\r\n") ? "\r\n" : "\n";
                    var newLine = $"{key} {BuildValue(key, normalized)}";
                    string newContent = string.IsNullOrEmpty(content)
                        ? newLine + sep
                        : (content.EndsWith(sep) ? content + newLine + sep : content + sep + newLine + sep);
                    WriteAllTextNoBomAtomic(path, newContent);
                    return true;
                }

                error = File.Exists(path) ? "Chiave non trovata e forceWrite=false" : $"File non trovato: {path}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Remove a key/value line from server.cfg if present.
        public static bool TryRemoveConfigValue(string path, string key, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "'path' param is null or empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(key))
            {
                error = "'key' param is null or empty.";
                return false;
            }

            try
            {
                if (!File.Exists(path))
                {
                    error = $"File non trovato: {path}";
                    return false;
                }

                string content = File.ReadAllText(path);
                var parts = Regex.Split(content, "(\r\n|\n)", RegexOptions.Compiled);
                var lineRegex = new Regex(@"^\s*(?<key>[^\s#;=]+)\s+(?<value>.*)$", RegexOptions.Compiled);

                bool removed = false;

                var sb = new StringBuilder();
                for (int i = 0; i < parts.Length; i += 2)
                {
                    var line = parts[i];
                    var sep = (i + 1 < parts.Length) ? parts[i + 1] : "";

                    if (!string.IsNullOrEmpty(line))
                    {
                        var match = lineRegex.Match(line);
                        if (match.Success)
                        {
                            var k = match.Groups["key"].Value;
                            if (string.Equals(k, key, StringComparison.Ordinal))
                            {
                                removed = true;
                                continue;
                            }
                        }
                    }

                    sb.Append(line);
                    sb.Append(sep);
                }

                if (removed)
                {
                    WriteAllTextNoBomAtomic(path, sb.ToString());
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Normalize control characters in configuration values.
        private static string NormalizeControlChars(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var cleaned = value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ');
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");
            return cleaned.Trim();
        }

        // Atomic write without BOM.
        private static void WriteAllTextNoBomAtomic(string path, string content)
        {
            var tmp = path + ".tmp_" + Guid.NewGuid().ToString("N");
            var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(tmp, content, encoding);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // Determine if a value needs quoting.
        private static bool NeedsQuoting(string v)
        {
            if (string.IsNullOrEmpty(v)) return true;
            foreach (var c in v)
                if (char.IsWhiteSpace(c)) return true;
            return false;
        }

    #endregion
    }
}

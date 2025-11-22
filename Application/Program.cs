using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace FeedMeUpdates
{
    internal static class Logger
    {
        private static StreamWriter? writer;
        private static readonly object sync = new();
        private const long MaxSizeBytes = 5L * 1024L * 1024L; // 5 MB

        /// <summary>Initializes the logger creating/rotating the log file in the server directory.</summary>
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

        /// <summary>Returns a timestamp string for log entries.</summary>
        private static string Timestamp()
        {
            var now = DateTime.Now;
            return $"[{now:dd/MM/yy HH:mm:ss}]";
        }

        /// <summary>Writes a log line to console and file with a given level.</summary>
        private static void Write(string level, string message)
        {
            var line = $"{Timestamp()} [{level}] {message}";
            lock (sync)
            {
                try { Console.WriteLine(line); } catch { }
                try { writer?.WriteLine(line); } catch { }
            }
        }

        /// <summary>Logs an informational message.</summary>
        public static void Info(string msg) => Write("INFO", msg);
        /// <summary>Logs a warning message.</summary>
        public static void Warn(string msg) => Write("WARN", msg);
        /// <summary>Logs an error message.</summary>
        public static void Error(string msg) => Write("ERROR", msg);

        /// <summary>Closes the logger and releases resources.</summary>
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
    }

    internal static class Program
    {
        private static string Fails = "";
        private static readonly List<DateTime> __PluginRequestTimestamps = new();

        private enum FlowKind { Init, Testrun, Scheduled }

        /// <summary>Entry point: parses arguments and routes to the appropriate update flow.</summary>
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

            if (RunState.IsService && RunState.ShowServerConsole)
                RunState.ShowServerConsole = false;

            if (string.Equals(updateId, "init", StringComparison.OrdinalIgnoreCase))
                return HandleFlow(FlowKind.Init);
            if (string.Equals(updateId, "testrun", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(RunState.What)) RunState.What = "both";
                return HandleFlow(FlowKind.Testrun);
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

        /// <summary>Coordinates a selected flow (init, test run, scheduled) including backup, update and restart.</summary>
        private static int HandleFlow(FlowKind kind)
        {
            Logger.Init(RunState.ServerDir);
            Logger.Info($"{kind.ToString().ToUpper()} flow started. Args: update_id={RunState.UpdateId} what={RunState.What} update_plugins={RunState.UpdatePluginsFlag} server_dir={RunState.ServerDir} steamcmd={RunState.SteamCmd} start_script={RunState.StartScript} isService={RunState.IsService} serviceName={RunState.ServiceName}");

            if (!ValidateFlow(kind))
            {
                Logger.Close();
                return 0;
            }

            Logger.Info($"{kind.ToString().ToUpper()} validation passed.");

            WaitForRustDedicatedAndLog();

            var updatingLockPath = Path.Combine(RunState.ServerDir, "updating.lock");
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

        /// <summary>Validates arguments and environment for the selected flow.</summary>
        private static bool ValidateFlow(FlowKind kind)
        {
            if (kind == FlowKind.Init || kind == FlowKind.Testrun)
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

        /// <summary>Returns true if server update is requested or both.</summary>
        private static bool IsWhatServerOrBoth() =>
            string.Equals(RunState.What, "both", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(RunState.What, "server", StringComparison.OrdinalIgnoreCase);

        /// <summary>Returns true if oxide update is requested or both.</summary>
        private static bool IsWhatOxideOrBoth() =>
            string.Equals(RunState.What, "both", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(RunState.What, "oxide", StringComparison.OrdinalIgnoreCase);

        /// <summary>Runs server update and restores from backup on failure.</summary>
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

        /// <summary>Runs oxide update and restores from backup on failure.</summary>
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

        /// <summary>Splits a flag token into key and value.</summary>
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

        /// <summary>Parses command-line tokens into a dictionary of key/value pairs.</summary>
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

        /// <summary>Parses a string into boolean supporting extra numeric forms.</summary>
        private static bool ParseBool(string? s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (bool.TryParse(s, out var b)) return b;
            if (s == "1") return true;
            if (s == "0") return false;
            return false;
        }

        /// <summary>Waits for the Rust process or service to stop before continuing.</summary>
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
        }

        /// <summary>Checks if a service is still running on the host platform.</summary>
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

        /// <summary>Checks if a process with given name is running.</summary>
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

        /// <summary>Starts the server start script (or window/tmux) based on platform and flags.</summary>
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

        /// <summary>Creates a temporary backup of the server directory (excluding updater artifacts).</summary>
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
                    var exeNames = new[] { "FeedMeUpdate.exe", "FeedMeUpdate" };
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

        /// <summary>Restores server files from the temporary backup and marks failure.</summary>
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

        /// <summary>Handles a failed restore attempt and writes failure markers.</summary>
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

        /// <summary>Runs an external process and captures combined output text.</summary>
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

        /// <summary>Runs an external process and returns its exit code logging output lines.</summary>
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

        /// <summary>Retrieves the remote Rust build ID using steamcmd parsing heuristics.</summary>
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

        /// <summary>Fetches latest Oxide release tag and a suitable asset URL from GitHub.</summary>
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

        /// <summary>Updates the Rust server using steamcmd with retries and diagnostics.</summary>
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

        /// <summary>Updates Oxide by downloading latest release and copying relevant files.</summary>
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

        /// <summary>Checks and updates plugins from uMod if newer versions are available.</summary>
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

                Logger.Info("[UpdatePlugins] Completed plugin update pass.");
                return !hadError;
            }
            catch (Exception ex)
            {
                Logger.Error($"[UpdatePlugins] Unexpected error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Resolves the systemctl path on Linux systems.</summary>
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

        /// <summary>Applies simple rate limiting for plugin HTTP requests.</summary>
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

        /// <summary>Parses the [Info(...)] attribute from a plugin file.</summary>
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

        /// <summary>Extracts the first class name from a .cs plugin file.</summary>
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

        /// <summary>Gets file name portion from a download URL.</summary>
        private static string GetFileNameFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try { var u = new Uri(url); return Path.GetFileName(u.AbsolutePath); } catch { var parts = url.Split('/'); return parts.LastOrDefault() ?? ""; }
        }

        /// <summary>Compares version strings using Version parsing with fallbacks.</summary>
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

        /// <summary>Parses loose version string formats into Version if possible.</summary>
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

        /// <summary>Determines if a path is under any excluded root path.</summary>
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

        /// <summary>Copies a directory tree overwriting files at destination.</summary>
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

        /// <summary>Finds the origin directory inside extracted Oxide content.</summary>
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

        /// <summary>Checks if a tmux session with given name exists.</summary>
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

        /// <summary>Detects availability of gnome-terminal and tmux on Linux.</summary>
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

        /// <summary>Removes the temporary backup directory if it exists.</summary>
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

        /// <summary>Starts or restarts the Rust service using platform-specific commands.</summary>
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

        /// <summary>Creates failure updateresult.json and starts script or service accordingly.</summary>
        private static void CreateUpdateresultAndStartScript(string server_dir, string update_id, string failReason, string start_script)
        {
            try
            {
                var resultPath = Path.Combine(server_dir, "updateresult.json");
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
                        ["update_id"] = update_id
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
                        ["update_id"] = update_id
                    };

                    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(resultPath, json);
                    Logger.Info($"Created {resultPath} with fail_reason='{failReason}'");
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
    }
}

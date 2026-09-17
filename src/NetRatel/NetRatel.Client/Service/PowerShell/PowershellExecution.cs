using Microsoft.PowerShell;
using NetRatel.Client.Data.PowerShell;
using NetRatel.Client.Service.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Powershell
{
    public sealed class PowerShellExecutor : IDisposable
    {
        private bool _disposed;

        static PowerShellExecutor()
        {
            // Force PSHOME early
            //EnsurePSHome();
        }

        public CommandResult ExecuteCommand(string command)
            => ExecuteCommandAsync(command, CancellationToken.None, null).GetAwaiter().GetResult();

        public Task<CommandResult> ExecuteCommandAsync(
            string command,
            CancellationToken cancellationToken = default,
            Action<System.Management.Automation.PowerShell, PSDataCollection<PSObject>>? configureStreams = null)
            => ExecuteScriptAsync(command, null, cancellationToken, configureStreams);

        public async Task<CommandResult> ExecuteScriptAsync(
            string script,
            IReadOnlyDictionary<string, string>? parameters,
            CancellationToken cancellationToken = default,
            Action<System.Management.Automation.PowerShell, PSDataCollection<PSObject>>? configureStreams = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PowerShellExecutor));
            if (string.IsNullOrWhiteSpace(script)) throw new ArgumentNullException(nameof(script));

            cancellationToken.ThrowIfCancellationRequested();

            var result = new CommandResult();

            var iss = InitialSessionState.Create();
            iss.ThrowOnRunspaceOpenError = true;
            iss.LanguageMode = PSLanguageMode.FullLanguage;
            iss.ExecutionPolicy = ExecutionPolicy.Bypass;

            var appBaseDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
            var ridRoot = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : "unix";
            var modulesRoot = Path.Combine(appBaseDir, "runtimes", ridRoot, "lib", "net9.0", "Modules");

            // Absolute manifests
            var host = Path.Combine(modulesRoot, "Microsoft.PowerShell.Host", "Microsoft.PowerShell.Host.psd1");
            var mgmt = Path.Combine(modulesRoot, "Microsoft.PowerShell.Management", "Microsoft.PowerShell.Management.psd1");
            var util = Path.Combine(modulesRoot, "Microsoft.PowerShell.Utility", "Microsoft.PowerShell.Utility.psd1");
            var sec = Path.Combine(modulesRoot, "Microsoft.PowerShell.Security", "Microsoft.PowerShell.Security.psd1");

            iss.ImportPSModule(new[] { host, mgmt, util, sec });  // don’t import by name

            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();

            var safeCwd = GetSafeWorkingDirectory();
            runspace.SessionStateProxy.Path.SetLocation(safeCwd);
            runspace.SessionStateProxy.SetVariable("ProgressPreference", "SilentlyContinue");
            runspace.SessionStateProxy.SetVariable("ErrorActionPreference", "Continue");

            using var ps = System.Management.Automation.PowerShell.Create();
            ps.Runspace = runspace;

            ps.AddScript("$PSVersionTable.PSEdition, $PSVersionTable.PSVersion.ToString()");
            var ok = ps.Invoke();
            foreach (var o in ok) LogManager.WriteLog($"[PS] {o}");

            ps.Commands.Clear();

            var outputCollection = new PSDataCollection<PSObject>();
            configureStreams?.Invoke(ps, outputCollection);

            EventHandler<DataAddedEventArgs>? fallbackOutput = null;
            if (configureStreams is null)
            {
                fallbackOutput = (_, e) =>
                {
                    var item = outputCollection[e.Index];
                    var text = item?.BaseObject?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        lock (result.StringOutput) result.StringOutput.Add(text);
                };
                outputCollection.DataAdded += fallbackOutput;
            }

            EventHandler<DataAddedEventArgs> errorHandler = (_, e) =>
            {
                var err = ps.Streams.Error[e.Index];
                if (err != null)
                    lock (result.ErrorMessages) result.ErrorMessages.Add(FormatErrorRecord(err));
            };
            ps.Streams.Error.DataAdded += errorHandler;

            try
            {
                var scriptHasParam = HasParamHeader(script);

                if (!scriptHasParam && parameters != null && parameters.Count > 0)
                {
                    foreach (var kv in parameters)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                        try
                        {
                            runspace.SessionStateProxy.SetVariable(kv.Key, kv.Value ?? string.Empty);
                        }
                        catch (SessionStateException)
                        {
                            // fall through to script-level resolution
                        }
                    }
                }

                ps.AddScript(script, useLocalScope: true);
                if (scriptHasParam && parameters != null && parameters.Count > 0)
                {
                    foreach (var kv in parameters)
                    {
                        ps.AddParameter(kv.Key, kv.Value ?? string.Empty);
                    }
                }

                var async = ps.BeginInvoke<PSObject, PSObject>(null, outputCollection);
                await Task.Factory.FromAsync(async, ps.EndInvoke);

                result.Success = ps.InvocationStateInfo.State == PSInvocationState.Completed && !result.ErrorMessages.Any();

                if (!result.Success && ps.InvocationStateInfo.Reason != null)
                {
                    var msg = ps.InvocationStateInfo.Reason.Message;
                    lock (result.ErrorMessages)
                    {
                        if (!result.ErrorMessages.Any(m => m.Contains(msg)))
                            result.ErrorMessages.Add($"Execution {ps.InvocationStateInfo.State}: {msg}");
                    }
                }

                if (!result.StructuredOutput.Any() && outputCollection.Count > 0)
                {
                    foreach (var o in outputCollection)
                    {
                        if (o is null) continue;
                        var dict = o.Properties
                            .Where(p => p.IsGettable)
                            .ToDictionary(p => p.Name, p => SafeToBasicType(p.Value), StringComparer.OrdinalIgnoreCase);
                        if (dict.Count > 0)
                            result.StructuredOutput.Add(dict);
                        else
                            result.StringOutput.Add(o.BaseObject?.ToString() ?? "");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                lock (result.ErrorMessages) result.ErrorMessages.Add("Cancelled.");
                try { ps.Stop(); } catch { }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ExecutionException = ex;
                lock (result.ErrorMessages) result.ErrorMessages.Add($"C# Exception: {ex.Message}");
            }
            finally
            {
                ps.Streams.Error.DataAdded -= errorHandler;
                if (fallbackOutput != null) outputCollection.DataAdded -= fallbackOutput;
            }

            return result;
        }

        private static string GetSafeWorkingDirectory()
        {
            try { var cd = Directory.GetCurrentDirectory(); if (Directory.Exists(cd)) return cd; } catch { }
            try { var sys = Environment.SystemDirectory; if (Directory.Exists(sys)) return sys; } catch { }
            return @"C:\";
        }

        internal static bool HasParamHeader(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var s = line.TrimStart();
                if (s.Length == 0) continue;
                if (s.StartsWith("#", StringComparison.Ordinal)) continue;
                return s.StartsWith("param(", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static object? SafeToBasicType(object? value)
        {
            if (value is null) return null;
            var t = value.GetType();
            if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal) ||
                t == typeof(DateTime) || t == typeof(DateTimeOffset) ||
                t == typeof(TimeSpan) || t == typeof(Guid))
                return value;
            return value.ToString();
        }

        internal static string FormatErrorRecord(ErrorRecord error)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Error: {error.Exception?.Message}");
            if (error.ErrorDetails?.Message != null) sb.AppendLine($"Details: {error.ErrorDetails.Message}");
            if (error.ScriptStackTrace != null) sb.AppendLine($"Stack: {error.ScriptStackTrace}");
            if (error.TargetObject != null) sb.AppendLine($"Target: {error.TargetObject}");
            sb.AppendLine($"Category: {error.CategoryInfo}");
            sb.AppendLine($"FQID: {error.FullyQualifiedErrorId}");
            return sb.ToString();
        }

        private static void EnsurePSHome()
        {
            if (Environment.GetEnvironmentVariable("PSHOME") != null) return;

            string? psHome = null;

            // Try PS7 in Program Files
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var ps7 = Path.Combine(pf, "PowerShell", "7");
            if (Directory.Exists(ps7))
            {
                psHome = ps7;
            }
            else
            {
                // Fallback: Windows PowerShell
                var sys32 = Environment.SystemDirectory;
                var winPs = Path.Combine(sys32, "WindowsPowerShell", "v1.0");
                if (Directory.Exists(winPs))
                    psHome = winPs;
            }

            if (psHome != null)
            {
                Environment.SetEnvironmentVariable("PSHOME", psHome);

                // CRITICAL: Tell PowerShell SDK where config is
                var configPath = Path.Combine(psHome, "powershell.config.json");
                if (!File.Exists(configPath))
                {
                    try
                    {
                        File.WriteAllText(configPath, "{}");
                    }
                    catch { /* ignore */ }
                }

                // ALSO: Force AppContext
                AppContext.SetData("POWERHELL_CONFIG_PATH", configPath);
                AppDomain.CurrentDomain.SetData("POWERHELL_CONFIG_PATH", configPath);
            }
        }

        public void Dispose() => _disposed = true;
    }
}

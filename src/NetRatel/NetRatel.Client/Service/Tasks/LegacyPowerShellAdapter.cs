using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Client.Service.Shells;
using NetRatel.Shared.Contracts.Execution;

namespace NetRatel.Client.Service.Tasks;

/// <summary>
/// Keeps the observable success-array contract of persisted PowerShell dispatches.
/// Execution, timeout, cancellation and process cleanup belong to the external runner.
/// </summary>
internal static class LegacyPowerShellAdapter
{
    internal static async Task<(ExternalShellRunner.RunResult Execution, string? ReturnData)> ExecuteAsync(
        ExternalShellRunner runner,
        string script,
        int maximumResultBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scriptPath = ExternalShellRunner.WritePrivateTemp(".ps1", script);
        string? resultPath = null;
        try
        {
            resultPath = ExternalShellRunner.WritePrivateTemp(".json", string.Empty);
            var execution = await runner.RunPowerShellScriptAsync(
                BuildWrapper(scriptPath, resultPath), ShellExecutor.Auto, null, null, cancellationToken,
                requirementsContent: script).ConfigureAwait(false);
            if (!execution.Success || cancellationToken.IsCancellationRequested)
                return (execution, null);

            if (new FileInfo(resultPath).Length > maximumResultBytes)
                throw new InvalidOperationException("Legacy PowerShell result exceeded the gateway output limit.");

            var json = await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return (execution, null);

            using var result = JsonDocument.Parse(json);
            if (result.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Legacy PowerShell result was not an array.");
            return (execution, result.RootElement.GetArrayLength() == 0 ? null : json);
        }
        finally
        {
            ExternalShellRunner.TryDeletePrivateTemp(scriptPath);
            if (resultPath is not null) ExternalShellRunner.TryDeletePrivateTemp(resultPath);
        }
    }

    private static string BuildWrapper(string scriptPath, string resultPath)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

        var wrapper = new StringBuilder();
        wrapper.AppendLine("[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)");
        wrapper.AppendLine("$ErrorActionPreference = 'Continue'");
        wrapper.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        wrapper.AppendLine("$Error.Clear()");
        wrapper.AppendLine("try {");
        wrapper.Append("  $__netratelOutput = @(& ").Append(Literal(scriptPath)).AppendLine(")");
        wrapper.AppendLine("  if ($Error.Count -gt 0) { exit 1 }");
        wrapper.AppendLine("  if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }");
        wrapper.AppendLine("  $__netratelObjects = [System.Collections.Generic.List[object]]::new()");
        wrapper.AppendLine("  $__netratelStrings = [System.Collections.Generic.List[string]]::new()");
        wrapper.AppendLine("  foreach ($__netratelItem in $__netratelOutput) {");
        wrapper.AppendLine("    if ($null -eq $__netratelItem) { continue }");
        wrapper.AppendLine("    [Console]::Out.WriteLine($__netratelItem.ToString())");
        wrapper.AppendLine("    $__netratelProperties = [ordered]@{}");
        wrapper.AppendLine("    foreach ($__netratelProperty in $__netratelItem.PSObject.Properties) {");
        wrapper.AppendLine("      if (-not $__netratelProperty.IsGettable) { continue }");
        wrapper.AppendLine("      $__netratelValue = $__netratelProperty.Value");
        wrapper.AppendLine("      if ($null -ne $__netratelValue) {");
        wrapper.AppendLine("        $__netratelType = $__netratelValue.GetType()");
        wrapper.AppendLine("        if ($__netratelType.FullName -eq 'System.Management.Automation.PSCustomObject') { $__netratelValue = $__netratelValue.PSObject.ToString() }");
        wrapper.AppendLine("        elseif ($__netratelValue -is [timespan]) { $__netratelValue = $__netratelValue.ToString('c', [Globalization.CultureInfo]::InvariantCulture) }");
        wrapper.AppendLine("        elseif ($__netratelValue -is [datetimeoffset]) { $__netratelValue = $__netratelValue.ToString('yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz', [Globalization.CultureInfo]::InvariantCulture) }");
        wrapper.AppendLine("        if (-not ($__netratelType.IsPrimitive -or $__netratelValue -is [string] -or $__netratelValue -is [decimal] -or $__netratelValue -is [datetime] -or $__netratelValue -is [datetimeoffset] -or $__netratelValue -is [timespan] -or $__netratelValue -is [guid])) {");
        wrapper.AppendLine("          $__netratelValue = $__netratelValue.ToString()");
        wrapper.AppendLine("        }");
        wrapper.AppendLine("      }");
        wrapper.AppendLine("      $__netratelProperties[$__netratelProperty.Name] = $__netratelValue");
        wrapper.AppendLine("    }");
        wrapper.AppendLine("    if ($__netratelProperties.Count -gt 0) { $__netratelObjects.Add($__netratelProperties) }");
        wrapper.AppendLine("    else { $__netratelStrings.Add($__netratelItem.ToString()) }");
        wrapper.AppendLine("  }");
        wrapper.AppendLine("  $__netratelResult = if ($__netratelObjects.Count -gt 0) { ,$__netratelObjects.ToArray() } else { ,$__netratelStrings.ToArray() }");
        wrapper.AppendLine("  $__netratelJson = ConvertTo-Json -InputObject $__netratelResult -Depth 4 -Compress");
        wrapper.Append("  [IO.File]::WriteAllText(").Append(Literal(resultPath)).AppendLine(", $__netratelJson, [Text.UTF8Encoding]::new($false))");
        wrapper.AppendLine("  if ($Error.Count -gt 0) { exit 1 }");
        wrapper.AppendLine("  exit 0");
        wrapper.AppendLine("} catch {");
        wrapper.AppendLine("  [Console]::Error.WriteLine($_.ToString())");
        wrapper.AppendLine("  exit 1");
        wrapper.AppendLine("}");
        return wrapper.ToString();
    }
}

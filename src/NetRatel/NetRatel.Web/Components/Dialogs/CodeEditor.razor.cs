using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NetRatel.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NetRatel.Web.Services.Script;

namespace NetRatel.Web.Components.Dialogs;

public partial class CodeEditor
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public ScriptModel ScriptToEdit { get; set; } = default!;

    [Inject] private ScriptService ScriptService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private MonacoTextEditor? _editor;
    private bool _saving;

    private string _selectedLanguage = "plaintext";
    private string _selectedTheme = "vs-dark";
    private string EditableName { get; set; } = string.Empty;
    private string EditableDescription { get; set; } = string.Empty;
    private string EditableFolderPath { get; set; } = "/";
    private string _initialContent = string.Empty;

    private IReadOnlyList<KeyValuePair<string, string>> _availableLanguages { get; } = new List<KeyValuePair<string, string>>
    {
        new("PowerShell", "powershell"),
        new("Shell", "shell"),
        new("Python", "python"),
        new("JavaScript", "javascript"),
        new("TypeScript", "typescript"),
        new("C#", "csharp"),
        new("SQL", "sql"),
        new("JSON", "json"),
        new("Plain Text", "plaintext")
    };

    private IReadOnlyList<KeyValuePair<string, string>> _availableThemes { get; } = new List<KeyValuePair<string, string>>
    {
        new("Visual Studio", "vs"),
        new("Visual Studio Dark", "vs-dark"),
        new("High Contrast", "hc-black")
    };

    private string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (_selectedLanguage == value)
            {
                return;
            }

            _selectedLanguage = value;
        }
    }

    private string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (_selectedTheme == value)
            {
                return;
            }

            _selectedTheme = value;
        }
    }

    protected override void OnParametersSet()
    {
        EditableName = ScriptToEdit.Name;
        EditableDescription = ScriptToEdit.Description ?? string.Empty;
        EditableFolderPath = ScriptToEdit.FolderPath ?? "/";
        _initialContent = ScriptToEdit.Content ?? string.Empty;
        _selectedLanguage = MapScriptTypeToLanguage(ScriptToEdit.ScriptType);
    }

    private async Task SaveScript()
    {
        if (_editor is null)
        {
            return;
        }

        _saving = true;
        try
        {
            var content = await _editor.GetValueAsync();
            var request = new ScriptUpdateRequest(
                EditableName,
                NormalizeFolderPath(EditableFolderPath),
                string.IsNullOrWhiteSpace(EditableDescription) ? null : EditableDescription,
                content,
                MapLanguageToScriptType(_selectedLanguage),
                ScriptToEdit.SourceRevision
            );

            await ScriptService.UpdateScriptAsync(ScriptToEdit.Id, request);
            Snackbar.Add("Script saved successfully.", Severity.Success);
            MudDialog.Close(DialogResult.Ok(true));
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to save script: {ex.Message}", Severity.Error);
        }
        finally
        {
            _saving = false;
        }
    }

    private void Cancel() => MudDialog.Cancel();

    private static string NormalizeFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return "/";
        }

        var normalized = folderPath.Replace("\\", "/", StringComparison.Ordinal);
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        return normalized;
    }

    private static string MapScriptTypeToLanguage(string? scriptType)
    {
        if (string.IsNullOrWhiteSpace(scriptType))
        {
            return "plaintext";
        }

        return scriptType.ToLowerInvariant() switch
        {
            "powershell" => "powershell",
            "shell" => "shell",
            "bash" => "shell",
            "python" => "python",
            "javascript" => "javascript",
            "typescript" => "typescript",
            "sql" => "sql",
            "json" => "json",
            "c#" or "csharp" => "csharp",
            _ => "plaintext"
        };
    }

    private static string? MapLanguageToScriptType(string language) => language switch
    {
        "powershell" => "powershell",
        "shell" => "shell",
        "python" => "python",
        "javascript" => "javascript",
        "typescript" => "typescript",
        "csharp" => "c#",
        "sql" => "sql",
        "json" => "json",
        "plaintext" => null,
        _ => language
    };
}

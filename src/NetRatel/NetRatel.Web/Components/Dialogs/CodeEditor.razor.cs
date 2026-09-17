using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BlazorMonaco.Editor;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using NetRatel.Web.Services.Script;

namespace NetRatel.Web.Components.Dialogs;

public partial class CodeEditor
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public ScriptModel ScriptToEdit { get; set; } = default!;

    [Inject] private ScriptService ScriptService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    private StandaloneCodeEditor? _editor;
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
            _ = UpdateLanguageAsync(value);
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
            _ = UpdateThemeAsync(value);
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

    private StandaloneEditorConstructionOptions EditorConstructionOptions(StandaloneCodeEditor editor) => new()
    {
        AutomaticLayout = true,
        Theme = _selectedTheme,
        Language = _selectedLanguage,
        Value = _initialContent,
        GlyphMargin = true,
        ScrollBeyondLastLine = false
    };

    private async Task EditorOnDidInit()
    {
        if (_editor is null)
        {
            return;
        }

        await UpdateThemeAsync(_selectedTheme);
        await UpdateLanguageAsync(_selectedLanguage);
    }

    private async Task UpdateLanguageAsync(string language)
    {
        if (_editor is null)
        {
            return;
        }

        try
        {
            var model = await _editor.GetModel();
            if (model is not null)
            {
                await BlazorMonaco.Editor.Global.SetModelLanguage(JSRuntime, model, language);
            }
        }
        catch (JSException ex)
        {
            Console.WriteLine($"Failed to set editor language: {ex.Message}");
        }
    }

    private async Task UpdateThemeAsync(string theme)
    {
        try
        {
            await BlazorMonaco.Editor.Global.SetTheme(JSRuntime, theme);
        }
        catch (JSException ex)
        {
            Console.WriteLine($"Failed to set editor theme: {ex.Message}");
        }
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
            var content = await _editor.GetValue();
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

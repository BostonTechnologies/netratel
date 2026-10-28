namespace NetRatel.API.Services.ComputerUse;

/// <summary>Explicit server opt-in. Enabling this never grants caller or endpoint permission.</summary>
public sealed class ComputerUseOptions
{
    public const string SectionName = "NetRatel:ComputerUse";
    public bool Enabled { get; set; }
}

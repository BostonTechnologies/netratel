using System;
using System.IO;

namespace NetRatel.Client.Service.ComputerUse;

/// <summary>Endpoint-local opt-in. The service supplies these settings over its authenticated helper pipe.</summary>
public sealed record ComputerUseOptions
{
    public bool Enabled { get; init; }
    public bool EnableNativeProvider { get; init; } = true;
    public string CuaDriverPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "computer-use", "cua-driver", "cua-driver.exe");
    public int OperationTimeoutSeconds { get; init; } = 20;
    public int ObservationLifetimeSeconds { get; init; } = 15;

    internal TimeSpan OperationTimeout => TimeSpan.FromSeconds(Math.Clamp(OperationTimeoutSeconds, 5, 30));
    internal TimeSpan ObservationLifetime => TimeSpan.FromSeconds(Math.Clamp(ObservationLifetimeSeconds, 1, 30));
}

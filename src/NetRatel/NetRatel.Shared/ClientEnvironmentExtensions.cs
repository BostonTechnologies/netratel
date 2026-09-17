using System;

namespace NetRatel.Shared;

public static class ClientEnvironmentExtensions
{
    public static bool Includes(this ClientEnvironment env, ClientEnvironment lane)
        => lane != ClientEnvironment.None && (env & lane) == lane;

    public static ClientEnvironment NormalizeLegacy(this int raw) => raw switch
    {
        0 => ClientEnvironment.Dev,
        1 => ClientEnvironment.Dev,
        2 => ClientEnvironment.Prod,
        3 => ClientEnvironment.Both,
        _ => ClientEnvironment.None
    };

    public static string ToShortLabel(this ClientEnvironment env) => env switch
    {
        ClientEnvironment.Dev => "Dev",
        ClientEnvironment.Prod => "Prod",
        ClientEnvironment.Both => "Dev + Prod",
        _ => "None"
    };
}

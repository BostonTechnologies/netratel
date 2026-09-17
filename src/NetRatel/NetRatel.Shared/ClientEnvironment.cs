namespace NetRatel.Shared;

[System.Flags]
public enum ClientEnvironment : int
{
    None = 0,
    Dev = 1 << 0,
    Prod = 1 << 1,
    Both = Dev | Prod
}

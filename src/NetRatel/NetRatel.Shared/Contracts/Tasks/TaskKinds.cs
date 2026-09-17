namespace NetRatel.Shared.Contracts.Tasks
{
    public static class TaskKinds
    {
        public const string ExecShellCommand = "exec-shell-cmd";
        public const string ExecLibraryScript = "exec-library-script";
        public const string OsInfo = "sys-osinfo";
        public const string ProcessesList = "sys-pslist";
        public const string ProcessesTopCpu = "sys-ps-topcpu";
        public const string DiskFree = "sys-df";

        public const string Legacy_RunPowerShell = "RunPowerShell";
        public const string Legacy_RunLibraryScript = "RunLibraryScript";
        public const string Legacy_ExecPs = "ExecPs";
        public const string Legacy_ExecSh = "ExecSh";
    }
}

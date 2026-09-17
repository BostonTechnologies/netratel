using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetRatel.Shared.Data;

public class ClientInformation
{
    // Basic OS Info (can duplicate DetectedOS or be more detailed)
    public string? OperatingSystem { get; set; }
    public string? OSDescription { get; set; } // e.g., from RuntimeInformation.OSDescription
    public string? OSArchitecture { get; set; } // e.g., from RuntimeInformation.OSArchitecture
    public string? FrameworkDescription { get; set; } // e.g., from RuntimeInformation.FrameworkDescription

    // Detailed Shell Info (Path + Version if detectable)
    public List<ShellCapability> ShellDetails { get; set; } = new();

    // Hardware Info
    public HardwareInfo? Hardware { get; set; }

    // Flattened summaries for quick UI rendering
    public List<ClientDiskSnapshot>? Disks { get; set; }
    public ClientCapabilityInfo? Capabilities { get; set; }

    public string? MachineName { get; set; }
    public string? LocalIpAddress { get; set; }
    public string? AgentVersion { get; set; }

    // Software Info (Keep simple initially)
    // public SoftwareInfo? Software { get; set; }

    // Add other sections as needed
}

public class ShellCapability
{
    public string Keyword { get; set; } = string.Empty; // "pwsh", "bash", "cmd"
    public string? Path { get; set; }
    public string? Version { get; set; } // Optional: Getting version can be complex
}

public class HardwareInfo
{
    public int ProcessorCount { get; set; }
    public string? ProcessorDetails { get; set; } // Added for Make/Model
    public long? TotalPhysicalMemoryMB { get; set; }
    public List<DiskInfo>? Disks { get; set; }
}

public class DiskInfo
{
    public string? Name { get; set; } // Device name (e.g., "sda1", "C:")
    public string? MountPoint { get; set; } // Mount point (e.g., "/", "/home", "C:\") - Added for clarity
    public string? VolumeLabel { get; set; }
    public string? DriveFormat { get; set; } // e.g., "NTFS", "ext4"

    // Store raw bytes for precision, calculate MB on demand or store both
    public long? TotalSizeBytes { get; set; }
    public long? AvailableFreeSpaceBytes { get; set; }

    // Keep MB for convenience if needed by UI, ensure calculation is safe
    public long? TotalSizeMB { get; set; }
    public long? AvailableFreeSpaceMB { get; set; }
}


public class ClientCapabilityInfo
{
    public bool WifiConnected { get; set; }
    public bool RemoteDesktopAvailable { get; set; }
    public string? RemoteSupportPreLoginSupportLevel { get; set; }
    public bool RemoteSupportPreLoginMediaSupported { get; set; }
    public bool RemoteSupportConsoleProviderScaffolded { get; set; }
    public bool RemoteSupportWindowsSessionInventorySupported { get; set; }
    public bool RemoteSupportExplicitTargetingSupported { get; set; }
    public int RemoteSupportProtocolRevision { get; set; }
    public bool RemoteSupportConsoleLoginSupported { get; set; }
    public bool RemoteSupportInteractiveAssistSupported { get; set; }
    public bool RemoteSupportTargetPreflightSupported { get; set; }
}

public class ClientDiskSnapshot
{
    public string DriveLetter { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
}

// Define SoftwareInfo later if needed
// public class SoftwareInfo { ... }

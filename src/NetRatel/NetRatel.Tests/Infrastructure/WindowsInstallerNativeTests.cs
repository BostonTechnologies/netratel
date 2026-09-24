using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Application.Artifacts;
using NetRatel.Infrastructure.Artifacts;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class WindowsInstallerNativeTests
{
    [Fact]
    [Trait("category", "hosted")]
    public async Task GeneratedPowerShellInstallerDownloadsVerifiesAndEnrollsNativePackage()
    {
        if (!OperatingSystem.IsWindows()) return;

        var packageDirectory = Environment.GetEnvironmentVariable("NETRATEL_NATIVE_CLIENT_DIRECTORY")
            ?? throw new InvalidOperationException("Set NETRATEL_NATIVE_CLIENT_DIRECTORY to the hosted Windows client package directory.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(packageDirectory, "netratel-client-manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString()!;
        Assert.Equal("win-x64", manifest.RootElement.GetProperty("runtimeId").GetString());

        var credentialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel");
        Assert.False(Directory.Exists(credentialDirectory), "the disposable Windows runner must start without a NetRatel enrollment");
        var root = Path.Combine(Path.GetTempPath(), $"netratel-windows-installer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Process? installerProcess = null;
        try
        {
            var archivePath = Path.Combine(root, "client.zip");
            ZipFile.CreateFromDirectory(packageDirectory, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            var archiveBytes = await File.ReadAllBytesAsync(archivePath, timeout.Token);
            var sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var apiBase = $"http://127.0.0.1:{port}";
            const string enrollmentCode = "ENR-SYNTHETIC-WINDOWS-INSTALLER";
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "win-x64", enrollmentCode, apiBase, DateTimeOffset.UtcNow.AddHours(1),
                InstallAsService: false, SilentInstall: true, version, sha256));
            var scriptPath = Path.Combine(root, "install.ps1");
            await File.WriteAllTextAsync(scriptPath, script, timeout.Token);

            var serving = ServePackageAndEnrollmentAsync(listener, archiveBytes, version, enrollmentCode, timeout.Token);
            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            start.Environment["ProgramFiles"] = Path.Combine(root, "ProgramFiles");
            start.Environment["ProgramData"] = Path.Combine(root, "ProgramData");
            start.Environment["TEMP"] = root;
            installerProcess = Process.Start(start)!;
            var output = installerProcess.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = installerProcess.StandardError.ReadToEndAsync(timeout.Token);
            await installerProcess.WaitForExitAsync(timeout.Token);
            await serving;
            Assert.True(installerProcess.ExitCode == 0,
                $"The Windows installer exited {installerProcess.ExitCode}: {await error}");
            Assert.Contains("NetRatel deployment complete.", await output);
            var installed = Path.Combine(root, "ProgramFiles", "NetRatel", "Client", "versions", version, "NetRatel.Client.exe");
            Assert.True(File.Exists(installed));
            Assert.True(File.Exists(Path.Combine(credentialDirectory, "agent.dat")) ||
                File.Exists(Path.Combine(root, "ProgramData", "NetRatel", "agent.dat")));
        }
        finally
        {
            if (installerProcess is { HasExited: false }) installerProcess.Kill(entireProcessTree: true);
            installerProcess?.Dispose();
            listener.Stop();
            if (Directory.Exists(credentialDirectory)) Directory.Delete(credentialDirectory, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ServePackageAndEnrollmentAsync(TcpListener listener, byte[] archive,
        string version, string enrollmentCode, CancellationToken ct)
    {
        for (var requestNumber = 0; requestNumber < 2; requestNumber++)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, ct);
            byte[] body;
            string contentType;
            if (requestNumber == 0)
            {
                Assert.StartsWith($"GET /api/v1/client-artifacts/win-x64/{version}/onboarding-download ", request);
                Assert.Contains($"X-NetRatel-Enrollment-Code: {enrollmentCode}", request, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("X-NetRatel-Tenant-Id: 4098", request, StringComparison.OrdinalIgnoreCase);
                body = archive;
                contentType = "application/zip";
            }
            else
            {
                Assert.StartsWith("POST /api/v1/agents/enroll ", request);
                Assert.Contains($"\"enrollmentCode\":\"{enrollmentCode}\"", request);
                body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    agentId = Guid.NewGuid(), refreshToken = "synthetic-native-installer-refresh-token", expiresInDays = 30
                });
                contentType = "application/json";
            }
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var buffer = new byte[4096];
        var headerEnd = -1;
        var bodyLength = 0;
        while (bytes.Count < 32 * 1024)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;
            bytes.AddRange(buffer.AsSpan(0, count).ToArray());
            if (headerEnd < 0)
            {
                headerEnd = Encoding.ASCII.GetString(bytes.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    headerEnd += 4;
                    var headers = Encoding.ASCII.GetString(bytes.ToArray(), 0, headerEnd);
                    var length = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    if (length is not null) bodyLength = int.Parse(length.Split(':', 2)[1].Trim());
                }
            }
            if (headerEnd >= 0 && bytes.Count >= headerEnd + bodyLength) break;
        }
        Assert.True(headerEnd >= 0 && bytes.Count >= headerEnd + bodyLength, "the native client sent an incomplete HTTP request");
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

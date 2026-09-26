using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
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
            var installRoot = Path.Combine(root, "client");
            start.Environment["NetRatel_ROOT"] = installRoot;
            start.Environment["NetRatel_STATE"] = Path.Combine(root, "state");
            start.Environment["NetRatel_LOG_DIR"] = Path.Combine(root, "logs");
            start.Environment["TEMP"] = root;
            installerProcess = Process.Start(start)!;
            var output = installerProcess.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = installerProcess.StandardError.ReadToEndAsync(timeout.Token);
            await installerProcess.WaitForExitAsync(timeout.Token);
            await serving;
            Assert.True(installerProcess.ExitCode == 0,
                $"The Windows installer exited {installerProcess.ExitCode}: {await error}");
            Assert.Contains("NetRatel deployment complete.", await output);
            var installed = Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe");
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

    [Fact]
    [Trait("category", "hosted")]
    [SupportedOSPlatform("windows")]
    public async Task GeneratedPowerShellInstallerPersistsApiConfigurationAcrossWindowsServiceRestart()
    {
        if (!OperatingSystem.IsWindows()) return;

        var packageDirectory = Environment.GetEnvironmentVariable("NETRATEL_NATIVE_CLIENT_DIRECTORY")
            ?? throw new InvalidOperationException("Set NETRATEL_NATIVE_CLIENT_DIRECTORY to the hosted Windows client package directory.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(packageDirectory, "netratel-client-manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString()!;
        Assert.Equal("win-x64", manifest.RootElement.GetProperty("runtimeId").GetString());

        const string serviceName = "NetRatel.Client";
        var credentialDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel");
        var credentialPath = Path.Combine(credentialDirectory, "agent.dat");
        RemoveWindowsServiceIfPresent(serviceName);
        DeleteCredentialFiles(credentialDirectory);
        Assert.False(File.Exists(credentialPath), "the disposable Windows runner must start without a NetRatel enrollment");

        var root = Path.Combine(Path.GetTempPath(), $"netratel-windows-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Process? installerProcess = null;
        Task? serving = null;
        try
        {
            var stagedPackage = Path.Combine(root, "package");
            CopyDirectory(packageDirectory, stagedPackage);
            // This file represents a pre-#118 installation. The service Environment value
            // must remain authoritative after install and after every service restart.
            await File.WriteAllTextAsync(Path.Combine(stagedPackage, "clientsettings.json"), """
                {
                  "apiBaseUrl": "http://stale.invalid"
                }
                """, timeout.Token);

            var archivePath = Path.Combine(root, "client.zip");
            ZipFile.CreateFromDirectory(stagedPackage, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            var archiveBytes = await File.ReadAllBytesAsync(archivePath, timeout.Token);
            var sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();

            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var apiBase = $"http://127.0.0.1:{port}";
            const string enrollmentCode = "ENR-SYNTHETIC-WINDOWS-SERVICE";
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "win-x64", enrollmentCode, apiBase, DateTimeOffset.UtcNow.AddHours(1),
                InstallAsService: true, SilentInstall: true, version, sha256));
            var scriptPath = Path.Combine(root, "install.ps1");
            await File.WriteAllTextAsync(scriptPath, script, timeout.Token);

            var fixture = new WindowsServiceHttpFixture(listener, archiveBytes, version, enrollmentCode);
            serving = fixture.RunAsync(timeout.Token);
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            var installRoot = Path.Combine(root, "client");
            var logDirectory = Path.Combine(root, "logs");
            start.Environment["NetRatel_ROOT"] = installRoot;
            start.Environment["NetRatel_STATE"] = Path.Combine(root, "state");
            start.Environment["NetRatel_LOG_DIR"] = logDirectory;
            start.Environment["TEMP"] = root;
            installerProcess = Process.Start(start)!;
            var outputTask = installerProcess.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = installerProcess.StandardError.ReadToEndAsync(timeout.Token);
            await installerProcess.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;

            Assert.True(installerProcess.ExitCode == 0,
                $"The Windows service installer exited {installerProcess.ExitCode}: {error}");
            Assert.Contains("PowerShell edition: Desktop", output);
            Assert.Contains("PowerShell version: 5.", output);
            Assert.Contains("NetRatel deployment complete.", output);

            var installed = Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe");
            await WaitUntilAsync(
                () => File.Exists(credentialPath) && fixture.EnrollmentRequests >= 1 && fixture.TokenRequests >= 1,
                TimeSpan.FromSeconds(45),
                timeout.Token);
            Assert.True(File.Exists(installed));
            Assert.True(new FileInfo(credentialPath).Length > 0);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(installed)!, "netratel.enroll.json")));
            Assert.Empty(fixture.UnexpectedRequests);

            using (var service = new ServiceController(serviceName))
            {
                Assert.Equal(ServiceControllerStatus.Running, service.Status);
            }

            var serviceEnvironment = ReadWindowsServiceEnvironment(serviceName);
            Assert.Contains($"NetRatelCLIENT__Client__ApiBaseUrl={apiBase}", serviceEnvironment);

            var pingsBeforeRestart = fixture.PingRequests;
            var tokensBeforeRestart = fixture.TokenRequests;
            StopWindowsService(serviceName);
            StartWindowsService(serviceName);
            await WaitUntilAsync(
                () => fixture.PingRequests > pingsBeforeRestart && fixture.TokenRequests > tokensBeforeRestart,
                TimeSpan.FromSeconds(45),
                timeout.Token);

            using (var restarted = new ServiceController(serviceName))
            {
                Assert.Equal(ServiceControllerStatus.Running, restarted.Status);
            }

            Assert.True(File.Exists(credentialPath), "the LocalSystem credential must survive a service restart");
            Assert.Contains($"NetRatelCLIENT__Client__ApiBaseUrl={apiBase}", ReadWindowsServiceEnvironment(serviceName));
            Assert.Equal(1, fixture.DownloadRequests);
            Assert.Equal(1, fixture.EnrollmentRequests);
            Assert.True(fixture.PingRequests >= 2);
            Assert.True(fixture.TokenRequests >= 2);

            StopWindowsService(serviceName);
            var logs = Directory.Exists(logDirectory)
                ? string.Join(Environment.NewLine, Directory.EnumerateFiles(logDirectory, "*.log").Select(File.ReadAllText))
                : string.Empty;
            Assert.Contains($"API={apiBase}", logs);
            Assert.Contains("Agent disabled by administrator", logs);
        }
        finally
        {
            timeout.Cancel();
            if (installerProcess is { HasExited: false }) installerProcess.Kill(entireProcessTree: true);
            installerProcess?.Dispose();
            StopWindowsService(serviceName);
            if (serving is not null)
            {
                try { await serving; }
                catch (OperationCanceledException exception)
                {
                    Debug.WriteLine($"Windows service fixture stopped during cleanup: {exception.Message}");
                }
                catch (ObjectDisposedException exception)
                {
                    Debug.WriteLine($"Windows service fixture listener was disposed during cleanup: {exception.Message}");
                }
            }
            listener.Stop();

            DeleteCredentialFiles(credentialDirectory);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            RemoveWindowsServiceIfPresent(serviceName);
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new System.TimeoutException("The hosted Windows installer condition was not observed before the timeout.");
            }

            await timer.WaitForNextTickAsync(ct);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string[] ReadWindowsServiceEnvironment(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}");
        return key?.GetValue("Environment") as string[] ?? [];
    }

    [SupportedOSPlatform("windows")]
    private static void StopWindowsService(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            if (service.Status == ServiceControllerStatus.Stopped) return;
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The service was not installed or was removed during an installer failure.
            Debug.WriteLine($"Windows service '{serviceName}' was already absent during cleanup: {exception.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void StartWindowsService(string serviceName)
    {
        using var service = new ServiceController(serviceName);
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveWindowsServiceIfPresent(string serviceName)
    {
        StopWindowsService(serviceName);
        try
        {
            using var delete = Process.Start(new ProcessStartInfo("sc.exe", $"delete {serviceName}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            delete?.WaitForExit();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The disposable runner did not have this service registered.
            Debug.WriteLine($"Windows service '{serviceName}' was not registered during cleanup: {exception.Message}");
        }
    }

    private static void DeleteCredentialFiles(string credentialDirectory)
    {
        foreach (var fileName in new[] { "agent.dat", ".netratel-credential-machine-id" })
        {
            var path = Path.Combine(credentialDirectory, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class WindowsServiceHttpFixture
    {
        private readonly TcpListener _listener;
        private readonly byte[] _archive;
        private readonly string _version;
        private readonly string _enrollmentCode;

        public WindowsServiceHttpFixture(TcpListener listener, byte[] archive, string version, string enrollmentCode)
        {
            _listener = listener;
            _archive = archive;
            _version = version;
            _enrollmentCode = enrollmentCode;
        }

        public int DownloadRequests { get; private set; }
        public int EnrollmentRequests { get; private set; }
        public int PingRequests { get; private set; }
        public int TokenRequests { get; private set; }
        public List<string> UnexpectedRequests { get; } = [];

        public async Task RunAsync(CancellationToken ct)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                {
                    return;
                }

                using (client)
                await using (var stream = client.GetStream())
                {
                    var request = await ReadRequestAsync(stream, ct);
                    var firstLine = request[..request.IndexOf("\r\n", StringComparison.Ordinal)];
                    if (firstLine.StartsWith($"GET /api/v1/client-artifacts/win-x64/{_version}/onboarding-download ", StringComparison.Ordinal))
                    {
                        DownloadRequests++;
                        await WriteResponseAsync(stream, 200, "OK", "application/zip", _archive, ct);
                    }
                    else if (firstLine.StartsWith("POST /api/v1/agents/enroll ", StringComparison.Ordinal))
                    {
                        EnrollmentRequests++;
                        if (!request.Contains($"\"enrollmentCode\":\"{_enrollmentCode}\"", StringComparison.Ordinal))
                        {
                            UnexpectedRequests.Add(firstLine);
                        }

                        var body = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            agentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                            refreshToken = "synthetic-windows-service-refresh-token",
                            expiresInDays = 30
                        });
                        await WriteResponseAsync(stream, 200, "OK", "application/json", body, ct);
                    }
                    else if (firstLine.StartsWith("GET /api/v1/agent-auth/ping ", StringComparison.Ordinal))
                    {
                        PingRequests++;
                        await WriteResponseAsync(stream, 401, "Unauthorized", "text/plain", [], ct);
                    }
                    else if (firstLine.StartsWith("POST /api/v1/agents/token ", StringComparison.Ordinal))
                    {
                        TokenRequests++;
                        var body = Encoding.UTF8.GetBytes("{\"title\":\"Agent disabled\",\"detail\":\"synthetic disabled state\",\"code\":\"agent_disabled\"}");
                        await WriteResponseAsync(stream, 403, "Forbidden", "application/problem+json", body, ct);
                    }
                    else
                    {
                        UnexpectedRequests.Add(firstLine);
                        await WriteResponseAsync(stream, 404, "Not Found", "text/plain", [], ct);
                    }
                }
            }
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            int statusCode,
            string reason,
            string contentType,
            byte[] body,
            CancellationToken ct)
        {
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }
    }
}

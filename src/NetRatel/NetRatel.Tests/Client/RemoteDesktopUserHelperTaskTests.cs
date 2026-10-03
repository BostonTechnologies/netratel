using FluentAssertions;
using NetRatel.Client.Service.RemoteDesktop;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteDesktopUserHelperTaskTests
{
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    // XML/unit tests are offline. Native tests use TASK_VALIDATE_ONLY and
    // never register/run a task, write a launcher, or capture an interactive session.
#pragma warning disable CA1416
    [Fact]
    public void GeneratedPrincipal_ValidatesMicrosoftSchemaAndKeepsInteractiveGroupLeastPrivilege()
    {
        var document = XDocument.Parse(RemoteDesktopUserHelperTask.BuildTaskXml("wscript.exe", "//B \"helper.vbs\""));
        var principal = document.Root!.Element(TaskNamespace + "Principals")!.Element(TaskNamespace + "Principal")!;

        ValidatePrincipal(principal).Should().BeEmpty();
        principal.Element(TaskNamespace + "GroupId")!.Value.Should().Be("S-1-5-32-545");
        principal.Element(TaskNamespace + "RunLevel")!.Value.Should().Be("LeastPrivilege");
        principal.Element(TaskNamespace + "LogonType").Should().BeNull();
        principal.Element(TaskNamespace + "UserId").Should().BeNull();
        document.Root.Element(TaskNamespace + "Actions")!.Attribute("Context")!.Value.Should().Be(principal.Attribute("id")!.Value);
        document.Root.Element(TaskNamespace + "Triggers")!.Element(TaskNamespace + "LogonTrigger")!
            .Element(TaskNamespace + "Enabled")!.Value.Should().Be("true");
    }

    [Fact]
    public void PreviousGroupLogonType_IsRejectedByMicrosoftXmlEnumeration()
    {
        var document = XDocument.Parse(RemoteDesktopUserHelperTask.BuildTaskXml("wscript.exe", "//B \"helper.vbs\""));
        var principal = document.Root!.Element(TaskNamespace + "Principals")!.Element(TaskNamespace + "Principal")!;
        principal.Add(new XElement(TaskNamespace + "LogonType", "Group"));

        ValidatePrincipal(principal).Should().Contain(error => error.Contains("Group", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wscript.exe", "//B \"C:\\ProgramData\\NetRatel\\remote desktop\\helper.vbs\"")]
    [InlineData("command & <name> \"quoted\"", "//B \"A&B <folder>\\helper.vbs\" 'text'")]
    public void GeneratedAction_PreservesCommandAndArgumentsThroughXmlEscaping(string command, string arguments)
    {
        var document = XDocument.Parse(RemoteDesktopUserHelperTask.BuildTaskXml(command, arguments));
        var exec = document.Root!.Element(TaskNamespace + "Actions")!.Element(TaskNamespace + "Exec")!;

        exec.Element(TaskNamespace + "Command")!.Value.Should().Be(command);
        exec.Element(TaskNamespace + "Arguments")!.Value.Should().Be(arguments);
    }

    [Fact]
    public void RegistrationChild_KeepsExecutablePathSeparateAndHasOneNarrowCommand()
    {
        const string executablePath = "C:\\Program Files\\NetRatel\\NetRatel.Client.exe";
        var startInfo = RemoteDesktopUserHelperTask.BuildRegistrationStartInfo(executablePath);

        startInfo.FileName.Should().Be(executablePath);
        startInfo.ArgumentList.Should().Equal(RemoteDesktopUserHelperConstants.RegistrationCommand);
        startInfo.Arguments.Should().BeEmpty();
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.RedirectStandardError.Should().BeTrue();
        startInfo.RedirectStandardOutput.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("//B \"C:\\ProgramData\\NetRatel & équipe 漢字\\helper.vbs\"")]
    [Trait("category", "hosted")]
    public void NativeTaskScheduler_ValidatesCompleteProductionUnicodeHandoff(string? arguments)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Task Scheduler Unicode handoff requires the native Windows lane."); return; }
        NetRatelWindowsServiceHost.ShouldRunAsService([RemoteDesktopUserHelperConstants.RegistrationCommand]).Should().BeFalse();
        var xml = arguments is null
            ? RemoteDesktopUserHelperTask.BuildCurrentTaskXml()
            : RemoteDesktopUserHelperTask.BuildTaskXml("wscript.exe", arguments);
        var document = XDocument.Parse(xml);
        document.Declaration!.Encoding.Should().Be("UTF-16");
        var principal = document.Root!.Element(TaskNamespace + "Principals")!.Element(TaskNamespace + "Principal")!;
        principal.Element(TaskNamespace + "GroupId")!.Value.Should().Be("S-1-5-32-545");
        principal.Element(TaskNamespace + "RunLevel")!.Value.Should().Be("LeastPrivilege");
        principal.Element(TaskNamespace + "UserId").Should().BeNull();
        document.Root.Element(TaskNamespace + "Settings")!.Element(TaskNamespace + "MultipleInstancesPolicy")!.Value.Should().Be("IgnoreNew");
        var actualArguments = document.Root.Element(TaskNamespace + "Actions")!.Element(TaskNamespace + "Exec")!
            .Element(TaskNamespace + "Arguments")!.Value;
        actualArguments.Should().Be(arguments ?? $"//B \"{RemoteDesktopUserHelperTask.GetLauncherPath()}\"");

        RemoteDesktopUserHelperTask.RegisterTaskXml(xml, validateOnly: true);
    }

    [Fact]
    [Trait("category", "hosted")]
    public void NativeTaskScheduler_RejectsPreviousInvalidGroupLogonTypeWithoutRegistration()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Task Scheduler Unicode handoff requires the native Windows lane."); return; }
        var xml = RemoteDesktopUserHelperTask.BuildTaskXml("wscript.exe", "//B \"helper.vbs\"");
        var document = XDocument.Parse(xml);
        document.Root!.Element(TaskNamespace + "Principals")!.Element(TaskNamespace + "Principal")!
            .Add(new XElement(TaskNamespace + "LogonType", "Group"));
        Action validate = () => RemoteDesktopUserHelperTask.RegisterTaskXml(document.Declaration + "\n" + document, validateOnly: true);

        validate.Should().Throw<COMException>();
    }
#pragma warning restore CA1416

    private static List<string> ValidatePrincipal(XElement principal)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var schemaPath = Path.Combine(repositoryRoot,
            "src/NetRatel/NetRatel.Tests/Client/Fixtures/TaskSchedulerPrincipal.xsd");
        var schemas = new XmlSchemaSet { XmlResolver = null };
        using var schemaReader = XmlReader.Create(schemaPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        schemas.Add(TaskNamespace.NamespaceName, schemaReader);
        schemas.Compile();
        var errors = new List<string>();
        new XDocument(new XElement(principal)).Validate(schemas, (_, error) => errors.Add(error.Message));
        return errors;
    }
}

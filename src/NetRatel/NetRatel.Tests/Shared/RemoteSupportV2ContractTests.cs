using System.Text.Json;
using System.Text;
using FluentAssertions;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Shared;

public sealed class RemoteSupportV2ContractTests
{
    [Fact]
    public void Interactive_open_contract_requires_an_operator_exact_target_and_capability()
    {
        var command = OpenInteractive();

        RemoteSupportV2ContractValidator.TryValidate(command, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void Console_target_cannot_silently_become_an_interactive_target()
    {
        var command = OpenInteractive() with
        {
            Target = new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.Console, WindowsSessionId: 4, UserSidHash: "sid-hash")
        };

        RemoteSupportV2ContractValidator.TryValidate(command, out var error).Should().BeFalse();
        error!.Code.Should().Be("console_target_invalid");
    }

    [Fact]
    public void Console_login_is_an_explicit_v2_target_while_the_legacy_console_spelling_remains_readable()
    {
        var command = OpenInteractive() with
        {
            Target = new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.ConsoleLogin)
        };

        RemoteSupportV2ContractValidator.TryValidate(command, out _).Should().BeTrue();
        RemoteSupportV2TargetKinds.IsConsoleLogin(RemoteSupportV2TargetKinds.Console).Should().BeTrue();
        RemoteSupportV2TargetKinds.IsConsoleLogin(RemoteSupportV2TargetKinds.ConsoleLogin).Should().BeTrue();
    }

    [Fact]
    public void Open_idempotency_key_is_stable_for_the_tenant_agent_and_request()
    {
        var command = OpenInteractive();
        var changedTarget = command with
        {
            Target = command.Target with { WindowsSessionId = 8, UserSidHash = "different-sid" }
        };

        RemoteSupportV2ContractValidator.OpenIdempotencyKey(changedTarget)
            .Should().Be(RemoteSupportV2ContractValidator.OpenIdempotencyKey(command));
    }

    [Fact]
    public void Control_and_resume_contracts_require_the_operator_bound_session_identity()
    {
        var session = new RemoteSupportSessionKey(7, Guid.NewGuid(), Guid.NewGuid());
        var operatorBinding = new RemoteSupportOperatorBinding("operator-42");
        var control = new RemoteSupportControlCommand(1, session, operatorBinding, Guid.NewGuid(), RemoteSupportV2ControlTypes.Close, 3, DateTimeOffset.UtcNow);
        var resume = new RemoteSupportResumeRequest(1, session, operatorBinding, 6, Guid.NewGuid(), DateTimeOffset.UtcNow);

        RemoteSupportV2ContractValidator.TryValidate(control, out _).Should().BeTrue();
        RemoteSupportV2ContractValidator.TryValidate(resume, out _).Should().BeTrue();

        RemoteSupportV2ContractValidator.TryValidate(control with { Operator = new RemoteSupportOperatorBinding(" ") }, out var error)
            .Should().BeFalse();
        error!.Code.Should().Be("operator_invalid");
    }

    [Fact]
    public void Capability_advertisement_is_versioned_bounded_and_expiring()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var advertisement = new RemoteSupportCapabilityAdvertisement(
            RemoteSupportV2ContractVersions.Current,
            7,
            Guid.NewGuid(),
            ["interactive_desktop", "console_login"],
            observedAt,
            observedAt.AddMinutes(5));

        RemoteSupportV2ContractValidator.TryValidate(advertisement, out _).Should().BeTrue();
        RemoteSupportV2ContractValidator.TryValidate(advertisement with { Capabilities = ["interactive_desktop", "INTERACTIVE_DESKTOP"] }, out var error)
            .Should().BeFalse();
        error!.Code.Should().Be("capabilities_invalid");
    }

    [Fact]
    public void Audit_contract_is_camel_case_tolerant_of_future_fields_and_contains_no_media_payload()
    {
        var audit = new RemoteSupportAuditEvent(
            1,
            new RemoteSupportSessionKey(7, Guid.NewGuid(), Guid.NewGuid()),
            4,
            Guid.NewGuid(),
            RemoteSupportV2AuditEventTypes.LifecycleChanged,
            "operator",
            "operator-42",
            Guid.NewGuid(),
            "accepted",
            null,
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(audit, RemoteSupportV2JsonContext.Default.RemoteSupportAuditEvent);
        json.Should().Contain("auditSequence");
        var normalizedJson = json.ToLowerInvariant();
        normalizedJson.Should().NotContain("sdp");
        normalizedJson.Should().NotContain("ice");
        normalizedJson.Should().NotContain("payload");

        var roundTrip = JsonSerializer.Deserialize($"{json[..^1]},\"futureField\":true}}", RemoteSupportV2JsonContext.Default.RemoteSupportAuditEvent);
        roundTrip.Should().BeEquivalentTo(audit);
    }

    [Fact]
    public void Contract_surface_does_not_expose_media_or_credential_members()
    {
        Type[] contractTypes =
        [
            typeof(RemoteSupportOpenSessionCommand),
            typeof(RemoteSupportSessionSnapshot),
            typeof(RemoteSupportSessionStatus),
            typeof(RemoteSupportControlCommand),
            typeof(RemoteSupportResumeRequest),
            typeof(RemoteSupportCapabilityAdvertisement),
            typeof(RemoteSupportAuditEvent)
        ];
        var names = contractTypes
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToArray();

        names.Should().NotContain(name =>
            name.Contains("sdp", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ice", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("payload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Protobuf_v2_contracts_are_additive_and_exclude_transient_media_fields()
    {
        var fields = RemoteSupportV2OpenSession.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => field.Name)
            .ToArray();
        var auditFields = RemoteSupportV2AuditEvent.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => field.Name)
            .ToArray();

        fields.Should().Equal(
            "contract_version", "tenant_id", "agent_id", "request_id", "initiating_operator", "target",
            "requested_capabilities", "requested_unix_ms", "expires_unix_ms", "has_expires_unix_ms");
        auditFields.Should().NotContain(field =>
            field.Contains("sdp", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("ice", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Typed_negotiation_is_bounded_directional_and_not_a_durable_contract_member()
    {
        var session = new RemoteSupportSessionKey(7, Guid.NewGuid(), Guid.NewGuid());
        var offer = new NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportV2NegotiationEnvelope(
            session, 1, RemoteSupportV2NegotiationDirections.Browser, 1, Guid.NewGuid(),
            RemoteSupportV2NegotiationSignalTypes.Offer, Encoding.UTF8.GetBytes("{\"type\":\"offer\"}"));

        RemoteSupportV2ContractValidator.TryValidate(offer, out _).Should().BeTrue();
        RemoteSupportV2ContractValidator.TryValidate(offer with { Direction = RemoteSupportV2NegotiationDirections.Agent }, out var error)
            .Should().BeFalse();
        error!.Code.Should().Be("negotiation_offer_direction_invalid");
        RemoteSupportV2ContractValidator.TryValidate(offer with { Payload = new byte[RemoteSupportV2ContractValidator.MaximumNegotiationPayloadBytes + 1] }, out error)
            .Should().BeFalse();
        error!.Code.Should().Be("negotiation_envelope_invalid");

        typeof(RemoteSupportSessionSnapshot).GetProperties().Select(property => property.Name)
            .Should().NotContain(name => name.Contains("negotiation", StringComparison.OrdinalIgnoreCase));
        RemoteSupportV2RouteEnvelope.Descriptor.Fields.InFieldNumberOrder().Select(field => field.Name)
            .Should().Contain("negotiation");
    }

    private static RemoteSupportOpenSessionCommand OpenInteractive() => new(
        RemoteSupportV2ContractVersions.Current,
        7,
        Guid.NewGuid(),
        Guid.NewGuid(),
        new RemoteSupportOperatorBinding("operator-42"),
        new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, 4, "sid-hash", 21),
        ["view", "control"],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(30));
}

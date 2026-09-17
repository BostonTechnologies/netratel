using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class ProductionActivationManifestTests
{
    private static readonly string[] RequiredOwnerDecisionLines =
    [
        "  candidate_source_sha: OWNER_DECISION_REQUIRED",
        "  candidate_image_tag: OWNER_DECISION_REQUIRED",
        "  candidate_image_digest: OWNER_DECISION_REQUIRED",
        "    rehearsal_evidence: OWNER_DECISION_REQUIRED",
        "    rollback_migration_procedure: OWNER_DECISION_REQUIRED",
        "  config_path: OWNER_DECISION_REQUIRED",
        "  secret_reference_owner: OWNER_DECISION_REQUIRED",
        "  outbound_app_identity: OWNER_DECISION_REQUIRED",
        "  production_api_target: OWNER_DECISION_REQUIRED",
        "  canonical_mcp_resource_uri: OWNER_DECISION_REQUIRED",
        "  dns_owner: OWNER_DECISION_REQUIRED",
        "  tls_certificate_owner: OWNER_DECISION_REQUIRED",
        "  traefik_route: OWNER_DECISION_REQUIRED",
        "  oidc_provider: OWNER_DECISION_REQUIRED",
        "  oidc_application: OWNER_DECISION_REQUIRED",
        "  audience: OWNER_DECISION_REQUIRED",
        "    observer: OWNER_DECISION_REQUIRED",
        "    operator: OWNER_DECISION_REQUIRED",
        "    automation_operator: OWNER_DECISION_REQUIRED",
        "    onboarding_operator: OWNER_DECISION_REQUIRED",
        "    policy_administrator: OWNER_DECISION_REQUIRED",
        "  redirect_uris: OWNER_DECISION_REQUIRED",
        "  signing_identity: OWNER_DECISION_REQUIRED",
        "  signing_key_reference: OWNER_DECISION_REQUIRED",
        "  key_rotation_owner: OWNER_DECISION_REQUIRED",
        "  review_status: OWNER_DECISION_REQUIRED",
        "  allow_policies: OWNER_DECISION_REQUIRED",
        "  deny_policies: OWNER_DECISION_REQUIRED",
        "  principals_or_groups: OWNER_DECISION_REQUIRED",
        "  tenant_selectors: OWNER_DECISION_REQUIRED",
        "  target_selectors: OWNER_DECISION_REQUIRED",
        "  allowed_operation_families: OWNER_DECISION_REQUIRED",
        "  allowed_file_read_roots: OWNER_DECISION_REQUIRED",
        "  allowed_file_write_roots: OWNER_DECISION_REQUIRED",
        "  allowed_shells: OWNER_DECISION_REQUIRED",
        "  maximum_fan_out: OWNER_DECISION_REQUIRED",
        "  command_timeout: OWNER_DECISION_REQUIRED",
        "  destructive_operations_allowed: OWNER_DECISION_REQUIRED",
        "  expiry_and_review_date: OWNER_DECISION_REQUIRED",
        "  tenant: OWNER_DECISION_REQUIRED",
        "  agent_id: OWNER_DECISION_REQUIRED",
        "  allowed_read_operations: OWNER_DECISION_REQUIRED",
        "  allowed_mutation_operations: OWNER_DECISION_REQUIRED",
        "  image_digest: OWNER_DECISION_REQUIRED",
        "  command_or_procedure: OWNER_DECISION_REQUIRED",
        "  owner: OWNER_DECISION_REQUIRED",
        "  verification: OWNER_DECISION_REQUIRED",
        "  approvers: OWNER_DECISION_REQUIRED"
    ];

    [Fact]
    public void Production_activation_manifest_remains_an_unresolved_owner_decision()
    {
        var manifest = ReadManifest();

        manifest.Should().Contain("schema: netratel.mcp.production-activation-manifest/v1");
        manifest.Should().Contain("status: owner_input_required");

        foreach (var ownerDecision in RequiredOwnerDecisionLines)
        {
            manifest.Should().Contain(ownerDecision);
        }
    }

    [Fact]
    public void Production_activation_manifest_prohibits_dev_reuse_and_enabling_actions()
    {
        var manifest = ReadManifest();

        manifest.Should().Contain("dev_api_url: prohibited");
        manifest.Should().Contain("dev_credentials: prohibited");
        manifest.Should().Contain("dev_signing_identity: prohibited");
        manifest.Should().Contain("inbound_bearer_forwarding: prohibited");
        manifest.Should().Contain("blanket_allow_all_policy: prohibited");
        manifest.Should().Contain("production_objects_created_disabled: false");
        manifest.Should().Contain("production_consumer_authorized: false");
        manifest.Should().Contain("target_policy_installed: false");
        manifest.Should().Contain("live_actions_authorized: false");
        manifest.Should().Contain("production_write_executed: false");
        manifest.Should().Contain("production_target_authorized: false");
        manifest.Should().Contain("usable_production_operator_grant_issued: false");
        manifest.Should().Contain("production_ingress_enabled: false");
    }

    [Fact]
    public void Production_activation_manifest_declares_the_reviewed_scope_set()
    {
        var manifest = ReadManifest();

        manifest.Should().Contain("- netratel.mcp.read");
        manifest.Should().Contain("- netratel.mcp.observe");
        manifest.Should().Contain("- netratel.mcp.files");
        manifest.Should().Contain("- netratel.mcp.write");
        manifest.Should().Contain("- netratel.mcp.execute");
        manifest.Should().Contain("- netratel.mcp.onboarding");
        manifest.Should().Contain("- netratel.mcp.admin");
        manifest.Should().Contain("- offline_access");
    }

    private static string ReadManifest()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        return File.ReadAllText(Path.Combine(repositoryRoot, "docs", "mcp-http", "production-activation-manifest.yml"));
    }
}

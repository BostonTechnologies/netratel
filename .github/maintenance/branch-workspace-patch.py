from pathlib import Path

def change(path, old, new, count=1):
    p=Path(path); s=p.read_text(); assert s.count(old)==count, (path,s.count(old),old[:70]); p.write_text(s.replace(old,new))

p='src/NetRatel/NetRatel.Application/Operations/McpOperatorContracts.cs'
needle='''    Task<McpOperatorPolicy?> DisableAsync(
        Guid policyId,'''
insert='''    /// <summary>
    /// Trusted account-adapter overloads. The caller must establish current
    /// instance authority before preview and again immediately before persistence.
    /// They preserve the real audit actor; ordinary callers keep self-grant checks.
    /// Custom implementations fail closed until they support account authority.
    /// </summary>
    Task ValidateDraftAsync(McpOperatorPolicyDraft draft, string actorId,
        CancellationToken cancellationToken, McpOperatorAccountAuthority? accountAuthority) =>
        accountAuthority is null ? ValidateDraftAsync(draft, actorId, cancellationToken)
            : throw new InvalidOperationException("Instance-account policy administration is not supported.");

    Task<McpOperatorPolicy> CreateAsync(McpOperatorPolicyDraft draft, string actorId,
        CancellationToken cancellationToken, McpOperatorAccountAuthority? accountAuthority) =>
        accountAuthority is null ? CreateAsync(draft, actorId, cancellationToken)
            : throw new InvalidOperationException("Instance-account policy administration is not supported.");

    Task<McpOperatorPolicy> ReplaceAsync(Guid policyId, long expectedVersion,
        McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken,
        McpOperatorAccountAuthority? accountAuthority) =>
        accountAuthority is null ? ReplaceAsync(policyId, expectedVersion, draft, actorId, cancellationToken)
            : throw new InvalidOperationException("Instance-account policy administration is not supported.");

'''
change(p,needle,insert+needle)
p='src/NetRatel/NetRatel.Infrastructure/Services/McpOperatorPolicyAdministration.cs'
for name,returns,args,params in [
 ('CreateAsync','Task<McpOperatorPolicy>', 'McpOperatorPolicyDraft draft,\n        string actorId,\n        CancellationToken cancellationToken','draft, actorId, cancellationToken'),
 ('ValidateDraftAsync','Task','McpOperatorPolicyDraft draft,\n        string actorId,\n        CancellationToken cancellationToken','draft, actorId, cancellationToken'),
 ('ReplaceAsync','Task<McpOperatorPolicy>','Guid policyId,\n        long expectedVersion,\n        McpOperatorPolicyDraft draft,\n        string actorId,\n        CancellationToken cancellationToken','policyId, expectedVersion, draft, actorId, cancellationToken')]:
    old=f'    public async {returns} {name}(\n        {args})\n    {{'
    new=f'''    public {returns} {name}(\n        {args}) =>\n        {name}({params}, null);\n\n    public async {returns} {name}(\n        {args},\n        McpOperatorAccountAuthority? accountAuthority)\n    {{'''
    change(p,old,new)
change(p,'await ValidateDraftAsync(draft, actorId, cancellationToken).ConfigureAwait(false);','await ValidateDraftAsync(draft, actorId, cancellationToken, accountAuthority).ConfigureAwait(false);',2)
change(p,'        ValidateDraft(draft, actorId);','''        if (accountAuthority is not null && !IsSafeValue(accountAuthority.PrincipalId, MaxIdentityLength))
            throw new ArgumentException("A valid verified instance-account identity is required.", nameof(accountAuthority));
        ValidateDraft(draft, actorId, allowSelfGrant: accountAuthority is not null);''')
change(p,'private static void ValidateDraft(McpOperatorPolicyDraft draft, string actorId)','private static void ValidateDraft(McpOperatorPolicyDraft draft, string actorId, bool allowSelfGrant = false)')
change(p,'        if (draft.Effect == McpOperatorPolicyEffect.Allow &&\n            draft.PrincipalSelector.Kind == McpOperatorPrincipalSelectorKind.OAuthSubject &&','        if (!allowSelfGrant && draft.Effect == McpOperatorPolicyEffect.Allow &&\n            draft.PrincipalSelector.Kind == McpOperatorPrincipalSelectorKind.OAuthSubject &&')
p='src/NetRatel/NetRatel.API/Endpoints/Client/McpOperatorPolicyMutationEndpoints.cs'
change(p,'administration.ValidateDraftAsync(request.Policy, context.Principal.Subject, cancellationToken)','administration.ValidateDraftAsync(request.Policy, context.Principal.Subject, cancellationToken, decision.AccountAuthority)',2)
change(p,'administration.ValidateDraftAsync(request.Policy!, context.Principal.Subject, cancellationToken)','administration.ValidateDraftAsync(request.Policy!, context.Principal.Subject, cancellationToken, decision.AccountAuthority)',2)
change(p,'administration.CreateAsync(request.Policy, context.Principal.Subject, mutationToken)','administration.CreateAsync(request.Policy, context.Principal.Subject, mutationToken, decision.AccountAuthority)')
change(p,'                    request.Policy!, context.Principal.Subject, mutationToken),','                    request.Policy!, context.Principal.Subject, mutationToken, decision.AccountAuthority),')

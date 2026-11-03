using System.Runtime.CompilerServices;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Branding;

// Preserve existing assembly-qualified contract identities while Web consumes Shared directly.
[assembly: TypeForwardedTo(typeof(IntegrationCredentialPurpose))]
[assembly: TypeForwardedTo(typeof(IntegrationCredentialGrantRequest))]
[assembly: TypeForwardedTo(typeof(IntegrationCredentialSummary))]
[assembly: TypeForwardedTo(typeof(BrandingValueSource))]
[assembly: TypeForwardedTo(typeof(BrandingField))]
[assembly: TypeForwardedTo(typeof(EffectiveDeploymentBranding))]
[assembly: TypeForwardedTo(typeof(BrandingFieldUpdate))]
[assembly: TypeForwardedTo(typeof(UpdateDeploymentBrandingRequest))]
[assembly: TypeForwardedTo(typeof(BrandingAssetUploadResult))]

# Deployment branding

NetRatel uses its approved mark, wordmark, splash, favicon and PWA assets by
default. Branding changes only visible presentation; it does not rename the
product in assemblies, packages, cookies, Data Protection, token audiences,
agent protocols, native Client enrollment, or MCP resources.

## Precedence and administration

Every field resolves independently in this order:

1. an explicit `Branding` deployment configuration value;
2. a durable instance-administrator override; then
3. the bundled NetRatel default.

Do not add a placeholder `Branding` section to tracked configuration. An absent
field is intentionally inherited, while a configured field is deployment-owned
and cannot be changed in the UI. Configure only fields that deployment policy
owns, for example:

```json
{
  "Branding": {
    "ApplicationName": "Example Operations",
    "SupportUrl": "https://support.example.test/netratel"
  }
}
```

`ApplicationName`, `OrganizationName`, `Tagline`, `LogoLightUrl`,
`LogoDarkUrl`, `CompactLogoUrl`, `FaviconUrl`, `SupportUrl`, and `SiteUrl` are
supported. URL fields must be HTTPS URLs or root-relative paths. Deployment
configuration is validated on API startup.

An instance administrator opens **Deployment branding** at `/admin/branding`.
The editor displays whether each value is inherited, stored, or deployment
managed. Save changes only the edited field. Discard restores the editor draft;
reset clears the stored override and exposes the configured/default value.

## Managed assets

The editor uploads only PNG, JPEG, and ICO images, up to 256 KiB and with
dimensions between 16 and 2048 pixels. Content type, signature, and dimensions
are checked; SVG, HTML, remote URL fetching, arbitrary filesystem paths, and
active formats are rejected. Assets are stored in the selected NetRatel
database, survive container replacement, and receive a versioned public URL
after every change so browser caches refresh safely.

The public branding projection contains presentation values only. It contains no
administrator identity, deployment secrets, configuration source content, or
credential data. The `InstanceAdministrator` authorization boundary protects
all changes and uploads; tenant-scoped roles cannot edit global branding.

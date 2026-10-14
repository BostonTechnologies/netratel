# NetRatel public branding

The complete approved public pack is canonically stored in
`docs/assets/netratel/brand-pack/`. Its `asset-manifest.json` records every
approved image, its dimensions, format, and intended use. The artwork was
copied unchanged from the approved NetRatel brand pack; the source pack is not
part of the application runtime and is never edited by this repository.

The Web application deliberately serves only its runtime subset from
`src/NetRatel/NetRatel.Web/wwwroot/brand/`: small PNG browser marks, Apple and
install icons, the 600px WebP navbar wordmark, and 1280/1600px WebP login
splashes. `favicon.ico` stays at the Web root as the browser contract, and
`site.webmanifest` references the 192px and 512px install icons. No service
worker or offline claim is implied.

| Purpose | Canonical asset | Runtime/reference |
| --- | --- | --- |
| README | `netratel-readme-hero.webp` | `docs/assets/netratel/netratel-readme-hero.webp` |
| GitHub social source | `netratel-social-preview.png` | `docs/assets/netratel/netratel-social-preview.png` |
| Application/browser mark | `netratel-mark-32.png`, `netratel-mark-64.png` | Web `brand/` |
| Browser favicon | `favicon.ico` | Web root `favicon.ico` |
| Apple icon | `apple-touch-icon.png` | Web `brand/` |
| Install icons | `pwa-192x192.png`, `pwa-512x512.png` | Web `brand/`, `site.webmanifest` |
| Navbar wordmark | `netratel-wordmark-600.webp` | Web `brand/` |
| Login splash | `netratel-splash-1600.webp`, `netratel-splash-1280.webp` | Web `brand/`; responsive public/login background |
| Email/integration | `netratel-email-mark.png`, `netratel-email-wordmark.png` | Canonical pack only; reserved for a real email/integration consumer |

The README hero and social-preview source remain at their existing direct
documentation paths for stable links. A repository owner must upload the
social-preview source through GitHub's repository settings; this repository,
CI, and release workflows do not mutate that setting.

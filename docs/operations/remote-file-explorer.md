# Basic and Full remote file explorers

The client-card folder button opens **Basic Explorer** until you explicitly choose **Full Explorer** in its adjacent menu. Disk-row entry uses the same preference and preserves the starting disk path. Both choices remain available. Preferences/layout are local to this origin, signed-in user and tenant, without file paths, content or credentials.

Basic retains listing/navigation, folder creation, empty-folder deletion, streaming upload/download and bounded text preview/edit/save, including retained V1 compatibility. Full supplies a fullscreen folder tree, sortable details/tiles, Preview/Details dock and read-only fullscreen content viewer. All operations run on the selected persisted native agent through the existing file channel. The Web disk is never the remote provider.

## Access and operations

Every request checks tenant/agent identity and effective `file.read`, `file.write` and `file.delete` grants, including valid instance administrators. Move/replace require write and delete. Granular file grants do not also require the Operator role. MCP keeps its separate admission policy.

Full intersects permissions with negotiated capabilities: `file-roots-v1`, `file-stat-v1`, `file-create-v1`, `file-move-entry-v1`, `file-delete-recursive-v1`, `file-read-range-v1`, `file-write-conditional-v1`, and existing folder/read/write/delete capabilities. Unsupported commands are disabled. Older clients retain Basic and supported Full browsing; new previews/commands need the corresponding capability. Update the native client for the complete workspace.

Directory loading is lazy, immediate-child only, without a recursive scan or per-entry stat/content fan-out. A directory over 4,096 entries produces a visible limit error. Sorting, filtering and virtualization cover the loaded directory; they do not provide remote pagination. Root discovery returns native volumes without traversing them. UNC is unsupported.

- **New folder / New empty file** is create-only, including genuine zero-byte creation.
- **Upload** streams browser files, including zero bytes, with a 16 GiB limit per Full upload. Existing names conflict by default. Select one file, enable replacement and confirm its path to replace it; the observed version is checked before staged commit.
- **Rename / Move to / internal drag onto a folder** performs exclusive same-volume native rename/move. Collisions and cross-volume moves are refused without a copy/delete fallback. Windows case-only rename is supported; POSIX case and significant spaces are preserved.
- **Delete** confirms selected top-level paths. Directory confirmation explicitly includes descendants without pre-scanning to count them. Native traversal protects roots, nested volumes and links/reparse points, checks cancellation, and stops at 100,000 deleted entries / 128 levels. Basic remains empty-directory-only.
- **Download file** uses browser-native HTTP streaming, progress and cancel, without a large MudEx BlobUrl.

Choose at most 128 top-level items per mutation. Roots, overlaps and directory moves into themselves/descendants are refused. Batches are not transactions: completed/failed/cancelled/unknown items can coexist. Errors report paths, native codes and known affected-entry counts. Cancellation can leave a partial tree. Source/destination parents are re-listed after batch attempts, including partial/unknown outcomes; refresh failures stay visible. Lost responses after dispatch are unknown. Inspect refreshed state before any manual retry. Mutations are never automatically replayed. Failed/cancelled upload sources clean native staging; committed work is not rolled back.

## Navigation, layout and lifetime

Use the path field/breadcrumbs, Go, Up and Refresh. Switch **Details / tiles**. Click selects; Ctrl/Command-click toggles; Shift-click selects a range; dragging a rectangle selects files. The grid supports arrows, Shift-arrows, Ctrl/Command-A, Enter, F2 rename, Delete with confirmation, and Escape. Toolbar/context actions provide alternatives. Rename preserves entered significant spaces.

Select one file for preview; double-click or **Open fullscreen** creates an independent viewer. **Return to Explorer** keeps directory/selection and restores scroll/focus. Full content is read-only; Basic retains its supported editing.

Drag dock headers to stack/move and dividers to resize. Use Float preview, Pop out / rejoin, Save layout and Reset layout. Permit popups for this instance. Saving first rejoins external panels. Narrow screens use separate Preview/Details tabs. Closing ends owned work/resources; a circuit disconnect cancels native work/content leases. Reopen Full after reconnection.

Whole-file preview defaults to 8 MiB via `FileBrowser:MaxPreviewBytes` (1–32 MiB). Acquisition checks observed bytes and re-stats afterwards. Missing/denied/changed/malformed reads show errors, not empty files. Zero bytes has an explicit empty state. Text excerpts are labelled at 512 Ki characters within that whole-file cap.

Eligible PDF/audio/video uses authenticated same-origin native ranges. Independent consumer leases bind operator, tenant, agent, exact path/version and a 15-minute expiry. Closing one cancels its active reads without ending another lease. Each request rechecks API authorization; responses are `no-store`/`nosniff`. See [range/wire semantics](../contracts/remote-file-gateway.md).

Observed versions use length/timestamps, not a strong hash or immutable snapshot. Detected changes return 412 before headers or abort an active response. Writes preserving those metadata cannot be detected. Refresh/reopen after changes/expiry. The 12 KiB native chunk budget, checksums, credits, bounded queues and request/attempt/epoch fences are unchanged.

## Local format/offline matrix

OfficeLive and unrestricted MudEx fallback are disabled. All enabled dependencies are local; no automatic external upload, embedded external resources, map tiles, formula/patch execution, extraction or writeback. A supported family does not promise every variant/codec.

| Exact variants | Enabled view / bounds | Fallback |
| --- | --- | --- |
| PNG/JPEG/GIF, extended-header static WebP | Raster BlobUrl; 16 million decoded pixels across at most 256 frames | Unsupported headers/animated WebP/over-budget images: Download; SVG: source |
| PDF | Local PDF.js canvas/worker/fonts/CMaps/WASM, page navigation/zoom; 64 MiB range-cache input, 30-second load / 15-second page budget; one page up to 16 million pixels and 16,000 pixels per dimension; native ranges when available | No script, link, form, annotation or XFA layer; capped BlobUrl without ranges; unsupported parser/font cases: Download |
| DOCX | Local docx-preview + JSZip in opaque sandbox; 15-second rendering budget | Text fallback on renderer failure; altChunks, macros and external resources disabled; embedded SVG refused |
| XLS/XLSX | ExcelDataReader stored values; first sheet, 1,000 rows / 100 columns | No formulas/macros/charts/writeback; encrypted/unsupported variants: Download |
| PPTX, ODT/ODS/ODP, EPUB XHTML | Local XML text, at most 256 parts / 512 Ki decoded characters | Text inspection without slide/page/book layout; legacy PPT/DOC, DRM/encrypted content: Download |
| UTF-8/BOM text, source, JSON/XML, Markdown/HTML/SVG, logs, diff/patch, EML, SRT/VTT | Escaped source; first 512 Ki characters | No active markup/includes/email images/patch application/subtitle attachment; unknown binary: Download |
| CSV | Quoted-field table; 1,000 rows / 100 columns | Unsupported encoding/malformed records: error/Download |
| ZIP | Entry directory; 2,048 entries / 32 MiB declared expanded bytes | No extraction/nested expansion; TAR/RAR/7z unsupported |
| MP4/WebM, MP3/Ogg/WAV | HTML media with authenticated ranges and browser codec support | Needs range capability; unsupported container/codec: Download |
| HAR | First 1,000 request method/URL/status records | No traffic replay; malformed input: error |
| OBJ | Wireframe; positive indices, 2,000 vertices / 4,000 edges | No textures/material/network assets; other 3D: Download |
| GPX | Local SVG trace; 5,000 points | No tiles/geocoding/external references; other maps: source/Download |
| ICS/VCF | First 2,000 local field lines | No URI resolution/import/install |
| DER/PEM X.509 (`cer`, `crt`, `der`, `pem`) | Local metadata/fingerprint; 1 MiB | No network trust checks, private-key containers or installation |
| SQLite (`sqlite`, `sqlite3`, `db`) | Private parser copy, 4 MiB; read-only/no pooling/extensions; 100 table names, first table 20 rows / 20 columns / 1,024 characters per cell | No user SQL/views/virtual tables/remote mutation; parser copy deleted afterwards |

ZIP-based documents also obey archive bounds. DOCX/PPTX disallow DTD/external XML resolution and cap each XML part at 512 Ki characters, depth 64 and 30,000 total nodes. Embedded raster images share 16 million pixels and are individually limited to 8 MiB. Basic's separate local Monaco/editor workers remain available.

Pinned compatible packages: MudBlazor.Extensions 9.12.2, MudBlazor 9.12.0, CodeBeam.MudBlazor.Extensions 9.2.0 and BlazorJS 2.3.0. CodeBeam is a distinct package. Full explicitly initializes the packaged fingerprinted BlazorJS module with local CSS. Browser assets: JSZip 3.10.1, docx-preview 0.3.3, pdfjs-dist 6.5.438. Shipped assets/licenses are in release SBOM/notices. Local previews need no external network, while app/authentication/native-client connectivity remains necessary.

## Disposable owner retest

In a test Linux client's terminal:

```bash
mkdir -p /tmp/netratel-explorer/{source,destination,source/nested}
printf 'synthetic text\n' > /tmp/netratel-explorer/source/notes.txt
truncate -s 32M /tmp/netratel-explorer/source/large.bin
```

In a test Windows client's PowerShell terminal:

```powershell
$fixture = Join-Path $env:TEMP 'netratel-explorer'
New-Item -ItemType Directory -Force "$fixture\source\nested", "$fixture\destination"
Set-Content "$fixture\source\notes.txt" 'synthetic text'
$file = [IO.File]::Create("$fixture\source\large.bin")
$file.SetLength(32MB)
$file.Dispose()
```

1. Open Basic from card/disk row. Check path/list/refresh, text edit/save, transfers and empty-folder operations. Choose Full and check the remembered choice after reload.
2. In the disposable root, create empty file/folder, upload zero bytes, attempt a collision, confirm replacement, rename/move and confirm recursive deletion. Check Linux case/significant-space names and Windows case-only rename. Use test accounts/older clients to check read-only/denied/compatibility behavior.
3. Transfer the generated file and compare SHA-256 (`sha256sum` on Linux; `Get-FileHash` in PowerShell). Cancel and inspect absent staging files. Interrupt a disposable mutation, inspect partial/unknown outcomes and refresh before retry.
4. Preview synthetic image, DOCX/XLSX/PPTX, PDF/media; seek late and inspect 206/native offsets. Try malformed/oversized/active markup, rapid selection, fullscreen return, popout/rejoin, resize/stack/float/save/reset and narrow screens. Network inspection should show local assets and no external tenant-content requests.
5. Inspect existing API/client logs and OTEL file-authority operations/failure counters with tenant/agent/request/attempt/epoch context, without logging contents/credentials. Verify cross-tenant/user denial. Owner Windows/macOS/native process, deployment and codec acceptance remains separate from fixture/build/distribution verification.

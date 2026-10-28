# Windows computer-use setup and acceptance

Computer Use Automation (CUA) is an opt-in Windows Client capability for observing and operating one explicitly selected interactive application. The Windows service remains in Session 0 and delegates execution through the existing authenticated Remote Support helper in the selected logged-in user session. The model/planner is separate from the execution provider.

The provider-neutral `IComputerUseProvider` contract has two implementations:

| Provider | Implemented surface | Limits |
| --- | --- | --- |
| `cua_driver` | Pinned Cua Driver over local stdio MCP: window discovery, accessibility state, screenshots and bounded semantic/pixel actions | Windows x64; exact selected window, background delivery and current session identity; each advertised capability must be checked at runtime. |
| `native_windows` | Native .NET/Win32 fallback: standard `Button` invoke, non-password `Edit` value writes, bounded `Static`/`Edit`/`Button` state and optional application-rendered window screenshots | No pixel, key or scroll input. Screenshot rendering depends on the application's `WM_PRINT` support; custom controls, WPF, WinUI and browser accessibility require another provider. It is not a claim of general UIA coverage. |

There is no automatic switch between providers after an error. Select a provider deliberately, inspect its capability result and reobserve after a refusal or uncertain action. macOS and Linux desktop providers are not implemented by this change.

Native screenshots are requested with `observe` and `includeScreenshot:true`. The provider sends a bounded `WM_PRINT` message with a 1,500 ms timeout, rejects windows larger than 4,096 pixels on either axis, scales the longest output side to at most 1,280 pixels and limits the PNG to 1,500 KiB before base64 encoding. It checks that window geometry did not change during capture. A returned image still needs visual inspection: message delivery alone cannot prove that an application's rendering is complete. Native capture does not enable pixel input, and Windows screenshot fidelity remains part of the pending canary.

## Install the optional pinned sidecar

Use an independently enrolled disposable Windows 11 x64 VM for acceptance. Install the reviewed NetRatel Client build using the normal [Client installation](CLIENT.md), with a logged-in ordinary user. A copied enrolled disk or another executable directory is not an independent Client identity.

The sidecar is an explicit optional download and is not bundled into normal Client archives. Its installer requires **PowerShell 7**, which runs alongside the Windows-provided PowerShell 5.1. Install it using [Microsoft's Windows instructions](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows). On Windows 11 with WinGet, the MSI installation command is:

```powershell
winget install --id Microsoft.PowerShell --source winget --installer-type wix
```

Open **PowerShell 7 as Administrator** afterward (`pwsh.exe`). The installer is [scripts/client/install-cua-driver.ps1](../../scripts/client/install-cua-driver.ps1). Obtain it from the same reviewed NetRatel source commit as the canary build. Replace the placeholder with that exact public commit:

```powershell
$sourceCommit = 'YOUR_REVIEWED_NETRATEL_COMMIT'
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/BostonTechnologies/netratel/$sourceCommit/scripts/client/install-cua-driver.ps1" -OutFile .\install-cua-driver.ps1
.\install-cua-driver.ps1 -InstallDirectory 'C:\Program Files\NetRatel\computer-use\cua-driver'
```

Alternatively run that script directly from the reviewed repository checkout. PowerShell 7 supplies `Invoke-WebRequest` and the archive APIs used by the installer. The installer downloads one fixed official archive, verifies the archive and extracted executable hashes, and writes a receipt. It extracts only the driver executable. It does not launch a daemon, expose a listening port or modify Client enrollment.

| Pinned input | Value |
| --- | --- |
| Driver release | Cua Driver `v0.38.0` |
| Upstream source | [`85e2b1318bd77ccf3cfa88ccdf875a4bb7eaed19`](https://github.com/trycua/cua/tree/85e2b1318bd77ccf3cfa88ccdf875a4bb7eaed19/libs/cua-driver) |
| Windows archive SHA-256 | `5edaa74d0afa1b55a9668dbc4f32736507bd2bd4d75b18ca1dc8881bb12402b5` |
| `cua-driver.exe` SHA-256 | `ffa58d88779ee219304b0c307f4cfa970fe822f830c6ecf9254eda5c29e22bf2` |
| MCP protocol required by adapter | `2025-06-18`; actual negotiated revision is returned in capabilities |

The endpoint verifies the executable hash before launching it. A different binary, unsupported negotiated protocol or incompatible tool discovery returns an explicit refusal. A later driver upgrade requires a reviewed adapter/pin change, not replacement of the executable under an existing lease. Native fallback has no external sidecar.

Only the MIT-licensed Cua Driver components are used. [Upstream licensing](https://github.com/trycua/cua/blob/85e2b1318bd77ccf3cfa88ccdf875a4bb7eaed19/legal/LICENSING.md) distinguishes Cua Spaces and optional perception components; neither is installed or invoked here. NetRatel does not bundle `cua-perception` or host Cua Spaces.

## Enable the Client

In the **Administrator PowerShell 7 terminal**, inspect the running service to locate the actual executable directory:

```powershell
Get-CimInstance Win32_Service -Filter "Name='NetRatel.Client'" | Select-Object Name, State, PathName
```

Open `clientsettings.json` beside that executable in an editor. Preserve its existing sections and merge this top-level `ComputerUse` section:

```json
{
  "ComputerUse": {
    "Enabled": true,
    "EnableNativeProvider": true,
    "CuaDriverPath": "C:\\Program Files\\NetRatel\\computer-use\\cua-driver\\cua-driver.exe",
    "OperationTimeoutSeconds": 20,
    "ObservationLifetimeSeconds": 15
  }
}
```

Client settings are read at startup. Restart the canary service after saving:

```powershell
Restart-Service -Name 'NetRatel.Client'
```

The stable sidecar directory above is separate from versioned Client application directories. Preserve the explicit setting when updating the Client and verify it again using `capabilities`. The default sidecar path, when no override is configured, is `computer-use\cua-driver\cua-driver.exe` below the running executable directory; a new version directory needs its own sidecar if that default is used.

| Setting | Default | Effective bounds |
| --- | --- | --- |
| `Enabled` | `false` | Explicit endpoint opt-in |
| `EnableNativeProvider` | `true` | Effective only while `Enabled` is true |
| `CuaDriverPath` | Relative to running application base as described above | Absolute protected executable path with the pinned digest |
| `OperationTimeoutSeconds` | `20` | 5–30 seconds |
| `ObservationLifetimeSeconds` | `15` | 1–30 seconds |

Enable the API independently and assign reviewed operator access using [HTTP MCP computer use](../mcp-http/computer-use.md). The Client's opt-in alone cannot admit a server caller.

## Owner-run Windows canary

Use one API replica for this canary, or explicitly route every CUA request to the API node holding the Client's control stream. CUA dispatch currently uses the existing node-local control registry; another API replica returns unavailable without dispatch even when shared Akka presence reports the Client online. Shared database ownership and replay protection do not provide cross-node command routing. Record the API topology with the evidence; multi-replica CUA routing remains unaccepted.

**Status: Windows desktop acceptance pending.** The Linux/source test suite cannot prove interactive Windows input, screenshot fidelity, UIA behavior, sidecar installation or service cleanup. Record actual observations for this matrix before a wider rollout.

### Start a reproducible native fixture

The repository includes a disposable [Win32 fixture](../../tools/testing/computer-use-fixture.ps1) with standard Edit, Button and Static controls. It writes no files and makes no network requests. In a **normal, non-elevated PowerShell terminal in the selected logged-in user's session**, run it from the reviewed repository checkout:

```powershell
powershell.exe -NoProfile -STA -File .\tools\testing\computer-use-fixture.ps1
```

If using downloaded scripts instead of a checkout, obtain `tools/testing/computer-use-fixture.ps1` from the same reviewed commit using the `Invoke-WebRequest` pattern above, and run that local path. The fixture prints its process ID and window title. Select those exact values through `list_windows`; do not guess its opaque provider window ID. Windows 11 Notepad may use WinUI and is not proof that the standard Win32 fallback works.

### Exercise the complete flow

1. Record the exact NetRatel source/build identity, installed service path, tenant/agent IDs, Windows build, logged-in session and user hash, driver receipt/hash, reported provider version and negotiated MCP revision.
2. With a view-only identity, list windows, open a view lease, observe the fixture and close the lease. Confirm control operations are refused with durable audit evidence.
3. With a reviewed view/control identity, preview and confirm a control lease for the same fixture. Observe its Edit element, preview/confirm typing a disposable marker such as `canary-417`, and inspect read-back. Use a new observation to invoke **Apply**, then observe **Applied: canary-417**. Record the status and `mayHaveApplied` returned for each action; a button delivery alone may correctly be `applied_unverified` until the application state is checked.
4. Repeat the semantic flow separately with `native_windows` and `cua_driver`, using each provider's own window/element identifiers. Request a native screenshot and visually check the fixture's bounds and rendered contents. For Cua Driver, capture an image, explicitly select a window-relative pixel click where semantic input is unsuitable, and verify the state afterward. A native pixel request must return unsupported capability even after a successful screenshot.
5. Exercise pause, manual operator takeover and terminate in the existing Remote Support workflow. Reobserve after resume. Confirm terminal/jobs and manual support remain usable and no duplicate interactive helper unexpectedly appears.
6. Close every lease. Check that the driver process, helper lease state and temporary manifests are gone. Then run the cleanup and upgrade cases below.

| Scenario | Required evidence | Execution |
| --- | --- | --- |
| Win32 Edit/Button fixture | Both providers: observe → type/invoke → read-back; exact process/window; inspect native and Cua screenshots separately | Pending owner run |
| WPF and WinUI fixture | Cua Driver semantic flow; native unsupported outcome reported accurately | Pending owner run |
| Electron/browser application | Cua Driver state and bounded input on the selected window; no arbitrary browser-tool proxy | Pending owner run |
| Representative legacy business application | Named test workflow and expected read-back; use disposable data | Pending owner run |
| Missing CUA permissions / cross-tenant target | No desktop dispatch; durable refusal/correlation evidence | Pending owner run |
| Wrong Windows session, changed login or wrong window | Exact target rejected; no fallback to a different desktop | Pending owner run |
| Conflicting controllers / manual takeover | One controlling owner, pause/termination fencing and fresh observation after resume | Pending owner run |
| Lock/disconnect, session switch, UAC/elevated target | Accurate unavailable/refused outcome; no secure-desktop or privilege bypass | Pending owner run |
| Stale element/screenshot, user interference | Refusal or unverified result; no blind coordinate replay | Pending owner run |
| Cancellation, helper/driver crash, service/API restart | Accurate outcome including uncertainty; old lease cannot resume implicitly | Pending owner run |
| Pinned installation, tampered sidecar, upgrade | Valid digest accepted; mismatched bytes refused; settings and exact provider rechecked after upgrade | Pending owner run |
| Feature disable and removal | Processes/temporary state removed, new operations disabled, optional sidecar removed explicitly | Pending owner run |

Retain operation IDs, audit IDs, safe correlation IDs and the observed outcome. Keep typed text, screenshots and application data out of public issue comments and durable audit metadata. Mark only executed cases passed; a successful compile or contract test does not change this table's GUI status.

## Disable and remove

Set Client `ComputerUse:Enabled` to `false` in its effective configuration and restart `NetRatel.Client`. Gateway/helper teardown revokes active provider leases. Set API `NetRatel:ComputerUse:Enabled` to `false` and apply its existing deployment to stop new admissions; existing operator pause/close controls can still fence a lease. Verify process/lease cleanup on the canary.

For a stable sidecar installed outside versioned Client directories, run its explicit removal in the **Administrator PowerShell 7 terminal** after disabling/restarting the Client:

```powershell
.\install-cua-driver.ps1 -InstallDirectory 'C:\Program Files\NetRatel\computer-use\cua-driver' -Uninstall
```

The script removes its managed executable/receipt only. An externally configured stable sidecar is not assumed to be deleted by the normal Client uninstaller; include this removal when decommissioning the canary. Removing a sidecar does not remove Client enrollment or durable NetRatel audit records.

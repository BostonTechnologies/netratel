# Theme first-paint contract

`NetRatelTheme` is the source of truth for both MudBlazor's interactive theme
and the prepaint CSS emitted by `NetRatelPrepaintTheme`. The small external
resolver normalizes the existing `netratel.theme.preference` value, resolves
System against `prefers-color-scheme`, and applies the document palette before
the stylesheet and application body can paint visible content.

Explicit light and dark choices override the operating system. When browser
storage or `matchMedia` cannot be used, the CSS fallback follows the operating
system. The interactive provider only adopts a persisted value if a newer user
selection has not already occurred, so a delayed read cannot undo an explicit
choice. Browser validation pauses only the noncritical Blazor runtime and
inspects visible computed colours before and after release.

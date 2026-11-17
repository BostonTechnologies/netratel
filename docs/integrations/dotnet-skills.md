# Repo-local .NET skills

This repository includes the skills from [Aaronontheweb/dotnet-skills](https://github.com/Aaronontheweb/dotnet-skills) under [`.agents/skills/`](../../.agents/skills). Codex discovers this repo-local directory. Each directory uses the skill's frontmatter `name`, so the skill IDs in [AGENTS.md](../../AGENTS.md) resolve directly. The skill files and supporting references are retained, with two table-of-contents anchor repairs and one whitespace cleanup recorded in the provenance file.

[`.agents/dotnet-skills.json`](../../.agents/dotnet-skills.json) records the exact upstream revision and original directories. [The upstream MIT license](../../.agents/dotnet-skills.LICENSE) accompanies the copy. This installs reference skills into the source repository; no global assistant installation or specialized Claude agents are required.

For .NET changes, read the relevant `SKILL.md` before implementing. Follow its referenced files when needed and the routing guidance in `AGENTS.md`. Repository-specific requirements, including focused regression selection and the hosted release pipeline, take precedence over generic tool/CI examples. Installing the library does not add mandatory test matrices or publication jobs.

To refresh the installation, clone the upstream repository into a temporary working directory, select and record the reviewed commit, and copy each directory under `skills/` to `.agents/skills/<frontmatter-name>/`, including all reference files. Update the revision/directory map and license together, review upstream instructions, and verify every routed skill exists. Do not copy the upstream repository's own `AGENTS.md` over NetRatel's repository policies.

## Source-quality gate

Use the SDK pinned in `global.json` and the repository's Slopwatch 0.5.0
gate. Run it before handing off authored or refactored source and MSBuild changes.

```sh
dotnet tool restore --tool-manifest dotnet-tools.json
python3 tools/ci/run-slopwatch.py
```

The gate restores the pinned tool and scans the full source tree, including tests,
Razor and root MSBuild files. Every new finding at Info or higher fails. The
scanner skips recognized generated files and standard build output. Generated-code
attributes do not exempt hand-written source. The gate reads the checked-in v2
baseline and writes findings, diagnostics and timing under `.slopwatch/reports/`.
The PR and release workflows retain these reports as artifacts.

For an intentional baseline, suppression or exclusion change, include the source
revision, rule/file/context, rationale and measured before/after findings in the
PR. Review added ignore directives and generated-code markers as policy changes.
Create or regenerate baseline candidates only during a deliberate review. The
full-tree gate is authoritative over the vendored skill's global-install,
older-version and dirty-worktree hook examples.

The legacy baseline keeps the existing Windows Task Scheduler COM binding in
`RemoteDesktopUserHelperTask.cs` and `RemoteSupportConsoleActiveSessionLauncher.cs`
and the five exact MSBuild warning lists. Changes to those lists are checked
against the complete XML element.

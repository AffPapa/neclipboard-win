# Site verification — 2026-10-11

The existing public Site was updated, not replaced with another project.

- URL: https://neclipboard-win.mail782861.chatgpt.site
- Saved Site version: 11 (not the application version).
- Source commit: `e4188191410f1df55eba9ef70f3c0220c27ffa7c`.
- Deployment: `appgdep_6acb9cd51204819182bab392f2824ed9`, succeeded 2026-10-11T14:27:50Z.
- Deployment status returned by Sites: `succeeded`.
- Application status on the page: **0.2.0 candidate, no public binary download yet**. GitHub link points to the verified public repository `AffPapa/neclipboard-win`; no public asset URL is claimed.

## Checks performed

- Version 11 production build: PASS (existing upstream dynamic-import warnings retained). TypeScript `--noEmit` and FAQ checks below were performed on older versions, not rerun for this screenshot/content-only change.
- Version 11 public home opened in the browser: caption, GitHub link, actual image load 768×674 and light desktop layout confirmed. Local proof: `outputs/neclipboard-site-v11-proof.png` in the parent workspace.
- Version 11 local preview desktop and requested 390×844 mobile viewport: native image loaded; DOM content width equals client width (375 CSS px), no observed horizontal overflow. Browser viewport override reset afterwards. This is not a fresh mobile public-environment check.
- Version 11 public changelog navigation: both latest shutdown/accessibility fixes visible, candidate disclaimer retained. Expanded .NET FAQ: historical PASS, not repeated in version 11.
- Home/changelog prerender: PASS. Removed unused starter API endpoint.
- Version 11 replaces the schematic with full, unedited native light/dark QA-window screenshots from the exact e099/28957d4 application. Only synthetic text/file entries are shown; test profile and disabled integrations are explicitly labelled. No generated UI or personal profile is presented as product evidence. Both assets packaged; light asset rendering confirmed in browser. Dark browser media selection remains unverified.
- CSS follows system light/dark preference and reduced-motion settings. This run visually inspected the light Site only; dark browser rendering and a full accessibility audit remain unverified.

## Dependency risk

Safe dependency updates and the `fflate` 0.7.5 override removed previously reported fixed advisories. Current `npm audit` still returns **7 high-severity package entries** from one transitive `braces` issue through the Vinext build plugin chain. This is not a zero-vulnerability result.

[GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm) reports no patched version for `braces <=3.0.3` at inspection time. The vulnerable operation parses deeply nested brace patterns. In this project the dependency belongs to build-time source globbing, and no user-supplied glob route exists. Searches of the generated server JavaScript found no `braces` or `unzipSync` references; that check is supporting evidence, not a formal proof of unreachability. Only trusted repository sources should be built. Track an upstream fix; do not run a breaking framework downgrade just to suppress audit output.

## Publication tooling

The supported Sites source helper committed/pushed the exact source and packaged its build. On Windows its Bash packaging required the installed Git Bash (rather than WSL Bash) and `TAR_OPTIONS=--force-local` for drive-letter archive paths. These were process-local settings; installed plugins and system settings were not changed.

This Site publication does **not** mean the EXE was published to GitHub or passed the outstanding native desktop acceptance tests.

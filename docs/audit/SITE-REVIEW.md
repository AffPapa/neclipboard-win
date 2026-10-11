# Site verification — 2026-10-11

The existing public Site was updated, not replaced with another project.

- URL: https://neclipboard-win.mail782861.chatgpt.site
- Saved Site version: 9 (not the application version).
- Source commit: `56bbf275f286f4719b400c1ffa69712f5b105691`.
- Deployment status returned by Sites: `succeeded`.
- Application status on the page: **0.2.0 candidate, no public binary download yet**. No invented GitHub repository or download URL.

## Checks performed

- Production build and TypeScript `--noEmit`: PASS.
- Home page HTTP 200, desktop screenshot and 390×844 mobile screenshots: PASS for readable layout without observed overlap.
- Changelog navigation and expanded .NET FAQ: PASS through browser interaction.
- Home/changelog prerender: PASS. Removed unused starter API endpoint.
- Clear, flat Windows clipboard palette schematic replaces ambiguous decorative presentation; it is explicitly labelled as a schematic, not a screenshot.
- CSS follows system light/dark preference and reduced-motion settings. This run visually inspected the light Site only; dark browser rendering and a full accessibility audit remain unverified.

## Dependency risk

Safe dependency updates and the `fflate` 0.7.5 override removed previously reported fixed advisories. Current `npm audit` still returns **7 high-severity package entries** from one transitive `braces` issue through the Vinext build plugin chain. This is not a zero-vulnerability result.

[GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm) reports no patched version for `braces <=3.0.3` at inspection time. The vulnerable operation parses deeply nested brace patterns. In this project the dependency belongs to build-time source globbing, and no user-supplied glob route exists. Searches of the generated server JavaScript found no `braces` or `unzipSync` references; that check is supporting evidence, not a formal proof of unreachability. Only trusted repository sources should be built. Track an upstream fix; do not run a breaking framework downgrade just to suppress audit output.

## Publication tooling

The supported Sites source helper committed/pushed the exact source and packaged its build. On Windows its Bash packaging required the installed Git Bash (rather than WSL Bash) and `TAR_OPTIONS=--force-local` for drive-letter archive paths. These were process-local settings; installed plugins and system settings were not changed.

This Site publication does **not** mean the EXE was published to GitHub or passed the outstanding native desktop acceptance tests.

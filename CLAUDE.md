# LayaSample

## Keep the README current

- `README.md` is the front door. When a change adds or alters a feature, in the same change: add or update its row in the **Features** table (with a one-line "run it"), update its section, and add the endpoint to the **API** table. Setup a newcomer needs goes in **Quick start** or the feature's own section.
- Every feature you build also gets a dated entry at the top of **Quickstarts by date** in the README: today's date (`date +%F`) as the heading (reuse it when working again the same day), a bold feature name, the commands to try it, what you should see, and which steps cost money or are opt-in. Newest first. Do not rewrite older entries; when behaviour changes, add a new dated entry that says so. Run the commands before you write them down, and mark the ones you could not run.
- Every plan or feature set you build also gets its own `docs/<feature-name>.md` (kebab-case, named for the plan, for example `docs/security-cost-hardening.md`), written in the same change. It starts with the date and why the work was done, then for each feature: what it does, how it works (key files), settings, how to try it, what it does not do, and the tests (mark the opt-in and paid ones). End with what was verified and what was not, and a checklist for anything to do before deploying. Link it from the README. When a feature changes later, update its page and say so with a date. The README stays the short version; the page is the long one.
- Deployment details live in `deploy/README.md`; link to it from the README rather than copying it.
- Say what a feature does not do, and which tests are opt-in (they spend money or need a daemon).

## Git workflow

- When executing a plan, first create a new branch from an up-to-date `main` before making any change: `feature/<plan-name>` (or `fix/<plan-name>` for a fix), kebab-case, matching the `docs/<feature-name>.md` page for that plan. One plan, one branch.
- Never commit plan work directly to `main`; it lands through a pull request.
- If the working tree is dirty or you are already on a non-main branch when a plan starts, stop and ask before branching.

## .NET conventions

- Use central package management. Package versions live only in `Directory.Packages.props`; csproj files contain version-less `<PackageReference Include="..." />`.
- Use central build configuration. Properties shared by all projects (`TargetFramework`, `Nullable`, `ImplicitUsings`) live in `Directory.Build.props`; keep only project-specific properties in a csproj.
- When adding a package, add its `<PackageVersion>` to `Directory.Packages.props` first, then reference it from the project.
- Restores write `packages.lock.json` per project and CI restores in locked mode: after any package change, run `dotnet restore` and commit the lock files.

## Dependency policy

- Only use open-source packages under a permissive license (MIT, Apache-2.0, BSD, BSL-1.0, or similar). No copyleft (GPL, LGPL, AGPL), no source-available or dual/split commercial licenses (e.g. Six Labors Split License), and no proprietary packages.
- Packages must be actively maintained: a release or repository activity within roughly the last 12 months, not archived or deprecated. Prefer the actively developed major line over a legacy one (e.g. `xunit.v3` over `xunit` 2.x).
- The policy covers native binaries a package bundles and transitive dependencies it pulls in. Check them before adding a package (`dotnet list package --include-transitive`).
- Use the latest stable version when adding or reviewing a package.
- One approved exception: ClamAV (GPL-2.0) scans uploads. It runs as its own container, which the API reaches over TCP (clamd's INSTREAM protocol); it is not a package, and nothing of it is linked into or bundled with the application. Approved explicitly because it is open source software. Do not add other copyleft components on the strength of this.
- Developer tools that run outside the build (for example garak for red-teaming, Apache-2.0) are pinned in their own requirements file and are not covered by the NuGet licence check, but they still must be permissively licensed.
- CI enforces the licence list in `.github/allowed-licenses.json` (`dotnet dnx -y nuget-license@4.0.18 -- -i LayaSample.slnx -t -a .github/allowed-licenses.json` runs it locally).

# LayaSample

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
- CI enforces the licence list in `.github/allowed-licenses.json` (`dotnet dnx -y nuget-license@4.0.18 -- -i LayaSample.slnx -t -a .github/allowed-licenses.json` runs it locally).

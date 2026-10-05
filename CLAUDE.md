# LayaSample

## .NET conventions

- Use central package management. Package versions live only in `Directory.Packages.props`; csproj files contain version-less `<PackageReference Include="..." />`.
- Use central build configuration. Properties shared by all projects (`TargetFramework`, `Nullable`, `ImplicitUsings`) live in `Directory.Build.props`; keep only project-specific properties in a csproj.
- When adding a package, add its `<PackageVersion>` to `Directory.Packages.props` first, then reference it from the project.

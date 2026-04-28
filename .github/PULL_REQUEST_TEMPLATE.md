## What changed and why

<!-- One paragraph. What does this PR do and what problem does it solve? -->

> **This is a public Forge snapshot repo.** Every merge is a potential public release.
> The checklist below is mandatory — do not self-approve to bypass unresolved items.

## Contract stability check

- [ ] No existing action or parameter was **renamed** without `OriginalName` on `[OSAction]`/`[OSParameter]`.
- [ ] No existing action changed **parameter semantics** (meaning, type, or cardinality) under the same name. If unavoidable, a new action name was added and the old name preserved as a compatibility alias.
- [ ] No action or parameter was **removed** without a deprecation path.
- [ ] Action names verified against the **previous published Forge version**; any removal or semantic change documented in this PR body.

## Native asset check

- [ ] All `<Content Include=...>` files referenced in the `.csproj` **exist on disk** and are committed to this repo.
- [ ] After `dotnet publish`, native binaries (e.g. `qpdf`) are present in the `publish/` output directory.

## Build and validation

- [ ] `dotnet build` exits 0 (or state **"build not verified"** explicitly if `dotnet` was unavailable).
- [ ] No unresolved **P1** review flags. Do not merge with open P1s.

## Documentation

- [ ] If any action, parameter, or return struct was added, changed, or removed: inline documentation or comments updated in this PR.

## Release

- [ ] `ExternalLibraries.json` `Version` and `ReleaseNotes` updated to match this snapshot.

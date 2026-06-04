# Mitchell1Helper

Windows .NET helper service for syncing Mitchell 1 data and coordinating with Odoo.

## Releasing with GitHub

This repository includes a GitHub Actions workflow at `.github/workflows/release.yml` that builds the app, creates a Velopack package, and publishes a GitHub release.

There are two supported ways to create a release.

### Option 1: Create a release from a Git tag

Use this when you want the release to start automatically after pushing a version tag.

1. Commit and push the changes you want included.
2. Create a semantic version tag in the format `vX.Y.Z`.
3. Push the tag to GitHub.

Example:

```bash
git tag v1.2.3
git push origin v1.2.3
```

What happens next:

1. GitHub Actions runs the `Build and Release` workflow.
2. The workflow builds the project on `windows-latest` using .NET 8.
3. It publishes a self-contained `win-x64` build.
4. It creates Velopack release artifacts.
5. It creates a GitHub release named `Release 1.2.3` and uploads the generated files.

### Option 2: Create a release from the GitHub Actions UI

Use this when you want to trigger a release manually in GitHub without pushing a tag first.

1. Open the repository on GitHub.
2. Go to `Actions`.
3. Open the `Build and Release` workflow.
4. Click `Run workflow`.
5. Enter the version number as `X.Y.Z`.
6. Run the workflow.

The workflow will create the GitHub release using tag `vX.Y.Z`.

## Release Rules

1. Tag format must be `vX.Y.Z` for tag-based releases.
2. Manual releases expect the version input as `X.Y.Z`.
3. Release notes are generated automatically by GitHub.
4. The published assets come from the Velopack output in the workflow.

## Where the Release Logic Lives

See `.github/workflows/release.yml` for the exact build and publish process.

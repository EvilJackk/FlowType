# Optional CI workflow

`github-build-workflow.yml` is a ready-to-use GitHub Actions workflow that
restores, builds, and publishes FlowType on every push and pull request.

It ships here rather than in `.github/workflows/` so it stays opt-in. To enable
it, move it into place:

```bash
git mv build/ci/github-build-workflow.yml .github/workflows/build.yml
git commit -m "Enable CI workflow" && git push
```

Note that pushing anything under `.github/workflows/` requires a token with the
`workflow` scope. If the push is rejected, grant it once with
`gh auth refresh -h github.com -s workflow`, or create the file through the
GitHub web UI (Actions → New workflow) and paste the contents.

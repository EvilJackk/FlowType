# CI workflow (not yet enabled)

`github-build-workflow.yml` is a ready-to-use GitHub Actions workflow that
builds and publishes FlowType on every push.

It isn't installed at `.github/workflows/` because pushing files to that
directory requires a token with the `workflow` OAuth scope, which the account
that created this repo didn't grant.

To enable it, either:

```bash
# Option 1 — grant the scope once, then move the file
gh auth refresh -h github.com -s workflow
git mv build/ci/github-build-workflow.yml .github/workflows/build.yml
git commit -m "Enable CI workflow" && git push
```

Or simply create the file through the GitHub web UI (Actions → New workflow)
and paste its contents.

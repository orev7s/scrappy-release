# Update notifier, end to end

Three scripts that drive the update dialog for real, without publishing a release and without a
hand on the mouse.

| file | what it is |
|---|---|
| `stub_releases.py` | a stand-in for the GitHub Releases API. It answers `/releases/latest` and `/tags` with a tag read from a file, so a run can publish a newer release between two launches of the app, and it logs every request. |
| `fake-xdg-open` | stands in for the desktop's URL handler, so "opens the release page in the default browser" is observable: the URL the OS was handed is written to a file. |
| `run.sh` | the scenarios. It starts the stand-in, runs the real app head-less under Xvfb, drives the real dialog, and checks what happened. |
| `perf.sh` | the startup cost of the check, measured against a baseline build. |

## The scenarios

`run.sh` runs the app with `--update-api <stand-in>` and `--update-answer <button>`, which are
development switches on `Scrappy.App` (`Program.cs`). Nothing is mocked: the comparison, the
suppression file, the dialog and the URL handed to the desktop are the product's own.

1. **Update** - a newer release exists; the dialog appears and the release page reaches the
   desktop's URL handler.
2. **Skip** - the dialog appears, is dismissed, and the next launch asks again. Nothing is written.
3. **Don't show again** - the dismissal is written; the same version stays silent; a newer version
   is offered again.
4. **Up to date** - the running build is the newest release, so nothing is offered.
5. **The API is unreachable** - the app starts, and says nothing.

## Running it

```sh
export DOTNET_ROOT=/root/.dotnet8
export PATH=$DOTNET_ROOT:$PATH
./tools/update-e2e/run.sh "$(pwd)"
```

Needs `xvfb` and `python3`. Exit code 0 means every scenario behaved as specified.

## The performance run

```sh
./tools/update-e2e/perf.sh <baseline-worktree> <branch-worktree> 7
```

Prints the median and the spread for the time to first frame with and without the check in the
build, and for the cost of the check itself once it runs.

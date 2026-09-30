# Contributing to Kadraj

Thanks for helping! Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an [issue](https://github.com/Cafu1107/kadraj/issues/new/choose) using the bug template. The most useful details:

- Kadraj version (shown next to the title), Windows version, and GPU if the background feature is involved
- Phone model and browser
- The meeting app you picked Kadraj in
- What you expected and what happened; a screenshot helps

Security problems go through [SECURITY.md](SECURITY.md), not public issues.

## Building

See *For developers* in the [README](README.md#for-developers). In short: Visual Studio 2022 with the C++ workload, the .NET 9 SDK, then `.\build.ps1`.

## Pull requests

- Keep changes focused; one topic per PR.
- Match the surrounding code style.
- UI text goes through `L.T("English", "Türkçe")`; please add both (ask in the PR if you need help with Turkish).
- Describe how you tested it: which phone, which app, GPU or CPU.

By contributing you agree that your work is released under the [GPL-3.0](LICENSE).

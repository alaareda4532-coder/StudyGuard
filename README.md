# StudyGuard

StudyGuard is a time and focus management app with an Arabic dark interface and RTL layout.

## Projects

- Windows application source: `PC/Source/StudyGuard`
- Windows project tests: `PC/Source/StudyGuard.Tests`
- Linux application and system agent: `PC/Linux`
- Handoff and requirements: `PC/Source/StudyGuard/StudyGuard_HANDOFF.md`

Build outputs, local distributions, backups, and IDE-generated files are excluded from this repository. Create platform packages from the respective source projects and workflow.

## Windows

The Windows project targets .NET 10 for Windows. Build `PC/Source/StudyGuard/StudyGuard.csproj` with the .NET 10 SDK and Windows desktop targeting support.

## Linux

The Linux project targets Linux x64 and uses Avalonia for its desktop UI. It includes an optional systemd agent for app and site blocking. See `PC/Linux/README.md` for its scope and installation details.

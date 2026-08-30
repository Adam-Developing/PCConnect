# Windows installer

The WiX 4.0.6 MSI contains a self-contained x64 .NET 10 Windows service and WPF
companion. It installs the service as LocalSystem, starts it automatically, and
starts the unprivileged companion in each interactive user session through the
machine Run key. The companion provides the complete graphical sign-in and
device-enrollment flow; no account password or enrollment secret is placed in
the MSI or passed through a command-line interface.

Build inputs are produced first:

```powershell
dotnet publish src/PCConnect.Windows.Agent/PCConnect.Windows.Agent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/windows/agent
dotnet publish src/PCConnect.Windows.Companion/PCConnect.Windows.Companion.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/windows/companion
dotnet build installer/PCConnect.Windows.Setup/PCConnect.Windows.Setup.wixproj -c Release
```

Release CI must Authenticode-sign and timestamp both executables and the MSI.
Unsigned artifacts are test-only and cannot be promoted. The signing certificate
and password are CI secrets, never repository files.

WiX 4.0.6 is intentionally pinned. WiX 6/7 binary distributions require a
separate Open Source Maintenance Fee EULA decision; the build must not silently
accept legal terms on the operator's behalf. Moving to a newer WiX binary is an
explicit release-owner decision.

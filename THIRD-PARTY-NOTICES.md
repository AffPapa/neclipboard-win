# Third-party software and distribution notices

NeClipboard Win source code is licensed under [MIT](LICENSE). The Windows x64 self-contained executable also bundles Microsoft .NET runtime and Windows Desktop components. Their licenses and upstream third-party notices accompany the distribution in **NeClipboardWin-LICENSES.txt**; retain that file when redistributing the program. These notices do not imply endorsement by Microsoft or any upstream contributor.

## Components in the 0.2.0 candidate

| Component | Exact version | License / notices |
| --- | --- | --- |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.12 | [Runtime MIT license](docs/legal/DOTNET-RUNTIME-LICENSE.txt), [complete upstream runtime notices](docs/legal/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt) |
| Microsoft.WindowsDesktop.App.Runtime.win-x64, including WPF and Windows Forms | 10.0.12 | [Windows Desktop MIT license](docs/legal/WINDOWSDESKTOP-LICENSE.txt), [WPF notices](docs/legal/WPF-THIRD-PARTY-NOTICES.txt), [Windows Forms notices](docs/legal/WINFORMS-THIRD-PARTY-NOTICES.txt) |

The application has no additional direct third-party runtime NuGet package. SDK 10.0.401 and ILLink build tasks are build tooling, not additional application features or separately bundled SDK distributions. The website's JavaScript dependencies and developer/test tools are not part of the Windows EXE. Do not substitute the SDK installation's general license for the exact runtime-pack licenses.

The complete upstream notice lists are retained conservatively: a list can cover other platforms or code paths in the upstream project, so inclusion is not a claim that every listed component executes in this application. No upstream license terms or copyright attributions have been removed.

## Provenance, checked 2026-10-11

The runtime license and notice text came from the restored [Microsoft.NETCore.App.Runtime.win-x64 10.0.12 package](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.12). The Windows Desktop license came from the restored [Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.12 package](https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.12). Both package manifests declare MIT and source repository `dotnet/dotnet` at commit **95017c711e6afc1085133d440e42b4bd78155701**.

The Windows Desktop runtime pack does not contain a third-party notices file. Its WPF and WinForms notices were therefore retrieved from that exact source commit, not a moving branch:

- [WPF THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/wpf/THIRD-PARTY-NOTICES.TXT)
- [WinForms THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/winforms/THIRD-PARTY-NOTICES.TXT)

Package archive SHA-256 values observed locally:

| NuGet archive | SHA-256 |
| --- | --- |
| microsoft.netcore.app.runtime.win-x64.10.0.12.nupkg | `0f63dee7ca4383cb16848966f81e439399e2058e1671ebe27e5eaf4b200e8691` |
| microsoft.windowsdesktop.app.runtime.win-x64.10.0.12.nupkg | `68bce56d2402969d82ac77fbd2b2a54f55fda86ea060366e6ddb832353dab80a` |

The copies under `docs/legal` preserve upstream text, normalized to UTF-8 and LF line endings; their bytes can therefore differ from CRLF originals. Release tooling combines the project license, this provenance document and all five full notice files into the companion asset without modifying or rebuilding the accepted EXE.

When changing a runtime pack version, review the new package manifests and corresponding upstream notice files, update these checked-in copies, and regenerate the companion asset. A future dependency must bring its applicable license/notice requirements with it; the current list is not a blanket approval for unreviewed additions.

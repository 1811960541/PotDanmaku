# Third-party dependencies

The prototype uses the following NuGet runtime dependencies. Exact transitive resolutions are recorded in `packages.lock.json`.

| Packages | Version | License | Upstream |
| --- | --- | --- | --- |
| Vortice.Direct2D1, Direct3D11, DirectComposition, DXGI, DirectX | 3.8.3 | MIT | https://github.com/amerkoleci/Vortice.Windows |
| Vortice.Mathematics | 2.1.0 | MIT | https://github.com/amerkoleci/Vortice.Mathematics |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT | https://github.com/SharpGenTools/SharpGenTools |

License texts are distributed in `licenses/`. SharpGen's beta version is a transitive dependency selected by Vortice 3.8.3 and is explicitly locked, not an unbounded preview update.

Self-contained builds also include the .NET runtime; its upstream notices are included in `licenses/dotnet-*`.

PotPlayerControl's published scalar command definitions were consulted as protocol documentation; no Java library code is bundled. The player itself is not redistributed. Test dependencies (xUnit, Microsoft.NET.Test.Sdk, coverlet) are development-only and are not included in the app publish directory.

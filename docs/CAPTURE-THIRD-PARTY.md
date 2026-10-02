# Capture dependency notices

The standalone capture tools restore these dependencies through NuGet:

- Vortice.Direct3D11, Vortice.DXGI, Vortice.DirectX, and Vortice.D3DCompiler 3.6.2.
  Copyright (c) Amer Koleci and Contributors. MIT license.
  [Upstream license](https://github.com/amerkoleci/Vortice.Windows/blob/main/LICENSE).
- Vortice.Mathematics 1.9.2. Copyright (c) Amer Koleci and contributors. MIT license.
  [Upstream license](https://github.com/amerkoleci/Vortice.Mathematics/blob/main/LICENSE).
- SharpGen.Runtime and SharpGen.Runtime.COM 2.2.0-beta.
  Copyright (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky,
  2023-2024 Amer Koleci. MIT license, as declared in the restored packages.
- C#/WinRT runtime support. Copyright (c) Microsoft Corporation. MIT license.
  [Upstream license](https://github.com/microsoft/CsWinRT/blob/master/LICENSE).
- Microsoft.Windows.SDK.NET.Ref 10.0.19041.56 is the local targeting pack.
  Copyright Microsoft Corporation. [Windows SDK terms](https://aka.ms/WinSDKLicenseURL).
  Windows capture and the shader compiler also use installed Windows system APIs.

The following MIT permission notice applies to the MIT components above:

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to
do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

The self-contained tools also include the .NET/Windows Desktop runtime under its
own notices. Brokencca's source remains GPL-3.0-or-later; see the repository
LICENSE and NOTICE.

# Third-party notices

DragShift is MIT-licensed (see `LICENSE`). `DragShift.exe` bundles the open-source components below, each under its
own license. The full license and notice texts are in the [`licenses/`](licenses) folder, and the app can show them:
tray icon → **Open-source licenses**.

| Component | Used for | License | Full text |
|---|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime), [WPF](https://github.com/dotnet/wpf) and WinForms | Runs the app (self-contained exe) | MIT | `dotnet-runtime-LICENSE.txt`, `dotnet-wpf-LICENSE.txt`, `dotnet-runtime-THIRD-PARTY-NOTICES.txt` |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) and [ImageMagick](https://imagemagick.org) | Reading, writing and editing images | Apache-2.0 / ImageMagick License | `Magick.NET-and-ImageMagick-NOTICE.txt` |
| Codec libraries inside Magick.NET's native library (libjpeg-turbo, libpng, zlib, libwebp, libtiff, aom, libjxl, openjpeg, libraw, lcms2, freetype, libheif, libde265, …) | Image formats | BSD / MIT / zlib / IJG and others; libheif, libde265 and a few others are LGPL (they live in the separate `Magick.Native-*.dll`, extracted at runtime and replaceable) | `Magick.NET-and-ImageMagick-NOTICE.txt` (each library's own text) |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | Running the background-removal model | MIT | `ONNXRuntime-LICENSE.txt`, `ONNXRuntime-ThirdPartyNotices.txt` |
| [U²-Net, u2netp model](https://github.com/xuebinqin/U-2-Net), Copyright (c) 2020 Xuebin Qin; ONNX export from [rembg](https://github.com/danielgatis/rembg) | Remove BG | Apache-2.0 | `Apache-2.0.txt` |
| [PdfPig](https://github.com/UglyToad/PdfPig), Copyright (c) UglyToad and contributors | Reading text and images from PDFs (PDF → DOCX) | Apache-2.0 | `Apache-2.0.txt` |

All components are used unmodified.

PDF page rendering and HEIC writing use components that are part of Windows (`Windows.Data.Pdf`, and the Windows
Imaging Component with Microsoft's HEIF/HEVC extensions). They aren't redistributed with DragShift.

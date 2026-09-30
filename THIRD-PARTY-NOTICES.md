# Third-party notices

Kadraj is licensed under the GNU General Public License v3.0 (see `LICENSE`).
It uses the following third-party components.

| Component | Used for | License | How it ships |
|---|---|---|---|
| [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (MobileNetV3, ONNX), Shanchuan Lin et al. | Separating the person from the background | GPL-3.0 | Downloaded on first use of the background feature from the project's GitHub release; SHA-256 checked. Not bundled. |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | Running the model | MIT | Bundled |
| [DirectML](https://github.com/microsoft/DirectML) | GPU acceleration for ONNX Runtime | Microsoft Software License Terms (redistributable with applications) | Bundled (`DirectML.dll`) |
| [QRCoder](https://github.com/codebude/QRCoder) | QR code for pairing | MIT | Bundled |
| [.NET and ASP.NET Core](https://github.com/dotnet) | App runtime, local web server | MIT | Runtime |

## Additional permission (GPLv3 section 7)

As the copyright holder of Kadraj, the author grants permission to combine and distribute Kadraj with
DirectML, ONNX Runtime and Windows system components (including Media Foundation and the Windows
Camera Frame Server), under their respective licenses, without requiring those components to be
licensed under the GPL.

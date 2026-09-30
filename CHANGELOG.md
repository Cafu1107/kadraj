# Changelog

All notable changes to Kadraj are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-09-30

First public release.

### Added
- **Phone as a webcam:** the phone opens a page in its browser (no app). Scan the QR code, tap Start. Supports front and back camera, 720p/1080p and the flashlight, and keeps the screen awake.
- **Virtual camera for Windows 11:** appears as "Kadraj" in Zoom, Teams, Discord, browsers and other camera apps while Kadraj is open. It shows a "waiting for a phone" card when no phone is connected.
- **AI background:** blur (adjustable), Studio, Sunset or your own picture. Uses Robust Video Matting on the GPU (DirectML, ~15 ms per frame), falls back to the CPU, and cleans colour from hair edges.
- **Filters:** Normal, Vivid, Warm, Cool, Cinematic, Vintage, B&W, Noir, plus brightness, contrast and saturation.
- **Frames:** Minimal, Polaroid, Film, Neon, Viewfinder or your own PNG, in six accent colours.
- **Name tag** with name and title, plus mirror and fill/fit.
- WYSIWYG preview: it shows exactly what the camera sends.
- English and Turkish UI (`KADRAJ_LANG` to override).

### Security
- Only phones on the local network and with the pairing key can connect. **Reset pairing** makes a new key.
- The pairing key and certificate are encrypted with DPAPI.
- The phone page is served with a strict CSP.
- Camera frames pass through memory only the user, SYSTEM and the camera service can open.
- The elevated camera setup runs system tools by full path.

[1.0.0]: https://github.com/Cafu1107/kadraj/releases/tag/v1.0.0

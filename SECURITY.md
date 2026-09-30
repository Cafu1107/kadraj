# Security policy

Kadraj handles a live camera feed, so security reports are taken seriously.

## Reporting a vulnerability

Please **don't open a public issue**. Use GitHub's private reporting instead:
**[Report a vulnerability](https://github.com/Cafu1107/kadraj/security/advisories/new)**.

Include what an attacker can do, the steps to reproduce, and the Kadraj version. You'll get a reply as soon as possible, and credit in the release notes if you'd like.

## Supported versions

Only the latest release gets security fixes.

## Design notes

- The web server accepts connections from private network ranges only. The WebSocket requires the 128-bit pairing key from the QR code and checks the request origin.
- The pairing key and the self-signed TLS certificate are stored under `%LocalAppData%\Kadraj`, encrypted with DPAPI for the current user.
- Frames reach the virtual camera through a section created in the user's session. Its DACL grants access only to the user, SYSTEM and LocalService (the Frame Server account).
- `Kadraj.exe --install-camera` runs elevated. It copies `KadrajCamera.dll` to `C:\Program Files\Kadraj` and runs `regsvr32` and `sc` by their full System32 paths. Keep Kadraj in a folder that only you can write to.
- The background model is downloaded over HTTPS from the Robust Video Matting release and checked against a pinned SHA-256 hash before it is used.

## Known limitations

- The first connection from a phone accepts a self-signed certificate. On a network you don't trust, someone could impersonate the PC during that first pairing.
- Anyone who has seen your QR code can connect while on the same network until you **Reset pairing**.

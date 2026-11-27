# OpenSSH terminal

The Android home screen includes an SSH terminal that is independent of Sunshine discovery and
pairing. It connects to an OpenSSH server and opens the account's normal interactive shell with a
PTY. Linux, macOS, Windows OpenSSH and WSL SSH endpoints are supported. The client does not require
tmux and does not manage persistent server sessions.

Only the address, port, username and verified host public keys are stored. Passwords and imported
private keys remain in memory for the current connection. Password, keyboard-interactive password,
and imported OpenSSH private-key authentication are supported. Multi-step MFA prompts are not yet
supported.

Rendering uses bundled xterm.js assets. No CDN is used. The WebView serves only packaged assets,
blocks network, file and content access, and exposes a narrow input/output bridge. Leaving the
terminal closes the SSH shell. Reconnect creates a new shell.

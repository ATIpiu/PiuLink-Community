package com.limelight.piulink.ssh;

final class PersistentShell {
    private PersistentShell() { }
    // Fixed command: no host/user/profile text is interpolated into a remote shell.
    static String command(boolean windows) {
        return windows ? "powershell.exe -NoLogo -NoProfile -NoExit"
                : "if command -v tmux >/dev/null 2>&1; then exec tmux new-session -A -s piulink 2>&1; "
                  + "else printf '\\r\\nPiuLink: install tmux on this host to enable persistent sessions.\\r\\n'; exit 127; fi";
    }
}

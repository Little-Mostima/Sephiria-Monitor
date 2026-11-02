// Keep Sephiria out of the foreground when an agent launches it.
//
// A launching Unity game grabs the foreground, and the BepInEx console pops up next to it.
// Both interrupt whatever the human is doing. Two layers keep that quiet:
//
//   1. `GAME\BepInEx\config\BepInEx.cfg` -> `[Logging.Console] Enabled = false` removes the log
//      window altogether; the same lines still land in `LogOutput.log` and in the plugin's
//      `/logs` ring, so nothing is lost for automation.
//   2. `startMinimizer()` runs a PowerShell loop (child of this Node process, NOT detached -
//      detached children do not survive here) that minimises every visible window owned by
//      Sephiria.exe every ~120 ms while the game boots.
//
// The game window still flashes for a fraction of a second; there is no supported way to make a
// Unity player start minimised. Launching the exe directly (instead of the steam:// URL) also
// skips Steam's own "正在启动 / 正在同步云" dialog.
//
// Set SEPHIRIA_KEEP_FOCUS=1 to opt out and let the game come to the front.

import fs from 'node:fs';
import { spawn, spawnSync } from 'node:child_process';

const PS_TYPE = `
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WinFocus {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
  static bool Match(uint pid, IntPtr h) {
    uint p; GetWindowThreadProcessId(h, out p);
    if (p == pid) return true;
    var sb = new StringBuilder(256);
    GetWindowText(h, sb, 256);
    return sb.ToString().IndexOf("Sephiria", StringComparison.OrdinalIgnoreCase) >= 0;
  }

  // ---- hidden desktop: the game's windows never appear on the interactive desktop ----
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct STARTUPINFO {
    public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
  }
  [StructLayout(LayoutKind.Sequential)]
  public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
  [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern IntPtr CreateDesktop(string name, IntPtr dev, IntPtr dm, int flags, uint access, IntPtr sa);
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
    IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  public static IntPtr Desk;
  public static int Pid;
  public static string LaunchOnDesktop(string exe, string workdir, string desktop) {
    Desk = CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
    if (Desk == IntPtr.Zero) return "CreateDesktop failed: " + Marshal.GetLastWin32Error();
    var si = new STARTUPINFO();
    si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
    si.lpDesktop = desktop;
    si.dwFlags = 1;          // STARTF_USESHOWWINDOW
    si.wShowWindow = 1;      // SW_SHOWNORMAL - irrelevant on an invisible desktop
    PROCESS_INFORMATION pi;
    if (!CreateProcess(exe, null, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workdir, ref si, out pi))
      return "CreateProcess failed: " + Marshal.GetLastWin32Error();
    Pid = pi.dwProcessId;
    return "ok pid=" + Pid;
  }
  public static bool Alive() {
    try { return !System.Diagnostics.Process.GetProcessById(Pid).HasExited; } catch { return false; }
  }

  public static int Minimize(uint pid) {
    int n = 0;
    EnumWindows((h, l) => {
      if (IsWindowVisible(h) && !IsIconic(h) && Match(pid, h)) { ShowWindow(h, 6); n++; }
      return true;
    }, IntPtr.Zero);
    return n;
  }
}
`;

const PS_LOOP = `
$deadline = (Get-Date).AddSeconds(SECONDS)
while ((Get-Date) -lt $deadline) {
  foreach ($proc in (Get-Process Sephiria -ErrorAction SilentlyContinue)) {
    [void][WinFocus]::Minimize([uint32]$proc.Id)
  }
  Start-Sleep -Milliseconds 120
}
`;

function script(seconds, loop = true) {
  const body = loop ? PS_LOOP.replace('SECONDS', String(seconds)) : '';
  return `Add-Type -TypeDefinition @'\n${PS_TYPE}\n'@\n${body}`;
}

/**
 * Bounded minimise loop that lives as long as the caller does. Returns the child so callers can
 * kill it early; it is unref'd so it never keeps the Node process alive.
 */
export function startMinimizer(seconds = 180) {
  if (process.env.SEPHIRIA_KEEP_FOCUS) return null;
  try {
    const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script(seconds, true)], {
      stdio: 'ignore',
      windowsHide: true,
    });
    child.unref();
    return child;
  } catch {
    return null;
  }
}

/** One-shot minimise, for restart steps where the next step asserts immediately. */
export function minimizeGameNow() {
  if (process.env.SEPHIRIA_KEEP_FOCUS) return false;
  try {
    spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script(0, false)], {
      stdio: 'ignore',
      windowsHide: true,
      timeout: 20000,
    });
    return true;
  } catch {
    return false;
  }
}

/** Turn the BepInEx log window on/off (default for automation: off). */
export function setConsoleWindow(enabled) {
  const cfg = `${process.env.SEPHIRIA_GAME_DIR || 'D:\\steam\\steamapps\\common\\Sephiria'}\\BepInEx\\config\\BepInEx.cfg`;
  try {
    const text = fs.readFileSync(cfg, 'utf8');
    const next = text.replace(/(\[Logging\.Console\][\s\S]*?Enabled = )(true|false)/, `$1${enabled ? 'true' : 'false'}`);
    if (next !== text) fs.writeFileSync(cfg, next, 'utf8');
    return true;
  } catch {
    return false;
  }
}

/** Name of the private desktop the agent-launched game lives on. */
export const HIDDEN_DESKTOP = 'SephiriaHidden';

/**
 * Launch the game on a private desktop. Its windows are created there instead of on the
 * interactive desktop, so nothing flashes and the foreground window never changes - no race with
 * the minimise loop, no focus steal. The helper PowerShell process also keeps the desktop object
 * alive for as long as the game runs (a desktop dies with its last handle).
 */
export function launchHidden({ exe, workdir, keepAliveMs = 6 * 60 * 60 * 1000 } = {}) {
  const body = `
$r = [WinFocus]::LaunchOnDesktop('${(exe || '').replace(/'/g, "''")}', '${(workdir || '').replace(/'/g, "''")}', '${HIDDEN_DESKTOP}')
Write-Output $r
$deadline = (Get-Date).AddMilliseconds(${keepAliveMs})
while ([WinFocus]::Alive() -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
`;
  const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', `Add-Type -TypeDefinition @'\n${PS_TYPE}\n'@\n${body}`], {
    stdio: ['ignore', 'pipe', 'ignore'],
    windowsHide: true,
  });
  child.unref();
  return child;
}

/** Is the Sephiria process running at all (any desktop)? */
export function gamePid() {
  const r = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
    '(Get-Process Sephiria -ErrorAction SilentlyContinue | Select-Object -First 1).Id'], { encoding: 'utf8', windowsHide: true });
  const n = parseInt(String(r.stdout || '').trim(), 10);
  return Number.isFinite(n) ? n : null;
}

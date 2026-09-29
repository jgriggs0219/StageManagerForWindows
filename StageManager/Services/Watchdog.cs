using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace StageManager.Services
{
	/// <summary>
	/// Keeps a crash or hang from locking the user out of their apps. At startup a small hidden
	/// PowerShell watcher is launched. Stage Manager touches a heartbeat file every 2 s from the
	/// UI thread. If the process dies without a clean exit, or the heartbeat stops for 12 s (UI
	/// hang), the watcher kills it, puts every window back on screen (visible, clickable), and
	/// restarts Stage Manager — at most 3 times in 2 minutes, so a crash loop can't spin forever.
	/// A normal Quit writes a clean-exit marker and the watcher just leaves.
	/// </summary>
	public static class Watchdog
	{
		private static readonly string Dir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StageManager");
		private static string HeartbeatPath => Path.Combine(Dir, $"heartbeat-{Environment.ProcessId}");
		private static string CleanExitPath => Path.Combine(Dir, $"clean-exit-{Environment.ProcessId}");
		private static DispatcherTimer? _heartbeat;

		public static void Start()
		{
			try
			{
				Directory.CreateDirectory(Dir);
				Beat();
				_heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
				_heartbeat.Tick += (_, _) => Beat();
				_heartbeat.Start();

				var script = Path.Combine(Dir, "watchdog.ps1");
				File.WriteAllText(script, Script);
				var exe = Environment.ProcessPath ?? "";
				Process.Start(new ProcessStartInfo
				{
					FileName = "powershell.exe",
					Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\" -TargetPid {Environment.ProcessId} -ExePath \"{exe}\" -StateDir \"{Dir}\"",
					CreateNoWindow = true,
					UseShellExecute = false,
				});
				Log.Info("WATCHDOG", "Watchdog started");
			}
			catch (Exception ex) { Log.Info("WATCHDOG", $"Watchdog failed to start: {ex.Message}"); }
		}

		public static void MarkCleanExit()
		{
			try
			{
				_heartbeat?.Stop();
				File.WriteAllText(CleanExitPath, DateTime.UtcNow.ToString("o"));
			}
			catch { }
		}

		private static void Beat()
		{
			try
			{
				if (!File.Exists(HeartbeatPath)) File.WriteAllText(HeartbeatPath, "");
				else File.SetLastWriteTimeUtc(HeartbeatPath, DateTime.UtcNow);
			}
			catch { }
		}

		private const string Script = """
param([int]$TargetPid, [string]$ExePath, [string]$StateDir)
$hb = Join-Path $StateDir "heartbeat-$TargetPid"
$clean = Join-Path $StateDir "clean-exit-$TargetPid"
$hung = $false
while ($true) {
  Start-Sleep -Seconds 2
  $p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
  if (-not $p) { break }
  if ((Test-Path $hb) -and ((Get-Date) - (Get-Item $hb).LastWriteTime).TotalSeconds -gt 12) {
    $hung = $true
    # Snapshot every thread's stack before killing it, so the cause of the freeze is on record.
    $stack = Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-stack.exe"
    if (Test-Path $stack) {
      $out = Join-Path $StateDir ("hang-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".txt")
      $job = Start-Process -FilePath $stack -ArgumentList "report -p $TargetPid" -RedirectStandardOutput $out -NoNewWindow -PassThru
      $null = $job.WaitForExit(15000)
    }
    Stop-Process -Id $TargetPid -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    break
  }
}
Remove-Item $hb -ErrorAction SilentlyContinue
if ((-not $hung) -and (Test-Path $clean)) { Remove-Item $clean -ErrorAction SilentlyContinue; exit }
Remove-Item $clean -ErrorAction SilentlyContinue

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SmRescue {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32")] static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetLong(IntPtr h, int i);
  [DllImport("user32", EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetLong(IntPtr h, int i, IntPtr v);
  [DllImport("user32")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
  [DllImport("user32")] static extern int GetSystemMetrics(int i);
  public struct RECT { public int L, T, R, B; }
  public static void Run() {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    int n = 0;
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0 || GetWindow(h, 4) != IntPtr.Zero) return true;
      long ex = GetLong(h, -20).ToInt64();
      if ((ex & 0x20) != 0) SetLong(h, -20, new IntPtr(ex & ~0x20L));
      if ((ex & 0x80000) != 0) SetLayeredWindowAttributes(h, 0, 255, 2);
      RECT r; GetWindowRect(h, out r);
      if ((r.L >= vx + vw || r.T >= vy + vh || r.R <= vx || r.B <= vy) && r.L > -30000) {
        SetWindowPos(h, IntPtr.Zero, 60 + (n % 8) * 40, 60 + (n % 8) * 40, 0, 0, 0x0001 | 0x0004 | 0x0010);
        n++;
      }
      return true;
    }, IntPtr.Zero);
  }
}
'@
[SmRescue]::Run()

# Restart, unless it has already been restarted 3 times in the last 2 minutes.
$log = Join-Path $StateDir "restarts.txt"
$now = Get-Date
$recent = @()
if (Test-Path $log) { $recent = Get-Content $log | ForEach-Object { [datetime]$_ } | Where-Object { ($now - $_).TotalMinutes -lt 2 } }
if ($recent.Count -lt 3 -and (Test-Path $ExePath)) {
  (@($recent) + $now) | ForEach-Object { $_.ToString("o") } | Set-Content $log
  Start-Process -FilePath $ExePath
}
""";
	}
}

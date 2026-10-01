using AsyncAwaitBestPractices;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using StageManager.Services;

namespace StageManager
{
	public partial class App : Application
	{
		internal static string? RestoreScenesPath { get; private set; }

		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);

			// One Stage Manager at a time: two copies fight over every window (both park and
			// restore). A second launch (user double-click while the watchdog restarts us) just leaves.
			_singleInstance = new System.Threading.Mutex(true, @"Local\StageManager.SingleInstance", out var isFirst);
			if (!isFirst && !Array.Exists(e.Args, a => a == "--restore-scenes"))
			{
				Environment.Exit(0);
				return;
			}

			// We hold the single-instance mutex, so any OTHER StageManager.exe still running is an
			// old build from before the guard existed (leftover test folders). Two copies fight
			// over every window, so close it; our startup then rescues the windows it had parked.
			try
			{
				var me = Environment.ProcessId;
				foreach (var other in System.Diagnostics.Process.GetProcessesByName("StageManager"))
				{
					if (other.Id == me) continue;
					try { other.Kill(); other.WaitForExit(3000); }
					catch { /* already gone, or not ours to close */ }
				}
			}
			catch { }

			// Started at login: Windows runs startup apps before the taskbar, desktop and DWM are
			// fully up. Starting that early left Stage Manager half-initialised (no log, no
			// watchdog, one stray tile). Wait for the shell, then give it a few more seconds.
			if (Array.IndexOf(e.Args, "--autostart") >= 0)
			{
				var deadline = DateTime.UtcNow.AddSeconds(90);
				while (DateTime.UtcNow < deadline &&
					(FindWindow("Shell_TrayWnd", null) == IntPtr.Zero || FindWindow("Progman", null) == IntPtr.Zero))
					System.Threading.Thread.Sleep(500);
				System.Threading.Thread.Sleep(TimeSpan.FromSeconds(10));
			}

			// Before anything that logs a frame number, so the count covers the whole run.
			FrameClock.Start();

			RestoreScenesPath = ParseRestoreScenesArg(e.Args);
			IsAutostart = Array.IndexOf(e.Args, "--autostart") >= 0;
			UpdateService.CleanupOldVersion();
			if (RestoreScenesPath is null)
				UpdateService.CleanupStagingFolder();

			Services.ThemeManager.ApplyTheme();
			Services.ThemeManager.StartListening();

			// Consent is per process and only affects sessions started after it lands, so
			// it goes out as early as possible. Not awaited: the tray tiles it misses are
			// parked off-screen, where the indicator it suppresses cannot be seen anyway.
			Composition.CaptureBorder.RequestAsync().SafeFireAndForget();

			// UI-thread exceptions are logged and SURVIVED. Terminating here used to strand every
			// parked window off-screen (apps unreachable until a reboot or the rescue script);
			// a sidebar glitch is always better than that. Throttled so a tight failure loop
			// still ends the process (the watchdog then restores windows and restarts us).
			DispatcherUnhandledException += (s, args) =>
			{
				Log.Fatal("CRASH", $"UI thread (survived): {args.Exception}");
				var now = DateTime.UtcNow;
				if ((now - _uiErrorWindowStart).TotalSeconds > 10) { _uiErrorWindowStart = now; _uiErrorCount = 0; }
				if (++_uiErrorCount <= 20)
					args.Handled = true;
				else
					Strategies.OpacityWindowStrategy.EmergencyRestoreAll();
			};

			AppDomain.CurrentDomain.UnhandledException += (s, args) =>
			{
				Log.Fatal("CRASH", $"Unhandled: {args.ExceptionObject}");
				// Going down for sure: give every parked window back before we do.
				Strategies.OpacityWindowStrategy.EmergencyRestoreAll();
			};

			Services.Watchdog.Start();

			TaskScheduler.UnobservedTaskException += (s, args) =>
			{
				Log.Fatal("CRASH", $"Unobserved task: {args.Exception}");
			};
		}

		/// <summary>Started by the Windows startup entry (login), not by the user.</summary>
		public static bool IsAutostart { get; private set; }

		private static System.Threading.Mutex? _singleInstance;

		[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
		private static extern IntPtr FindWindow(string? className, string? windowName);

		private static DateTime _uiErrorWindowStart = DateTime.MinValue;
		private static int _uiErrorCount;

		protected override void OnExit(ExitEventArgs e)
		{
			// Tell the watchdog this was the user quitting, not a crash: no restart.
			Services.Watchdog.MarkCleanExit();
			Services.ThemeManager.StopListening();
			base.OnExit(e);
		}

		private static string? ParseRestoreScenesArg(string[] args)
		{
			var index = Array.IndexOf(args, "--restore-scenes");
			if (index < 0 || index + 1 >= args.Length)
				return null;

			var path = Path.GetFullPath(args[index + 1]);
			var expectedDir = Path.GetFullPath(UpdateService.StagingFolder);

			if (!path.StartsWith(expectedDir, StringComparison.OrdinalIgnoreCase))
				return null;

			return path;
		}
	}
}

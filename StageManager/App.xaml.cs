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

			// Before anything that logs a frame number, so the count covers the whole run.
			FrameClock.Start();

			RestoreScenesPath = ParseRestoreScenesArg(e.Args);
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace StageManager.Services
{
	/// <summary>
	/// Reopens the user's grouped apps at login. While Stage Manager runs it keeps a list of the
	/// executables of every app that sits in a group (%AppData%\StageManager\relaunch.json).
	/// When started with --autostart (the Windows startup entry), it launches the ones that are
	/// not already running. Apps restore their own windows/tabs (e.g. Chrome's "continue where
	/// you left off"); Stage Manager then groups and lays them out as they appear.
	/// </summary>
	public static class AppRelauncher
	{
		private static readonly string FilePath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StageManager", "relaunch.json");

		private static List<string> _paths = Load();

		/// <summary>Records the grouped apps currently running. Cheap; call periodically.</summary>
		public static void Remember(IEnumerable<int> processIds)
		{
			var now = new List<string>();
			foreach (var pid in processIds.Distinct())
			{
				try
				{
					var path = Process.GetProcessById(pid).MainModule?.FileName;
					if (!string.IsNullOrEmpty(path) && !path.EndsWith("StageManager.exe", StringComparison.OrdinalIgnoreCase))
						now.Add(path);
				}
				catch { /* exited, or elevated — skip */ }
			}
			// Keep apps remembered earlier that simply aren't open right now; add new ones.
			var merged = _paths.Union(now, StringComparer.OrdinalIgnoreCase).ToList();
			if (merged.Count == _paths.Count) return;
			_paths = merged;
			Save();
		}

		/// <summary>Forgets an app so it is no longer reopened at login.</summary>
		public static void Forget(string exeName)
		{
			if (_paths.RemoveAll(p => string.Equals(Path.GetFileName(p), exeName, StringComparison.OrdinalIgnoreCase)) > 0)
				Save();
		}

		/// <summary>Launches remembered apps that aren't running. Only for --autostart.</summary>
		public static async Task RelaunchMissingAsync()
		{
			await Task.Delay(TimeSpan.FromSeconds(3)); // let the sidebar come up first
			var running = Process.GetProcesses()
				.Select(p => { try { return p.ProcessName; } catch { return ""; } })
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			foreach (var path in _paths.ToList())
			{
				var name = Path.GetFileNameWithoutExtension(path);
				if (running.Contains(name) || !File.Exists(path)) continue;
				try
				{
					Log.Info("RELAUNCH", $"Reopening {path}");
					Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
					await Task.Delay(400); // stagger so they don't all fight for focus at once
				}
				catch (Exception ex) { Log.Info("RELAUNCH", $"Could not reopen {path}: {ex.Message}"); }
			}
		}

		private static List<string> Load()
		{
			try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new(); }
			catch { }
			return new();
		}

		private static void Save()
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
				File.WriteAllText(FilePath, JsonSerializer.Serialize(_paths, new JsonSerializerOptions { WriteIndented = true }));
			}
			catch (Exception ex) { Log.Info("RELAUNCH", $"Save failed: {ex.Message}"); }
		}
	}
}

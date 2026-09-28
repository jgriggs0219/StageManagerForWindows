using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StageManager.Services
{
	/// <summary>
	/// User-defined app groups: named sidebar sections (emoji welcome, e.g. "🚚 Hoenig") and
	/// which app belongs to which. Apps are remembered by executable name, so every Chrome
	/// window joins Chrome's group and the choice survives restarts. Persisted as JSON in
	/// %AppData%\StageManager\groups.json.
	/// </summary>
	public static class AppGroups
	{
		private sealed class Data
		{
			public List<string> Groups { get; set; } = new();
			public Dictionary<string, string> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		}

		private static readonly string FilePath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StageManager", "groups.json");

		private static Data _data = Load();

		/// <summary>Raised on any change. Subscribers refresh the sidebar.</summary>
		public static event Action? Changed;

		/// <summary>Group names in sidebar order.</summary>
		public static IReadOnlyList<string> Groups => _data.Groups;

		/// <summary>The group an app belongs to, or "" when ungrouped.</summary>
		public static string GetGroup(string? exe) =>
			exe is not null && _data.Apps.TryGetValue(exe, out var g) && _data.Groups.Contains(g) ? g : "";

		/// <summary>Sort key for a group: ungrouped first (0), then groups in order (1..n).</summary>
		public static int GetOrder(string group) => group.Length == 0 ? 0 : _data.Groups.IndexOf(group) + 1;

		public static void Assign(string exe, string group)
		{
			if (!_data.Groups.Contains(group)) _data.Groups.Add(group);
			_data.Apps[exe] = group;
			Save();
		}

		public static void Unassign(string exe)
		{
			if (_data.Apps.Remove(exe)) Save();
		}

		/// <summary>Adds a group (no-op if it exists). Returns the trimmed name, or null if blank.</summary>
		public static string? Create(string name)
		{
			name = name.Trim();
			if (name.Length == 0) return null;
			if (!_data.Groups.Contains(name)) { _data.Groups.Add(name); Save(); }
			return name;
		}

		public static void Rename(string oldName, string newName)
		{
			newName = newName.Trim();
			var i = _data.Groups.IndexOf(oldName);
			if (i < 0 || newName.Length == 0 || newName == oldName || _data.Groups.Contains(newName)) return;
			_data.Groups[i] = newName;
			foreach (var app in _data.Apps.Where(kv => kv.Value == oldName).Select(kv => kv.Key).ToList())
				_data.Apps[app] = newName;
			Save();
		}

		/// <summary>Removes a group; its apps go back to ungrouped (nothing is closed).</summary>
		public static void Delete(string name)
		{
			if (!_data.Groups.Remove(name)) return;
			foreach (var app in _data.Apps.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList())
				_data.Apps.Remove(app);
			Save();
		}

		public static void Move(string name, int delta)
		{
			var i = _data.Groups.IndexOf(name);
			var j = i + delta;
			if (i < 0 || j < 0 || j >= _data.Groups.Count) return;
			(_data.Groups[i], _data.Groups[j]) = (_data.Groups[j], _data.Groups[i]);
			Save();
		}

		private static Data Load()
		{
			try
			{
				if (File.Exists(FilePath))
				{
					var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
					if (d is not null)
					{
						d.Apps = new Dictionary<string, string>(d.Apps ?? new(), StringComparer.OrdinalIgnoreCase);
						d.Groups ??= new();
						return d;
					}
				}
			}
			catch (Exception ex) { Log.Info("GROUPS", $"Load failed, starting empty: {ex.Message}"); }
			return new Data();
		}

		private static void Save()
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
				File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
			}
			catch (Exception ex) { Log.Info("GROUPS", $"Save failed: {ex.Message}"); }
			Changed?.Invoke();
		}
	}
}

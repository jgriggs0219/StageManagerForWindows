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
			public List<TitleRule> Rules { get; set; } = new();
			public List<List<string>> Splits { get; set; } = new();
			public Dictionary<string, int[]> Layouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
			public Dictionary<string, string> SplitGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
			public int[]? DefaultStage { get; set; }
			public List<string> Pinned { get; set; } = new();
		}

		private static readonly string FilePath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StageManager", "groups.json");

		// One independent set of groups (apps, rules, combos, positions) per virtual desktop,
		// keyed by the desktop's id. Nothing is mirrored between desktops.
		private sealed class Root
		{
			public Dictionary<string, Data> Desktops { get; set; } = new(StringComparer.OrdinalIgnoreCase);
			/// <summary>Pre-desktop groups.json, waiting to be given to a desktop (AdoptLegacy).</summary>
			public Data? Legacy { get; set; }
		}

		private static readonly Root _root = LoadRoot();

		// Which desktop's set the calls on this thread use; null = the current desktop.
		[ThreadStatic] private static string? _ctx;

		private static Data _data
		{
			get
			{
				var k = _ctx ?? Native.VirtualDesktop.CurrentDesktopId.ToString();
				lock (_root)
				{
					if (!_root.Desktops.TryGetValue(k, out var d))
						_root.Desktops[k] = d = new Data();
					return d;
				}
			}
		}

		/// <summary>
		/// Scopes AppGroups calls on this thread to one desktop's set — for looking up a window
		/// that lives on another desktop. Dispose to restore.
		/// </summary>
		public static IDisposable For(Guid desktop)
		{
			var prev = _ctx;
			if (desktop != Guid.Empty) _ctx = desktop.ToString();
			return new Restore(prev);
		}

		private sealed class Restore : IDisposable
		{
			private readonly string? _prev;
			public Restore(string? prev) => _prev = prev;
			public void Dispose() => _ctx = _prev;
		}

		/// <summary>True while groups from before per-desktop support still need a home.</summary>
		public static bool HasLegacy => _root.Legacy is not null;

		/// <summary>Gives the pre-desktop groups to one desktop (where most of those apps live).</summary>
		public static void AdoptLegacy(Guid desktop)
		{
			if (_root.Legacy is null || desktop == Guid.Empty) return;
			lock (_root)
			{
				var k = desktop.ToString();
				if (!_root.Desktops.TryGetValue(k, out var existing) || existing.Groups.Count == 0)
					_root.Desktops[k] = _root.Legacy;
				_root.Legacy = null;
			}
			Log.Info("GROUPS", $"Existing groups assigned to desktop {desktop}");
			Save();
		}

		private static Data Normalize(Data d)
		{
			d.Apps = new Dictionary<string, string>(d.Apps ?? new(), StringComparer.OrdinalIgnoreCase);
			d.Groups ??= new();
			d.Rules ??= new();
			d.Splits ??= new();
			d.Layouts = new Dictionary<string, int[]>(d.Layouts ?? new(), StringComparer.OrdinalIgnoreCase);
			d.SplitGroups = new Dictionary<string, string>(d.SplitGroups ?? new(), StringComparer.OrdinalIgnoreCase);
			d.Pinned ??= new();
			return d;
		}

		private static Root LoadRoot()
		{
			try
			{
				if (File.Exists(FilePath))
				{
					var text = File.ReadAllText(FilePath);
					using var doc = JsonDocument.Parse(text);
					if (doc.RootElement.TryGetProperty("Desktops", out _))
					{
						var r = JsonSerializer.Deserialize<Root>(text) ?? new Root();
						r.Desktops = new Dictionary<string, Data>(
							(r.Desktops ?? new()).ToDictionary(kv => kv.Key, kv => Normalize(kv.Value)), StringComparer.OrdinalIgnoreCase);
						if (r.Legacy is not null) Normalize(r.Legacy);
						return r;
					}
					// Old single-set file: keep it aside until a desktop adopts it.
					var legacy = JsonSerializer.Deserialize<Data>(text);
					return new Root { Legacy = legacy is null ? null : Normalize(legacy) };
				}
			}
			catch (Exception ex) { Log.Info("GROUPS", $"Load failed, starting empty: {ex.Message}"); }
			return new Root();
		}


		/// <summary>Raised on any change. Subscribers refresh the sidebar.</summary>
		public static event Action? Changed;

		/// <summary>Group names in sidebar order.</summary>
		public static IReadOnlyList<string> Groups => _data.Groups;

		/// <summary>The group an app belongs to, or "" when ungrouped.</summary>
		public static string GetGroup(string? exe) =>
			exe is not null && _data.Apps.TryGetValue(exe, out var g) && _data.Groups.Contains(g) ? g : "";

		/// <summary>Sort key for a group: ungrouped first (0), then groups in order (1..n).</summary>
		public static int GetOrder(string group) => group.Length == 0 ? 0 : _data.Groups.IndexOf(group) + 1;

		/// <summary>
		/// "Windows of <see cref="Exe"/> whose title contains <see cref="Contains"/> go to
		/// <see cref="Group"/>" — survives restarts, unlike a per-window move.
		/// </summary>
		public sealed class TitleRule
		{
			public string Exe { get; set; } = "";
			public string Contains { get; set; } = "";
			public string Group { get; set; } = "";
		}

		// Per-window moves ("this window only"). Keyed by window handle, so they last until the
		// window closes or Stage Manager restarts. "" = explicitly ungrouped.
		private static readonly Dictionary<IntPtr, string> _windowOverrides = new();

		public static IReadOnlyList<TitleRule> Rules => _data.Rules;

		/// <summary>
		/// The group one window belongs to: its own move first, then the first matching title
		/// rule, then its app's group. "" = ungrouped.
		/// </summary>
		public static string GetWindowGroup(IntPtr hwnd, string? exe, string? title)
		{
			lock (_windowOverrides)
				if (_windowOverrides.TryGetValue(hwnd, out var own))
					return own.Length == 0 || _data.Groups.Contains(own) ? own : "";

			if (exe is not null && title is not null)
			{
				var rule = _data.Rules.FirstOrDefault(r =>
					string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase) &&
					r.Contains.Length > 0 &&
					title.Contains(r.Contains, StringComparison.OrdinalIgnoreCase) &&
					_data.Groups.Contains(r.Group));
				if (rule is not null)
				{
					// A rule decides ONCE: pin the window to that group, so later tab/title
					// changes never bounce it between groups (it used to flip on every tab switch,
					// and hiding the stage window mid-flip looked like apps closing).
					lock (_windowOverrides) _windowOverrides[hwnd] = rule.Group;
					return rule.Group;
				}
			}
			return GetGroup(exe);
		}

		/// <summary>Moves just this window (not the whole app). group "" = no group.</summary>
		public static void AssignWindow(IntPtr hwnd, string group)
		{
			if (group.Length > 0 && !_data.Groups.Contains(group)) _data.Groups.Add(group);
			lock (_windowOverrides) _windowOverrides[hwnd] = group;
			Save();
		}

		public static void ForgetWindow(IntPtr hwnd)
		{
			lock (_windowOverrides) _windowOverrides.Remove(hwnd);
		}

		public static void AddRule(string exe, string contains, string group)
		{
			contains = contains.Trim();
			if (contains.Length == 0) return;
			if (!_data.Groups.Contains(group)) _data.Groups.Add(group);
			_data.Rules.RemoveAll(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(r.Contains, contains, StringComparison.OrdinalIgnoreCase));
			_data.Rules.Insert(0, new TitleRule { Exe = exe, Contains = contains, Group = group });
			Save();
		}

		public static void RemoveRulesFor(string group)
		{
			if (_data.Rules.RemoveAll(r => r.Group == group) > 0) Save();
		}

		/// <summary>
		/// Apps the user combined into one tile (a "split", e.g. Dialpad + Discord side by side).
		/// Remembered by executable, so after a restart their windows land in one tile again.
		/// </summary>
		public static IReadOnlyList<List<string>> Splits => _data.Splits;

		/// <summary>Stable key for the split an app belongs to, or null.</summary>
		public static string? GetSplitKey(string? exe)
		{
			if (exe is null) return null;
			var s = _data.Splits.FirstOrDefault(x => x.Contains(exe, StringComparer.OrdinalIgnoreCase));
			return s is null ? null : "split:" + string.Join("+", s.OrderBy(e => e, StringComparer.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Records that these apps share a tile. Each app is in at most one split, so they are
		/// first taken out of any other; splits left with fewer than two apps disappear.
		/// </summary>
		public static void RecordSplit(IEnumerable<string> exes, string group)
		{
			var set = exes.Where(e => !string.IsNullOrEmpty(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (set.Count < 2) return;
			var existing = _data.Splits.FirstOrDefault(x => x.Count == set.Count && x.All(e => set.Contains(e, StringComparer.OrdinalIgnoreCase)));
			if (existing is not null) return;
			foreach (var s in _data.Splits) s.RemoveAll(e => set.Contains(e, StringComparer.OrdinalIgnoreCase));
			_data.Splits.RemoveAll(s => s.Count < 2);
			_data.Splits.Add(set);
			// The combined tile stays in the group it was made in.
			_data.SplitGroups[GetSplitKey(set[0])!] = group.Length == 0 || _data.Groups.Contains(group) ? group : "";
			Save();
		}

		/// <summary>Takes an app out of its split (the user pulled it into its own tile).</summary>
		public static void RemoveFromSplit(string exe)
		{
			var changed = false;
			foreach (var s in _data.Splits) changed |= s.RemoveAll(e => string.Equals(e, exe, StringComparison.OrdinalIgnoreCase)) > 0;
			_data.Splits.RemoveAll(s => s.Count < 2);
			if (changed) Save();
		}

		/// <summary>
		/// Where the user last put an app on stage (moved/resized by hand), per tile + app, in
		/// physical pixels [x, y, width, height]. Used instead of the automatic layout.
		/// </summary>
		public static int[]? GetLayout(string key) => _data.Layouts.TryGetValue(key, out var r) && r.Length == 4 ? r : null;

		/// <summary>Remembers a hand-placed rect. Saved without raising Changed (no regroup needed).</summary>
		public static void SaveLayout(string key, int x, int y, int w, int h)
		{
			_data.Layouts[key] = new[] { x, y, w, h };
			Persist();
		}

		/// <summary>Forgets hand-placed rects for a tile so it goes back to the automatic layout.</summary>
		public static void ClearLayouts(string tileKey)
		{
			var keys = _data.Layouts.Keys.Where(k => k.StartsWith(tileKey + "|", StringComparison.OrdinalIgnoreCase)).ToList();
			foreach (var k in keys) _data.Layouts.Remove(k);
			if (keys.Count > 0) Persist();
		}

		/// <summary>
		/// The group a combined tile (split) lives in. Stored for the split itself so its member
		/// windows can never disagree: before this, a Chrome window matching a title rule and a
		/// Discord window in another group bounced the tile between groups every few seconds.
		/// </summary>
		public static string GetSplitGroup(string splitKey)
		{
			if (_data.SplitGroups.TryGetValue(splitKey, out var g) && (g.Length == 0 || _data.Groups.Contains(g)))
				return g;
			// Default: the group of the split's first app (alphabetical, so it is stable).
			var first = splitKey.StartsWith("split:") ? splitKey.Substring(6).Split('+').FirstOrDefault() : null;
			return GetGroup(first);
		}

		public static void SetSplitGroup(string splitKey, string group)
		{
			if (group.Length > 0 && !_data.Groups.Contains(group)) _data.Groups.Add(group);
			_data.SplitGroups[splitKey] = group;
			Save();
		}

		/// <summary>
		/// The group a window's tile belongs to, accounting for splits: a window in a split
		/// takes the split's group; otherwise its own move, title rule, or app group.
		/// </summary>
		public static string GetEffectiveGroup(IntPtr hwnd, string? exe, string? title)
		{
			var split = GetSplitKey(exe);
			return split is not null ? GetSplitGroup(split) : GetWindowGroup(hwnd, exe, title);
		}

		/// <summary>The "Use everywhere" stage rect for single-app tiles, or null.</summary>
		public static int[]? GetDefaultStage() => _data.DefaultStage is { Length: 4 } d ? d : null;

		/// <summary>
		/// Makes one rect the spot for every single-app tile and forgets their individual spots
		/// (combined tiles such as Dialpad + Discord keep theirs).
		/// </summary>
		public static void SetDefaultStage(int x, int y, int w, int h)
		{
			_data.DefaultStage = new[] { x, y, w, h };
			foreach (var k in _data.Layouts.Keys.Where(k => !k.StartsWith("split:", StringComparison.OrdinalIgnoreCase)).ToList())
				_data.Layouts.Remove(k);
			Persist();
		}

		/// <summary>Pinned groups, in pin order: always at the top of this desktop's sidebar.</summary>
		public static IReadOnlyList<string> Pinned => _data.Pinned;

		public static bool IsPinned(string group) => _data.Pinned.Contains(group);

		public static void TogglePin(string group)
		{
			if (!_data.Pinned.Remove(group)) _data.Pinned.Add(group);
			Save();
		}

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
			foreach (var r in _data.Rules.Where(r => r.Group == oldName)) r.Group = newName;
			var pi = _data.Pinned.IndexOf(oldName); if (pi >= 0) _data.Pinned[pi] = newName;
			lock (_windowOverrides)
				foreach (var h in _windowOverrides.Where(kv => kv.Value == oldName).Select(kv => kv.Key).ToList())
					_windowOverrides[h] = newName;
			Save();
		}

		/// <summary>Removes a group; its apps go back to ungrouped (nothing is closed).</summary>
		public static void Delete(string name)
		{
			if (!_data.Groups.Remove(name)) return;
			foreach (var app in _data.Apps.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList())
				_data.Apps.Remove(app);
			_data.Rules.RemoveAll(r => r.Group == name);
			_data.Pinned.Remove(name);
			lock (_windowOverrides)
				foreach (var h in _windowOverrides.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList())
					_windowOverrides.Remove(h);
			Save();
		}

		public static void Move(string name, int delta)
		{
			// Pinned groups move within the pins (so pins go in the order you want); others
			// move within the rest of the groups.
			var list = _data.Pinned.Contains(name) ? _data.Pinned : _data.Groups;
			var i = list.IndexOf(name);
			var j = i + delta;
			if (i < 0 || j < 0 || j >= list.Count) return;
			(list[i], list[j]) = (list[j], list[i]);
			Save();
		}


		private static void Save()
		{
			Persist();
			Changed?.Invoke();
		}

		private static void Persist()
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
				File.WriteAllText(FilePath, JsonSerializer.Serialize(_root, new JsonSerializerOptions { WriteIndented = true }));
			}
			catch (Exception ex) { Log.Info("GROUPS", $"Save failed: {ex.Message}"); }
		}
	}
}

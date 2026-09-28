using Microsoft.Win32;
using System;

namespace StageManager.Services
{
	public static class Settings
	{
		private const string REG_KEY = @"SOFTWARE\StageManager\Settings";

		public const int DefaultAnimationDurationMs = 300;
		public const int DefaultMaxScenes = 5;

		public static void SetHideDesktopIcons(bool hideIcons) => SetInt("HideDesktopIcons", hideIcons ? 1 : 0);

		public static bool GetHideDesktopIcons()
		{
			// Legacy: old versions stored bool as REG_SZ — reader always fell through to default true.
			// Treat legacy strings the same way so existing users keep their expected behavior.
			return GetInt("HideDesktopIcons", 1) != 0;
		}

		/// <summary>Scene switch animation length. 0 = no animation (instant switch).</summary>
		public static int GetAnimationDurationMs() => Math.Clamp(GetInt("AnimationDurationMs", DefaultAnimationDurationMs), 0, 2000);
		public static void SetAnimationDurationMs(int ms) => SetInt("AnimationDurationMs", Math.Clamp(ms, 0, 2000));

		/// <summary>How many recent scenes the sidebar shows.</summary>
		public static int GetMaxScenes() => Math.Clamp(GetInt("MaxScenes", DefaultMaxScenes), 1, 12);
		public static void SetMaxScenes(int count) => SetInt("MaxScenes", Math.Clamp(count, 1, 12));

		/// <summary>Clicking empty desktop hides the current scene (and clicking again brings it back).</summary>
		public static bool GetClickDesktopToShowDesktop() => GetInt("ClickDesktopToShowDesktop", 1) != 0;
		public static void SetClickDesktopToShowDesktop(bool enabled) => SetInt("ClickDesktopToShowDesktop", enabled ? 1 : 0);


		private static int GetInt(string name, int fallback)
		{
			using var key = Registry.CurrentUser.OpenSubKey(REG_KEY);
			return key?.GetValue(name) is int i ? i : fallback;
		}

		private static void SetInt(string name, int value)
		{
			using var key = Registry.CurrentUser.CreateSubKey(REG_KEY);
			key?.SetValue(name, value, RegistryValueKind.DWord);
		}
	}
}

using Nox.CCK.Utils;

namespace Nox.Assets.Editor {
	/// <summary>
	/// State of the publisher UI that must survive a domain reload: which foldouts are open, and the
	/// values the user last typed (version policy). Stored in the <b>editor</b> configuration
	/// (<c>Config.LoadEditor()</c>), under <c>assets.ui.&lt;panel&gt;…</c>, so it never ships with the
	/// project settings.
	/// </summary>
	public static class AssetPublishSettings {
		private static readonly string[] Root = { "assets", "ui" };

		/// <summary>Whether the foldout <paramref name="section"/> of <paramref name="panel"/> is open.</summary>
		public static bool Foldout(string panel, string section, bool defaultExpanded = true)
			=> Get(Path(panel, "foldout", section), defaultExpanded);

		/// <summary>Remembers the state of a foldout.</summary>
		public static void SetFoldout(string panel, string section, bool expanded)
			=> Set(Path(panel, "foldout", section), expanded);

		/// <summary>Last value of a setting of the panel (version policy, default channel…).</summary>
		public static T Get<T>(string panel, string key, T fallback = default)
			=> Get(Path(panel, "value", key), fallback);

		public static void Set<T>(string panel, string key, T value)
			=> Set(Path(panel, "value", key), value);

		private static T Get<T>(string[] path, T fallback) {
			try {
				return Config.LoadEditor().Get(path, fallback);
			} catch {
				return fallback;
			}
		}

		private static void Set<T>(string[] path, T value) {
			try {
				var config = Config.LoadEditor();
				config.Set(path, value);
				config.Save();
			} catch {
				// The UI must not break because the configuration could not be written.
			}
		}

		private static string[] Path(string panel, string kind, string key)
			=> new[] { Root[0], Root[1], panel, kind, key };
	}
}

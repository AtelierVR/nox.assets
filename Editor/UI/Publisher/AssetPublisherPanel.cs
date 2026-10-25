using System;
using System.Collections.Generic;
using Nox.Editor.Panel;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Generic publisher panel: a mod only constructs it with its <see cref="AssetPublisherSource"/>
	/// and keeps it in the editor's panel list — the form, the attach flow, the images and the
	/// publish are all here.
	/// </summary>
	public class AssetPublisherPanel : IPanel {
		private readonly AssetPublisherSource _source;

		public AssetPublisherPanel(AssetPublisherSource source)
			=> _source = source ?? throw new ArgumentNullException(nameof(source));

		/// <summary>Single instance of the panel, or <c>null</c> before it is opened.</summary>
		public AssetPublisherInstance Instance { get; internal set; }

		/// <summary>Declaration handed to the panel (the mod's instructions).</summary>
		public AssetPublisherSource Source
			=> _source;

		public string[] GetPath()
			=> _source.Path;

		public IInstance[] GetInstances()
			=> Instance != null ? new IInstance[] { Instance } : Array.Empty<IInstance>();

		/// <summary>
		/// Menu entry: the path given by the mod, Title-cased (<c>avatar/publisher</c> ⇒
		/// <c>Avatar/Publisher</c>). The path itself stays a stable lowercase identifier.
		/// </summary>
		public string GetLabel()
			=> ToMenuLabel(GetPath());

		/// <summary>Turns <c>["avatar", "publisher"]</c> into <c>Avatar/Publisher</c>.</summary>
		internal static string ToMenuLabel(string[] path) {
			if (path == null || path.Length == 0)
				return string.Empty;

			var parts = new string[path.Length];

			for (var i = 0; i < path.Length; i++) {
				var part  = path[i] ?? string.Empty;
				parts[i] = part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part.Substring(1);
			}

			return string.Join("/", parts);
		}

		public IInstance Instantiate(IWindow window, Dictionary<string, object> data) {
			if (Instance != null)
				throw new InvalidOperationException($"{_source.Panel}: the publisher panel only supports a single instance.");

			return Instance = new AssetPublisherInstance(this, window, _source);
		}
	}
}

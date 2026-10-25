using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.Network.Assets;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Editor entry point of the <c>assets</c> mod (declared in <c>nox.mod.json</c> as
	/// <c>entrypoints.editor</c>). It exposes the generic asset pipeline (<c>network</c>) and the
	/// shared cache (<c>assets</c>) to the editor tooling, so the mods can drive their publisher
	/// without reaching into another mod's runtime.
	/// </summary>
	public class Editor : IEditorModInitializer {
		public const string LogTag = "AssetsEditor";

		private IEditorModCoreAPI _core;

		public static Editor Instance { get; private set; }

		/// <summary>Editor core API of the application, or <c>null</c> before initialization.</summary>
		public static IEditorModCoreAPI CoreAPI
			=> Instance?._core;

		/// <summary>Shared asset cache (endpoint resolution, local files), or <c>null</c>.</summary>
		public static IAssetCacheAPI Cache
			=> Self?.GetInstance<IAssetCacheAPI>();

		/// <summary>Generic asset pipeline (fetch, release, upload), or <c>null</c>.</summary>
		public static IAssetsAPI Pipeline
			=> Self?.GetInstance<IAssetsAPI>();

		/// <summary>This mod, without asking the ModAPI for ourselves by id (which logs a warning).</summary>
		private static Nox.CCK.Mods.IMod Self
			=> CoreAPI?.ModAPI.GetSelf();

		public void OnInitializeEditor(IEditorModCoreAPI api) {
			_core    = api;
			Instance = this;

			Logger.LogDebug("Asset editor helpers ready.", tag: LogTag);
		}

		public void OnPreDisposeEditor() {
			Instance = null;
			_core    = null;
		}
	}
}

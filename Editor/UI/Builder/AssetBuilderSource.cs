using System;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.Editor.Panel;

namespace Nox.Assets.Editor {
	/// <summary>
	/// What a mod declares to the generic builder panel: which descriptor it builds, where the
	/// bundles go, how to build them and which extra steps surround the build (save the scene,
	/// rebind after a scene reload).
	/// <para>
	/// The panel — selector, notifications, platforms, output folder, progress and result — comes
	/// from <c>nox.assets</c>.
	/// </para>
	/// </summary>
	public abstract class AssetBuilderSource {
		/// <summary>Identifier used for the persisted UI state (<c>avatar</c>, <c>world</c>…).</summary>
		public abstract string Panel { get; }

		/// <summary>Human name used in the titles.</summary>
		public abstract string Label { get; }

		/// <summary>Path of the editor panel (<c>["world", "builder"]</c>).</summary>
		public abstract string[] Path { get; }

		/// <summary>Title of the editor tab.</summary>
		public abstract string Title { get; }

		/// <summary>Called by the panel when the editor initializes, before any UI is built.</summary>
		public virtual void Initialize(Nox.CCK.Mods.Cores.IEditorModCoreAPI api) { }

		/// <summary>Type of the descriptor selected in the panel.</summary>
		public abstract Type DescriptorType { get; }

		/// <summary>Descriptor currently built; writing it selects another one.</summary>
		public abstract UnityEngine.Object Selection { get; set; }

		/// <summary>Descriptors the selector offers (searchable popup).</summary>
		public abstract UnityEngine.Object[] Candidates();

		/// <summary>Text shown for a descriptor in the selector popup.</summary>
		public virtual string Describe(UnityEngine.Object candidate)
			=> candidate ? candidate.name : "(none)";

		/// <summary>Platforms stored on the descriptor (one bundle per entry).</summary>
		public abstract Platform[] Platforms { get; set; }

		/// <summary>Default output folder of this asset type, when nothing is configured yet.</summary>
		public virtual string DefaultOutputFolder
			=> "Assets/Builds/";

		/// <summary>Builds the variants (progress is reported through <see cref="AssetBuildContext.Report"/>).</summary>
		public abstract Func<AssetBuildContext, UniTask<AssetBuildResult>> Build { get; }

		/// <summary>
		/// Runs before the build (save the scene the pipeline reads…). Returns the error to show, or
		/// <c>null</c>.
		/// </summary>
		public virtual Func<string> Prepare
			=> null;

		/// <summary>Called after a build, when the scenes may have been reloaded.</summary>
		public virtual Action OnBuildFinished
			=> null;

		/// <summary>Toolbar options of the panel (e.g. « Publisher »).</summary>
		public virtual IToolOption[] Options(IWindow window)
			=> Array.Empty<IToolOption>();
	}
}

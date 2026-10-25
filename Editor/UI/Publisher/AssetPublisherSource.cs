using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Convertors;
using Nox.CCK.Network.Assets;
using Nox.Network.Assets;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Everything the generic publisher needs to know about one asset type. A mod only provides this
	/// class — its "instructions" — plus the build itself; the panel, the form, the uploads and the
	/// version policy come from <c>nox.assets</c>.
	/// </summary>
	public abstract class AssetPublisherSource {
		// --- identity ---------------------------------------------------------------------------

		/// <summary>Identifier used for the persisted UI state and the foldout ids (<c>world</c>…).</summary>
		public abstract string Panel { get; }

		/// <summary>Human name used in the section titles.</summary>
		public abstract string Label { get; }

		/// <summary>Path of the editor panel (<c>["world", "publisher"]</c>).</summary>
		public abstract string[] Path { get; }

		/// <summary>Title of the editor tab.</summary>
		public abstract string Title { get; }

		/// <summary>Type of the descriptor selected in the panel (drives the <c>ObjectField</c>).</summary>
		public abstract Type DescriptorType { get; }

		/// <summary>Toolbar options of the panel (e.g. « Builder »), empty by default.</summary>
		public virtual Nox.Editor.Panel.IToolOption[] Options(Nox.Editor.Panel.IWindow window)
			=> Array.Empty<Nox.Editor.Panel.IToolOption>();

		// --- selection --------------------------------------------------------------------------

		/// <summary>Descriptor/object currently edited (the <c>ObjectField</c> of the panel).</summary>
		public abstract UnityEngine.Object Selection { get; set; }

		/// <summary>Compatible objects offered by the searchable selector.</summary>
		public virtual UnityEngine.Object[] Candidates()
			=> Array.Empty<UnityEngine.Object>();

		/// <summary>Text shown for an object in the selector popup.</summary>
		public virtual string Describe(UnityEngine.Object candidate)
			=> candidate ? candidate.name : "(none)";

		// --- attached asset ---------------------------------------------------------------------

		/// <summary>Asset currently attached, or <c>null</c>.</summary>
		public IAsset Attached { get; protected set; }

		/// <summary>Title/description last read from the node (shown in the form).</summary>
		public string AttachedTitle { get; protected set; }

		public string AttachedDescription { get; protected set; }

		/// <summary>Collection served by the node.</summary>
		public abstract AssetEndpoint Endpoint { get; }

		/// <summary>Server of the attached asset, or the current user's server.</summary>
		public virtual string Server
			=> Attached?.Server;

		/// <summary>Reference published: the numeric id of the attached asset.</summary>
		public virtual string AssetRef
			=> Attached?.Id.ToString();

		/// <summary>Version to publish; written back to the descriptor by the implementation.</summary>
		public abstract ushort Version { get; set; }

		/// <summary>Raised when <see cref="Attached"/> changed, so the form refreshes itself.</summary>
		public event Action<IAsset> AttachedChanged;

		/// <summary>Fetches (or creates) the asset with <paramref name="id"/> and attaches it.</summary>
		public abstract UniTask<IAsset> AttachAsync(uint id);

		/// <summary>
		/// Attaches the asset already stored on the descriptor (<c>publishId</c>) without asking the user:
		/// a descriptor that was published before reopens with its asset attached. Returns <c>true</c>
		/// when something was attached; failures stay silent (the attach form is then shown).
		/// </summary>
		public virtual UniTask<bool> TryAutoAttachAsync()
			=> UniTask.FromResult(false);

		/// <summary>Forgets the attached asset when the selection changed.</summary>
		public void ResetAttached()
			=> SetAttached(null);

		/// <summary>Pushes the edited title/description to the node.</summary>
		public abstract UniTask<bool> UpdateAsync(string title, string description);

		/// <summary>Forgets the attached asset.</summary>
		public abstract void Detach();

		/// <summary>Stores the attached asset and notifies the form.</summary>
		protected void SetAttached(IAsset asset) {
			Attached            = asset;
			AttachedTitle       = asset?.Title?.Resolve() ?? string.Empty;
			AttachedDescription = asset?.Description?.Resolve() ?? string.Empty;

			AttachedChanged?.Invoke(asset);
		}

		/// <summary>Re-reads the attached asset (after an update) and notifies the form.</summary>
		public async UniTask RefreshAsync() {
			var assets = Editor.Pipeline;

			if (assets == null || Attached == null || string.IsNullOrEmpty(Server))
				return;

			var asset = await assets.Fetch(Server, Endpoint, AssetRef);

			if (asset != null)
				SetAttached(asset);
		}

		// --- publish ----------------------------------------------------------------------------

		/// <summary>Builds the variants (the only part that stays specific to the asset type).</summary>
		public abstract IAssetBuilder Builder { get; }

		/// <summary>Mod rules added to the pipeline.</summary>
		public abstract AssetPublishConstraints Constraints { get; }

		/// <summary>State handed to the builder (usually the descriptor).</summary>
		public virtual object State
			=> Selection;

		/// <summary>Extra information rows (world capacity, viseme count…).</summary>
		public virtual AssetInfoEntry[] Info
			=> Array.Empty<AssetInfoEntry>();

		// --- releases ---------------------------------------------------------------------------

		/// <summary>
		/// Release note (<c>changelog</c>) of the version about to be published. Persisted in the editor
		/// configuration, so it survives a domain reload.
		/// </summary>
		public string ReleaseNote {
			get => AssetPublishSettings.Get(Panel, "release_note", string.Empty);
			set => AssetPublishSettings.Set(Panel, "release_note", value ?? string.Empty);
		}

		/// <summary>Release currently published under <see cref="Version"/>, or <c>null</c>.</summary>
		public async UniTask<IAssetRelease> FetchCurrentReleaseAsync(CancellationToken token = default) {
			var assets = Editor.Pipeline;

			if (assets == null || string.IsNullOrEmpty(Server) || string.IsNullOrEmpty(AssetRef) || Version == 0)
				return null;

			return await assets.FetchRelease(Server, Endpoint, AssetRef, Version.ToString(), true, token);
		}

		/// <summary>Applies <paramref name="note"/> to the release of the current version.</summary>
		public async UniTask<bool> SaveReleaseNoteAsync(string note, CancellationToken token = default) {
			var assets = Editor.Pipeline;

			if (assets == null || string.IsNullOrEmpty(Server) || string.IsNullOrEmpty(AssetRef) || Version == 0)
				return false;

			var release = await assets.UpdateRelease(
				Server,
				Endpoint,
				AssetRef,
				Version.ToString(),
				new AssetReleaseRequest { Changelog = (note ?? string.Empty).ToTranslated() },
				token
			);

			return release != null;
		}

		/// <summary>
		/// Releases the asset already carries. The node has no list route, so the versions are probed
		/// from <paramref name="from"/> down to 1 and the ones that do not exist are skipped.
		/// </summary>
		public async UniTask<IAssetRelease[]> FetchReleasesAsync(
			int from,
			CancellationToken token = default
		) {
			var assets = Editor.Pipeline;

			if (assets == null || string.IsNullOrEmpty(Server) || string.IsNullOrEmpty(AssetRef))
				return Array.Empty<IAssetRelease>();

			var found = new System.Collections.Generic.List<IAssetRelease>();

			for (var version = Math.Max(from, 0); version >= 1; version--) {
				if (token.IsCancellationRequested)
					break;

				var release = await assets.FetchRelease(Server, Endpoint, AssetRef, version.ToString(), true, token);

				if (release != null)
					found.Add(release);
			}

			return found.ToArray();
		}

		/// <summary>Extra sections (foldouts) of the form.</summary>
		public virtual AssetPublisherSection[] Sections
			=> Array.Empty<AssetPublisherSection>();

		/// <summary>Called by the panel when the editor initializes, before any UI is built.</summary>
		public virtual void Initialize(Nox.CCK.Mods.Cores.IEditorModCoreAPI api) { }

		/// <summary>Called when the auto-increment changed the version.</summary>
		public virtual void OnVersionResolved(ushort version) { }

		/// <summary>Called once the release is published, so the mod refreshes its own state.</summary>
		public virtual void OnPublished(AssetPublishResult result) { }
	}
}

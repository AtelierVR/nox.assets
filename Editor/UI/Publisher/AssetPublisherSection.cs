using UnityEngine.UIElements;

namespace Nox.Assets.Editor {
	/// <summary>
	/// A section a mod adds to the generic publisher (its own foldout): the avatar only needs a
	/// version field, a world may need a capacity and a spawn point, a mod a dependency list.
	/// <para>
	/// The section builds its own content; the view gives it a persisted foldout and the state the
	/// mod passed in <see cref="AssetPublisherSource.State"/>.
	/// </para>
	/// </summary>
	public abstract class AssetPublisherSection {
		/// <summary>Stable identifier, used to persist the open/closed state.</summary>
		public abstract string Id { get; }

		/// <summary>Key of the language entry shown as foldout title (no literal fallback).</summary>
		public abstract string TitleKey { get; }

		/// <summary>State of the foldout the first time the section is shown.</summary>
		public virtual bool DefaultExpanded
			=> true;

		/// <summary>Fills <paramref name="content"/>; <paramref name="source"/> gives access to the context.</summary>
		public abstract void Build(VisualElement content, AssetPublisherSource source);
	}
}

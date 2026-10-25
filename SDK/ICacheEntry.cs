using Nox.CCK.Utils;
using Nox.Network.Assets;

namespace Nox.Assets {
	/// <summary>
	/// A download target of the shared asset cache.
	/// <para>
	/// The entry describes <b>what</b> is downloaded — a raw URL, or the bundle of an asset — and
	/// carries the identity needed to follow a pending analysis. A mod that needs extra data for its
	/// own entries (e.g. a world's capacity) derives its own implementation from
	/// <see cref="IdentifierCache"/>; the cache only consumes the members below.
	/// </para>
	/// </summary>
	public interface ICacheEntry {
		/// <summary>URL of the file to download, or <c>null</c> when the entry is unusable.</summary>
		string Url { get; }

		/// <summary>
		/// Expected content hash in its cache-file form (<c>&lt;type&gt;-&lt;hex&gt;</c>), or
		/// <c>null</c> when the file does not declare one.
		/// </summary>
		string Hash { get; }

		/// <summary>Asset the bundle belongs to, or <see cref="Identifier.Invalid"/> for a raw URL.</summary>
		Identifier Identifier { get; }

		/// <summary>Resolved bundle file, or <c>null</c> for a raw URL.</summary>
		IAssetFile File { get; }

		/// <summary><c>true</c> when the entry describes the bundle of an asset.</summary>
		bool IsBundle { get; }
	}
}

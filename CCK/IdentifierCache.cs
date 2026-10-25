using Nox.CCK.Utils;
using Nox.Network.Assets;

namespace Nox.Assets {
	/// <summary>
	/// The bundle of an asset: its resolved file, plus the identifier needed to refetch the file
	/// status while the node analyzes it.
	/// <para>
	/// Not sealed: a mod with divergent needs derives its own entry (e.g. a world's capacity) and
	/// the cache keeps working with the base members.
	/// </para>
	/// </summary>
	public class IdentifierCache : ICacheEntry {
		public IdentifierCache(Identifier identifier, IAssetFile file) {
			Identifier = identifier;
			File       = file;
			Url        = file?.Url;
			Hash       = ToCacheFileName(file);
		}

		/// <inheritdoc />
		public string Url { get; }

		/// <inheritdoc />
		public string Hash { get; }

		/// <inheritdoc />
		public Identifier Identifier { get; }

		/// <inheritdoc />
		public IAssetFile File { get; }

		/// <inheritdoc />
		public bool IsBundle
			=> File != null;

		/// <summary>
		/// Cache file name of a file's hash (<c>&lt;type&gt;-&lt;hex&gt;</c>), or <c>null</c> when it
		/// declares none. Mirrors the naming of the asset pipeline.
		/// </summary>
		private static string ToCacheFileName(IAssetFile file) {
			var hash = file?.Hashs is { Length: > 0 } ? file.Hashs[0] : null;

			if (hash?.Hex == null)
				return null;

			return hash.Type == null ? hash.Hex : $"{hash.Type}-{hash.Hex}";
		}

		public override string ToString()
			=> $"{nameof(IdentifierCache)}[{Identifier}, {File?.Name ?? Url}, {Hash}]";
	}
}

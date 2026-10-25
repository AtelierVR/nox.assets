using Nox.CCK.Utils;
using Nox.Network.Assets;

namespace Nox.Assets {
	/// <summary>
	/// A raw download: a URL and the hash its bytes must match. Used when the client is given a URL
	/// (a traveling event, an external mirror) instead of a resolved asset bundle.
	/// </summary>
	public sealed class UrlCache : ICacheEntry {
		public UrlCache(string url, string hash = null) {
			Url  = url;
			Hash = hash;
		}

		/// <inheritdoc />
		public string Url { get; }

		/// <inheritdoc />
		public string Hash { get; }

		/// <inheritdoc />
		public Identifier Identifier
			=> Identifier.Invalid;

		/// <inheritdoc />
		public IAssetFile File
			=> null;

		/// <inheritdoc />
		public bool IsBundle
			=> false;

		public override string ToString()
			=> $"{nameof(UrlCache)}[{Url}, {Hash}]";
	}
}

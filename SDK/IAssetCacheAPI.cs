using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.Network.Assets;
using UnityEngine.Events;

namespace Nox.Assets {
	/// <summary>
	/// The <c>assets</c> mod: one cache for every asset kind, and the resolution of an asset kind
	/// from its Nox identifier key through the instance configuration (<c>GET /api/configs</c>).
	/// </summary>
	public interface IAssetCacheAPI {
		/// <summary>
		/// Downloads an entry into the shared cache. An <see cref="IdentifierCache"/> entry also
		/// waits for a pending analysis (<c>409 ANALYSIS_PENDING</c>) before retrying.
		/// </summary>
		ICaching Download(ICacheEntry entry, UnityAction<float> progress = null, CancellationToken token = default);

		/// <summary>Running download for a URL or hash, or <c>null</c>.</summary>
		ICaching GetDownload(string url, string hash);

		/// <summary>Whether a hash is already stored in the cache.</summary>
		bool Has(string hash);

		/// <summary>Removes a hash from the cache.</summary>
		void Clear(string hash);

		/// <summary>Absolute path the cache stores a hash at.</summary>
		string PathFor(string hash);

		/// <summary>
		/// Resolves the asset kind served by the instance for a Nox identifier key (the single
		/// character type of an <see cref="Identifier"/>, e.g. <c>a</c> or <c>w</c>) using the
		/// instance configuration.
		/// </summary>
		UniTask<AssetEndpoint> ResolveEndpoint(string key, string server, CancellationToken token = default);

		/// <summary>
		/// Resolves the bundle an identifier is loaded from (preferred release, then the file matching
		/// the current platform and engine) through the instance configuration.
		/// </summary>
		UniTask<IAssetFile> ResolveBundle(Identifier identifier, CancellationToken token = default);

		/// <summary>
		/// Refetches a bundle file through the status endpoint, reduced to what a wait loop needs
		/// (<see cref="IAssetStatusSnapshot"/>).
		/// </summary>
		UniTask<IAssetStatusSnapshot> FetchStatus(Identifier identifier, IAssetFile file, CancellationToken token = default);

		/// <summary>Resolves and downloads the bundle of an identifier, returning the running download.</summary>
		UniTask<ICaching> DownloadAsset(Identifier identifier, UnityAction<float> progress = null, CancellationToken token = default);
	}
}

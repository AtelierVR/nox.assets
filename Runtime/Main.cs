using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Assets.Runtime.Caching;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.Network;
using Nox.CCK.Network.Assets;
using Nox.CCK.Utils;
using Nox.Network;
using Nox.Network.Assets;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Assets.Runtime {
	/// <summary>
	/// The <c>assets</c> mod: one cache for every asset kind and the resolution of an asset kind from
	/// its Nox identifier key through the instance configuration (<c>GET /api/configs</c>).
	/// <para>
	/// A mod such as <c>nox.avatars</c> only knows its own identity (an <see cref="Identifier"/> and
	/// the bundle it resolved); everything about the cache — where files live, the analysis wait,
	/// the progress and the events — is handled here.
	/// </para>
	/// </summary>
	public class Main : IMainModInitializer, IAssetCacheAPI {
		public const string LogTag = "Assets";

		/// <summary>Lifetime of a cached instance configuration.</summary>
		private static readonly TimeSpan ConfigTtl = TimeSpan.FromMinutes(5);

		private IMainModCoreAPI _core;
		private CacheManager    _cache;

		private readonly Dictionary<string, CachedConfig> _configs = new();

		public static Main Instance { get; private set; }

		public IMainModCoreAPI CoreAPI
			=> _core;

		internal CacheManager Cache
			=> _cache;

		internal INetworkAPI NetworkAPI
			=> _core?.ModAPI.GetMod("network")?.GetInstance<INetworkAPI>();

		internal IAssetsAPI AssetsAPI
			=> _core?.ModAPI.GetSelf()?.GetInstance<IAssetsAPI>();

		public void OnInitializeMain(IMainModCoreAPI api) {
			_core  = api;
			_cache = new CacheManager();
			Instance = this;

			Logger.LogDebug($"Shared asset cache ready (root: {CacheManager.Root}).", tag: LogTag);
		}

		public void OnDisposeMain() {
			_cache?.Dispose();
			_cache = null;
			_configs.Clear();
			Instance = null;
			_core    = null;
		}

		/* ------------------------------------------------------------------ */
		/* Cache                                                                */
		/* ------------------------------------------------------------------ */

		public ICaching Download(ICacheEntry entry, UnityAction<float> progress = null, CancellationToken token = default)
			=> _cache?.AddDownload(entry, token, progress);

		public ICaching GetDownload(string url, string hash)
			=> _cache?.GetDownload(url, hash);

		public bool Has(string hash)
			=> _cache != null && _cache.Has(hash);

		public void Clear(string hash)
			=> _cache?.Clear(hash);

		public string PathFor(string hash)
			=> _cache?.PathFor(hash);

		/* ------------------------------------------------------------------ */
		/* Endpoint resolution (GET /api/configs)                               */
		/* ------------------------------------------------------------------ */

		public async UniTask<AssetEndpoint> ResolveEndpoint(string key, string server, CancellationToken token = default) {
			if (string.IsNullOrEmpty(key)) {
				Logger.LogWarning("Cannot resolve an asset endpoint: no identifier key.", tag: LogTag);
				return default;
			}

			var config = await GetConfig(server, token);

			if (config?.Assets is not { Length: > 0 }) {
				Logger.LogWarning($"No asset configuration available on {server}.", tag: LogTag);
				return default;
			}

			foreach (var asset in config.Assets)
				if (asset != null && string.Equals(asset.Key, key, StringComparison.OrdinalIgnoreCase))
					return new AssetEndpoint(asset.Type, asset.Endpoint);

			Logger.LogWarning($"No asset kind declared for the '{key}' key on {server}.", tag: LogTag);
			return default;
		}

		private async UniTask<InstanceConfig> GetConfig(string server, CancellationToken token) {
			var address = string.IsNullOrEmpty(server) || server == Identifier.LOCAL_SERVER
				? Config.Load().Get<string>(new[] { "server" })
				: server;

			if (string.IsNullOrEmpty(address))
				return null;

			if (_configs.TryGetValue(address, out var cached) && cached.Expires > DateTime.UtcNow)
				return cached.Config;

			var request = await RequestNode.To(address, AssetRoutes.Configs, RequestExtension.Method.GET) as Request;

			if (request == null) {
				Logger.LogWarning($"Cannot read the configuration of {address}.", tag: LogTag);
				return null;
			}

			await request.Send(token);

			if (!request.Ok()) {
				Logger.LogWarning($"Cannot read the configuration of {address} (HTTP {request.responseCode}).", tag: LogTag);
				return null;
			}

			var response = await request.Node<InstanceConfig>(token);
			var config   = response?.Data;

			if (config == null) {
				Logger.LogWarning($"The configuration of {address} is empty.", tag: LogTag);
				return null;
			}

			_configs[address] = new CachedConfig(config, DateTime.UtcNow + ConfigTtl);
			return config;
		}

		/* ------------------------------------------------------------------ */
		/* Generic bundle download                                             */
		/* ------------------------------------------------------------------ */

		public async UniTask<IAssetFile> ResolveBundle(Identifier identifier, CancellationToken token = default) {
			if (!identifier.IsValid()) {
				Logger.LogWarning("Cannot resolve a bundle from an invalid identifier.", tag: LogTag);
				return null;
			}

			var endpoint = await ResolveEndpoint(identifier.Type, identifier.Server, token);

			if (!endpoint.IsValid)
				return null;

			var release = await ResolveRelease(identifier, endpoint, token);

			if (release == null)
				return null;

			var assets = AssetsAPI;

			if (assets == null) {
				Logger.LogError("Cannot resolve a bundle: the asset pipeline is not available.", tag: LogTag);
				return null;
			}

			var releaseRef = string.IsNullOrEmpty(release.Name) ? release.Id.ToString() : release.Name;
			var files      = await assets.FetchFiles(identifier.Server, endpoint, AssetReference(identifier), releaseRef, token);
			var file       = files.BestFile(PlatformExtensions.CurrentPlatform, EngineExtensions.CurrentEngine);

			if (file == null)
				Logger.LogError(
					$"No compatible bundle for {identifier}: release {releaseRef} has no file for "
					+ $"{PlatformExtensions.CurrentPlatform.GetPlatformName()} on {EngineExtensions.CurrentEngine.GetEngineName()}.",
					tag: LogTag
				);

			return file;
		}

		public async UniTask<IAssetStatusSnapshot> FetchStatus(Identifier identifier, IAssetFile file, CancellationToken token = default) {
			if (!identifier.IsValid() || file == null)
				return null;

			var endpoint = await ResolveEndpoint(identifier.Type, identifier.Server, token);

			if (!endpoint.IsValid)
				return null;

			var release = await ResolveRelease(identifier, endpoint, token);

			if (release == null)
				return null;

			var assets = AssetsAPI;

			if (assets == null) {
				Logger.LogError("Cannot refetch a bundle status: the asset pipeline is not available.", tag: LogTag);
				return null;
			}

			var releaseRef = string.IsNullOrEmpty(release.Name) ? release.Id.ToString() : release.Name;
			var reference  = file.Id > 0 ? file.Id.ToString() : file.Name;
			var slot       = await assets.FetchFile(identifier.Server, endpoint, AssetReference(identifier), releaseRef, reference, token);

			return slot == null ? null : new AssetStatusSnapshot(slot.Status, slot.RefetchAt);
		}

		public async UniTask<ICaching> DownloadAsset(Identifier identifier, UnityAction<float> progress = null, CancellationToken token = default) {
			var file = await ResolveBundle(identifier, token);

			if (file == null)
				return null;

			return Download(new IdentifierCache(identifier, file), progress, token);
		}

		/* ------------------------------------------------------------------ */
		/* Helpers                                                             */
		/* ------------------------------------------------------------------ */

		private async UniTask<IAssetRelease> ResolveRelease(Identifier identifier, AssetEndpoint endpoint, CancellationToken token) {
			var assets = AssetsAPI;

			if (assets == null) {
				Logger.LogError("Cannot resolve a release: the asset pipeline is not available.", tag: LogTag);
				return null;
			}

			var assetRef = AssetReference(identifier);
			var version  = GetVersion(identifier);

			var release = version == ushort.MinValue
				? await assets.FetchPreferredRelease(identifier.Server, endpoint, assetRef, token)
				: await assets.FetchRelease(identifier.Server, endpoint, assetRef, version.ToString(), false, token);

			if (release == null)
				Logger.LogWarning($"No release found for {identifier} (version {version}).", tag: LogTag);

			return release;
		}

		/// <summary>Reference used by the asset routes: the numeric id when there is one, else the name.</summary>
		private static string AssetReference(Identifier identifier)
			=> identifier.Id is uint id && id > 0 ? id.ToString() : identifier.StringId;

		/// <summary>
		/// Version pinned by the identifier (<c>?v=</c>), or <see cref="ushort.MinValue"/> when the
		/// release the asset points at should be resolved (<c>auto</c>).
		/// </summary>
		private static ushort GetVersion(Identifier identifier)
			=> identifier.Query != null
				&& identifier.Query.TryGetValue("v", out var values)
				&& values is { Length: > 0 }
				&& ushort.TryParse(values[0], out var version)
					? version
					: ushort.MinValue;

		private readonly struct CachedConfig {
			public readonly InstanceConfig Config;
			public readonly DateTime       Expires;

			public CachedConfig(InstanceConfig config, DateTime expires) {
				Config  = config;
				Expires = expires;
			}
		}
	}
}

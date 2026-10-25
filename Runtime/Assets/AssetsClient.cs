using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.Network;
using Nox.CCK.Network.Assets;
using Nox.CCK.Utils;
using Nox.Network.Assets;
using UnityEngine.Networking;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Assets.Runtime.Assets {
	/// <summary>
	/// Generic client for the Nox asset pipeline. One instance drives every asset type
	/// (<c>avatar</c>, <c>world</c>, <c>mod</c>, <c>prop</c>, …): the routes only differ by the
	/// <see cref="AssetEndpoint"/> and the downloaded files all share <see cref="AssetCache"/>.
	/// Exposed to other mods as <see cref="IAssetsAPI"/> through the <c>network</c> mod.
	/// </summary>
	public class AssetsClient : IMainModInitializer, IAssetsAPI {
		public const string LogTag = "AssetsClient";

		private AssetCache _cache;

		public IAssetCache Cache
			=> _cache;

		public void OnInitializeMain(IMainModCoreAPI api) {
			_cache = new AssetCache();
			Logger.LogDebug($"Asset pipeline ready (cache: {_cache.Root})", tag: LogTag);
		}

		public void OnDisposeMain() {
			_cache?.Dispose();
			_cache = null;
		}

		#region Requests

		/// <summary>Current server when the caller did not name one.</summary>
		private static string ResolveServer(string server)
			=> string.IsNullOrEmpty(server) ? Config.Load().Get<string>(new[] { "server" }) : server;

		private static async UniTask<Request> NewRequest(string server, string path, string method) {
			var address = ResolveServer(server);

			if (string.IsNullOrEmpty(address)) {
				Logger.LogError($"No server address available for {method} {path}.", tag: LogTag);
				return null;
			}

			var request = await RequestNode.To(address, path, method) as Request;

			if (request == null)
				Logger.LogError($"Could not resolve the gateway of {address} for {method} {path}.", tag: LogTag);

			return request;
		}

		private static async UniTask<T> Read<T>(Request request, string operation, CancellationToken token) {
			NodeResponse<T> response = null;
			Exception failure = null;

			try {
				response = await request.Node<T>(token);
			} catch (OperationCanceledException) {
				// A cancelled request is not a failure: the caller drops the result (search workers
				// are cancelled whenever the page is left). Reading its status would report a bogus 0.
				return default;
			} catch (Exception ex) {
				failure = ex;
			}

			// An error status carrying the JSON envelope is reported by the envelope itself.
			if (response != null && response.HasError()) {
				Logger.LogError(
					$"{operation} failed: {response.Error.Message} ({response.Error.Code}/{response.Error.Status}).",
					tag: LogTag
				);
				return default;
			}

			// Otherwise an error status means the server answered with something that is not the
			// envelope (an error page): report its status, not the parsing failure it caused.
			if (!request.Ok()) {
				Logger.LogError(
					$"{operation} failed with status {request.responseCode}"
					+ (string.IsNullOrEmpty(request.error) ? string.Empty : $" ({request.error})")
					+ ".",
					tag: LogTag
				);
				return default;
			}

			if (failure != null) {
				Logger.LogError(failure, tag: LogTag);
				return default;
			}

			return response == null ? default : response.Data;
		}

		private static UniTask<T> Get<T>(string server, string path, string operation, CancellationToken token)
			=> Send<T>(server, path, RequestExtension.Method.GET, null, operation, token);

		private static async UniTask<T> Send<T>(
			string server,
			string path,
			string method,
			object body,
			string operation,
			CancellationToken token
		) {
			var request = await NewRequest(server, path, method);

			if (request == null)
				return default;

			if (body != null)
				request.SetBody(JsonConvert.SerializeObject(body), "application/json");

			await request.Send(token);
			return await Read<T>(request, operation, token);
		}

		private static async UniTask<bool> Remove(string server, string path, CancellationToken token) {
			var request = await NewRequest(server, path, RequestExtension.Method.DELETE);

			if (request == null)
				return false;

			await request.Send(token);

			var response = await Read<AssetDeleteResponse>(request, $"DELETE {path}", token);
			return response?.Success ?? request.Ok();
		}

		private static IReadOnlyDictionary<string, string> AuthHeaders(string address) {
			var authorization = RequestNode.GetAuthorization(address);
			return string.IsNullOrEmpty(authorization)
				? null
				: new Dictionary<string, string> { { "Authorization", authorization } };
		}

		private static void AttachServer(IAsset asset, string address) {
			if (asset is BaseAsset native && string.IsNullOrEmpty(native.Server))
				native.Server = address;
		}

		#endregion

		#region Reads

		public async UniTask<IAssetSearchResponse> Search(
			string server,
			AssetEndpoint endpoint,
			IAssetSearchRequest request = null,
			CancellationToken token = default
		)
			=> await Search<AssetSearchResponse>(server, endpoint, request, token);

		public async UniTask<TResponse> Search<TResponse>(
			string server,
			AssetEndpoint endpoint,
			IAssetSearchRequest request = null,
			CancellationToken token = default
		) where TResponse : IAssetSearchResponse {
			if (!endpoint.IsValid) {
				Logger.LogError($"Invalid asset endpoint '{endpoint}'.", tag: LogTag);
				return default;
			}

			var address = ResolveServer(server);
			var search  = AssetSearchRequest.From(request);
			var path    = AssetRoutes.Collection(endpoint) + search.QueryString;

			var response = await Get<TResponse>(server, path, $"GET {path}", token);

			if (response == null)
				return default;

			if (response.Items != null)
				foreach (var item in response.Items)
					AttachServer(item, address);

			if (response is IAssetSearchNavigator navigable) {
				navigable.Request   = search;
				navigable.Navigator = (next, nextToken) => Search(server, endpoint, next, nextToken);
			}

			return response;
		}

		public async UniTask<IAsset> Fetch(
			string server,
			AssetEndpoint endpoint,
			string asset,
			bool full = false,
			CancellationToken token = default
		)
			=> await Fetch<BaseAsset>(server, endpoint, asset, full, token);

		public async UniTask<TAsset> Fetch<TAsset>(
			string server,
			AssetEndpoint endpoint,
			string asset,
			bool full = false,
			CancellationToken token = default
		) where TAsset : IAsset {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset)) {
				Logger.LogError($"Invalid asset reference '{asset}' for '{endpoint}'.", tag: LogTag);
				return default;
			}

			var path   = AssetRoutes.Asset(endpoint, asset) + (full ? "?full=1" : string.Empty);
			var result = await Get<TAsset>(server, path, $"GET {path}", token);

			if (result != null)
				AttachServer(result, ResolveServer(server));

			return result;
		}

		public async UniTask<IAssetRelease> FetchRelease(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string release,
			bool full = false,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(release)) {
				Logger.LogError($"Invalid release reference '{asset}/{release}' for '{endpoint}'.", tag: LogTag);
				return null;
			}

			var path = AssetRoutes.Release(endpoint, asset, release) + (full ? "?full=1" : string.Empty);
			return await Get<AssetRelease>(server, path, $"GET {path}", token);
		}

		public async UniTask<IAssetRelease> FetchPreferredRelease(
			string server,
			AssetEndpoint endpoint,
			string asset,
			CancellationToken token = default
		) {
			var fetched    = await Fetch(server, endpoint, asset, true, token);
			var preference = fetched?.Release;
			// With ?full=1 the API sends { auto, value }: value is the release the client should
			// use (the pinned one, or the latest while auto is true). The older resolved field is
			// kept as a fallback for servers that still expose it.
			var release = preference == null
				? AssetReleasePreference.None
				: preference.Value > 0
					? preference.Value
					: preference.Resolved;

			if (release <= 0) {
				Logger.LogWarning($"Asset '{asset}' has no resolved release.", tag: LogTag);
				return null;
			}

			// A release never embeds its files, so `full` would only expand the url: leave it off.
			return await FetchRelease(server, endpoint, asset, release.ToString(), false, token);
		}

		public async UniTask<IAssetFile> FetchFile(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string release,
			string file,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(release) || string.IsNullOrEmpty(file)) {
				Logger.LogError($"Invalid file reference '{asset}/{release}/{file}' for '{endpoint}'.", tag: LogTag);
				return null;
			}

			var path = AssetRoutes.FileStatus(endpoint, asset, release, file);
			return await Get<AssetFile>(server, path, $"GET {path}", token);
		}

		public async UniTask<IAssetFile[]> FetchFiles(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string release,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(release)) {
				Logger.LogError($"Invalid release reference '{asset}/{release}' for '{endpoint}'.", tag: LogTag);
				return null;
			}

			// A release never embeds its files: they are paged, so walk the pages until `total`.
			const uint limit = 100;
			var files  = new List<IAssetFile>();
			var offset = 0u;

			while (true) {
				var path = AssetRoutes.Files(endpoint, asset, release) + $"?limit={limit}&offset={offset}";
				var page = await Get<AssetFileSearchResponse>(server, path, $"GET {path}", token);

				if (page == null)
					return files.Count > 0 ? files.ToArray() : null;

				if (page.Items is not { Length: > 0 })
					break;

				files.AddRange(page.Items);

				if (files.Count >= page.Total)
					break;

				offset += (uint)page.Items.Length;
			}

			return files.ToArray();
		}

		public async UniTask<IAssetUploadRules> FetchRules(
			string server,
			AssetEndpoint endpoint,
			CancellationToken token = default
		) {
			const string path = AssetRoutes.Configs;
			var config = await Get<InstanceConfig>(server, path, $"GET {path}", token);

			if (config == null) {
				Logger.LogWarning("Could not read the instance config; the upload rules are unknown.", tag: LogTag);
				return null;
			}

			return config.RulesFor(endpoint);
		}

		#endregion

		#region Writes

		public async UniTask<IAsset> Create(
			string server,
			AssetEndpoint endpoint,
			IAssetCreateRequest request,
			CancellationToken token = default
		)
			=> await Create<BaseAsset>(server, endpoint, request, token);

		public async UniTask<TAsset> Create<TAsset>(
			string server,
			AssetEndpoint endpoint,
			IAssetCreateRequest request,
			CancellationToken token = default
		) where TAsset : IAsset {
			if (!endpoint.IsValid || request == null) {
				Logger.LogError($"Cannot create an asset on '{endpoint}'.", tag: LogTag);
				return default;
			}

			var path   = AssetRoutes.Collection(endpoint);
			var result = await Send<TAsset>(server, path, RequestExtension.Method.PUT, request, $"PUT {path}", token);

			if (result != null)
				AttachServer(result, ResolveServer(server));

			return result;
		}

		public async UniTask<IAsset> Update(
			string server,
			AssetEndpoint endpoint,
			string asset,
			IAssetUpdateRequest request,
			CancellationToken token = default
		)
			=> await Update<BaseAsset>(server, endpoint, asset, request, token);

		public async UniTask<TAsset> Update<TAsset>(
			string server,
			AssetEndpoint endpoint,
			string asset,
			IAssetUpdateRequest request,
			CancellationToken token = default
		) where TAsset : IAsset {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || request == null) {
				Logger.LogError($"Cannot update asset '{asset}' on '{endpoint}'.", tag: LogTag);
				return default;
			}

			var path   = AssetRoutes.Asset(endpoint, asset);
			var result = await Send<TAsset>(server, path, RequestExtension.Method.PATCH, request, $"PATCH {path}", token);

			if (result != null)
				AttachServer(result, ResolveServer(server));

			return result;
		}

		public UniTask<bool> Delete(
			string server,
			AssetEndpoint endpoint,
			string asset,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset)) {
				Logger.LogError($"Cannot delete asset '{asset}' on '{endpoint}'.", tag: LogTag);
				return UniTask.FromResult(false);
			}

			return Remove(server, AssetRoutes.Asset(endpoint, asset), token);
		}

		public async UniTask<IAssetRelease> CreateRelease(
			string server,
			AssetEndpoint endpoint,
			string asset,
			IAssetReleaseRequest request,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || request == null) {
				Logger.LogError($"Cannot create a release on '{endpoint}/{asset}'.", tag: LogTag);
				return null;
			}

			var path = AssetRoutes.Releases(endpoint, asset);
			return await Send<AssetRelease>(server, path, RequestExtension.Method.PUT, request, $"PUT {path}", token);
		}

		public async UniTask<IAssetRelease> UpdateRelease(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string release,
			IAssetReleaseRequest request,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(release) || request == null) {
				Logger.LogError($"Cannot update release '{asset}/{release}' on '{endpoint}'.", tag: LogTag);
				return null;
			}

			var path = AssetRoutes.Release(endpoint, asset, release);
			return await Send<AssetRelease>(server, path, RequestExtension.Method.PATCH, request, $"PATCH {path}", token);
		}

		public UniTask<bool> DeleteRelease(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string release,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(release)) {
				Logger.LogError($"Cannot delete release '{asset}/{release}' on '{endpoint}'.", tag: LogTag);
				return UniTask.FromResult(false);
			}

			return Remove(server, AssetRoutes.Release(endpoint, asset, release), token);
		}

		public async UniTask<IAssetFile> ReserveFile(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string release,
			IAssetFileReservation reservation = null,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(release)) {
				Logger.LogError($"Cannot reserve a file on '{endpoint}/{asset}/{release}'.", tag: LogTag);
				return null;
			}

			var path = AssetRoutes.Files(endpoint, asset, release);
			return await Send<AssetFile>(
				server,
				path,
				RequestExtension.Method.POST,
				AssetFileReservation.From(reservation),
				$"POST {path}",
				token
			);
		}

		public async UniTask<IAssetFile> UploadFile(
			string server,
			AssetEndpoint endpoint,
			IAssetFile slot,
			string filePath,
			IAssetUploadOptions options = null,
			Action<float, ulong> progress = null,
			CancellationToken token = default
		) {
			if (slot == null || string.IsNullOrEmpty(slot.Url)) {
				Logger.LogError("Cannot upload: the reserved slot has no upload URL.", tag: LogTag);
				return null;
			}

			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
				Logger.LogError($"Cannot upload '{filePath}': file not found.", tag: LogTag);
				return null;
			}

			var settings = AssetUploadOptions.From(options);
			var address  = ResolveServer(server);
			var request  = await RequestNode.ToUrl(address, slot.Url, RequestExtension.Method.PUT) as Request;

			if (request == null) {
				Logger.LogError($"Could not create the upload request for '{slot.Url}'.", tag: LogTag);
				return null;
			}

			var length = new FileInfo(filePath).Length;

			if (settings.Length > 0 && settings.Length != length)
				Logger.LogWarning($"Declared length ({settings.Length}) differs from '{filePath}' ({length} bytes).", tag: LogTag);

			var hash = AssetHash.Has(settings.Hash)
				? settings.Hash
				: await UniTask.RunOnThreadPool(
					() => AssetHash.Parse(Hashing.HashFile(AssetHash.Sha256, filePath)),
					cancellationToken: token
				);

			request.uploadHandler   = new UploadHandlerFile(filePath);
			request.downloadHandler = new DownloadHandlerBuffer();
			// A hash without an algorithm is sent as the pipeline default (sha256).
			request.SetRequestHeader("Content-Hash", Hashing.Format(hash.Type, hash.Hex));
			request.SetRequestHeader(
				"Content-Type",
				string.IsNullOrEmpty(settings.Mime) ? "application/octet-stream" : settings.Mime
			);

			if (!string.IsNullOrEmpty(settings.Name))
				request.SetRequestHeader("Content-Disposition", $"attachment; filename=\"{settings.Name}\"");

			if (!string.IsNullOrEmpty(settings.Signature))
				request.SetRequestHeader("Content-Signature", settings.Signature);

			if (progress != null)
				request.HandleUploadProgress((value, size) => progress(value, size), token);

			await request.Send(token);
			return await Read<AssetFile>(request, $"PUT {slot.Url}", token);
		}

		public async UniTask<IAssetFile> Upload(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string release,
			string filePath,
			IAssetFileReservation reservation = null,
			IAssetUploadOptions options = null,
			Action<float, ulong> progress = null,
			CancellationToken token = default
		) {
			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
				Logger.LogError($"Cannot upload '{filePath}': file not found.", tag: LogTag);
				return null;
			}

			var settings = AssetUploadOptions.From(options);
			var slot     = AssetFileReservation.From(reservation);

			if (string.IsNullOrEmpty(slot.Name))
				slot.Name = Path.GetFileName(filePath);

			if (string.IsNullOrEmpty(slot.Mime))
				slot.Mime = string.IsNullOrEmpty(settings.Mime) ? "application/octet-stream" : settings.Mime;

			var reserved = await ReserveFile(server, endpoint, asset, release, slot, token);

			if (reserved == null)
				return null;

			return await UploadFile(server, endpoint, reserved, filePath, settings, progress, token);
		}

		public UniTask<bool> DeleteFile(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string release,
			string file,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(release) || string.IsNullOrEmpty(file)) {
				Logger.LogError($"Cannot delete file '{asset}/{release}/{file}' on '{endpoint}'.", tag: LogTag);
				return UniTask.FromResult(false);
			}

			return Remove(server, AssetRoutes.File(endpoint, asset, release, file), token);
		}

		#endregion

		#region Download & images

		public async UniTask<string> Download(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string release,
			string file,
			Action<float, ulong> progress = null,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(release) || string.IsNullOrEmpty(file)) {
				Logger.LogError($"Cannot download '{asset}/{release}/{file}' from '{endpoint}'.", tag: LogTag);
				return null;
			}

			var address  = ResolveServer(server);
			var deadline = DateTime.UtcNow + AnalysisTimeout;
			var slot     = await FetchFile(server, endpoint, asset, release, file, token);
			var state    = slot?.Status?.Status ?? AssetState.Unknown;

			// The download route only serves analyzed files: a file still being analyzed (fresh
			// upload, or re-analysis of a file analyzed by an older analyzer version) answers a
			// 409 ANALYSIS_PENDING. Poll its status until it settles, then read the slot again
			// (its url and hashes are only final once completed).
			if (state is AssetState.Queued or AssetState.Processing) {
				Logger.Log($"'{asset}/{release}/{file}' is being analyzed ({state}), waiting for it…", tag: LogTag);
				slot  = await WaitForAnalysis(server, endpoint, asset, release, file, deadline, token);
				state = slot?.Status?.Status ?? AssetState.Unknown;
			}

			// url only becomes the download URL once the upload completed: while the slot is
			// awaiting/queued/processing it still points at the upload URL, so its bytes are not
			// downloadable yet.
			if (state != AssetState.Unknown && state != AssetState.Completed) {
				Logger.LogError(
					$"Cannot download '{asset}/{release}/{file}': the file is '{state}'"
					+ " (only completed files are downloadable).",
					tag: LogTag
				);
				return null;
			}

			var hash = slot?.Hashs is { Length: > 0 } ? slot.Hashs[0] : null;

			if (AssetHash.Has(hash) && Cache.Has(hash))
				return Cache.PathFor(hash);

			var gateway = string.IsNullOrEmpty(address) ? null : await NodeDiscover.GetGateway(address);
			var url     = AssetRoutes.File(endpoint, asset, release, file);
			var target  = string.IsNullOrEmpty(gateway) ? url : RequestExtension.MergeUrl(gateway, url);

			while (true) {
				var download = Cache.Download(target, hash, AuthHeaders(address), progress, token);
				var path     = await download.Start();

				if (path != null)
					return path;

				// The pre-check can be outdated (an older analyzer version triggers the re-analysis on
				// the spot): a 409 ANALYSIS_PENDING means the file is not served yet. Refetch its
				// status, wait for the analysis, then retry.
				var pending = download.StatusCode == AssetErrorCodes.AnalysisPendingStatus
					&& string.Equals(
						download.ErrorCode, 
						AssetErrorCodes.AnalysisPending,
						StringComparison.OrdinalIgnoreCase
					);

				if (!pending)
					return null;

				Logger.Log($"'{asset}/{release}/{file}' is still being analyzed, waiting for it…", tag: LogTag);

				slot = await WaitForAnalysis(server, endpoint, asset, release, file, deadline, token);

				if (slot?.Status?.Status != AssetState.Completed)
					return null;

				hash = slot.Hashs is { Length: > 0 } 
					? slot.Hashs[0] 
					: hash;

				if (AssetHash.Has(hash) && Cache.Has(hash))
					return Cache.PathFor(hash);
			}
		}

		/// <summary>
		/// Upper bound of the wait for an analysis, so a stuck analysis cannot block a load forever.
		/// </summary>
		private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(10);

		/// <summary>Delay used while the API does not announce a <c>refetch_at</c>.</summary>
		private static readonly TimeSpan AnalysisFallbackDelay = TimeSpan.FromSeconds(2);

		/// <summary>Longest delay accepted between two polls.</summary>
		private static readonly TimeSpan AnalysisMaxDelay = TimeSpan.FromSeconds(30);

		/// <summary>
		/// Polls the file status route (<c>GET …/files/{file}/status</c>) while the analysis runs,
		/// using the <c>refetch_at</c> announced by the API as the delay.
		/// </summary>
		/// <param name="deadline">Instant after which the wait is abandoned.</param>
		/// <param name="token"></param>
		/// <returns>
		/// The slot once it is <c>completed</c>, or <c>null</c> when the analysis failed, the status
		/// could not be read, the wait timed out or the token was cancelled.
		/// </returns>
		private async UniTask<IAssetFile> WaitForAnalysis(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string release,
			string file,
			DateTime deadline,
			CancellationToken token = default
		) {
			while (!token.IsCancellationRequested) {
				var slot  = await FetchFile(server, endpoint, asset, release, file, token);
				var state = slot?.Status?.Status ?? AssetState.Unknown;

				if (state == AssetState.Completed)
					return slot;

				if (state != AssetState.Queued && state != AssetState.Processing) {
					Logger.LogError(
						$"The analysis of '{asset}/{release}/{file}' ended as '{state}': "
						+ $"{slot?.Status?.Message ?? "no reason given"}.",
						tag: LogTag
					);
					return null;
				}

				if (DateTime.UtcNow >= deadline) {
					Logger.LogWarning(
						$"The analysis of '{asset}/{release}/{file}' is still '{state}' after "
						+ $"{AnalysisTimeout.TotalMinutes:0} minutes, giving up.",
						tag: LogTag
					);
					return null;
				}

				var delay = slot.RefetchAt == default
					? AnalysisFallbackDelay
					: slot.RefetchAt.ToUniversalTime() - DateTime.UtcNow;

				if (delay < TimeSpan.FromMilliseconds(200))
					delay = AnalysisFallbackDelay;
				else if (delay > AnalysisMaxDelay)
					delay = AnalysisMaxDelay;

				await UniTask.Delay(delay, ignoreTimeScale: true, cancellationToken: token);
			}

			return null;
		}

		public async UniTask<string> DownloadUrl(
			string url,
			IAssetHash hash = null,
			Action<float, ulong> progress = null,
			CancellationToken token = default
		) {
			if (string.IsNullOrEmpty(url))
				return null;

			return await Cache.Download(url, hash, null, progress, token).Start();
		}

		public async UniTask<IAsset> AddImage(
			string server,
			AssetEndpoint endpoint,
			string asset,
			string filePath,
			Action<float, ulong> progress = null,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset)) {
				Logger.LogError($"Cannot add an image to '{endpoint}/{asset}'.", tag: LogTag);
				return null;
			}

			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
				Logger.LogError($"Cannot add image '{filePath}': file not found.", tag: LogTag);
				return null;
			}

			var path    = AssetRoutes.Images(endpoint, asset);
			var request = await NewRequest(server, path, RequestExtension.Method.POST);

			if (request == null)
				return null;

			var data = await File.ReadAllBytesAsync(filePath, token);

			request.SetBody(new List<IMultipartFormSection> {
				new MultipartFormFileSection("file", data, Path.GetFileName(filePath), ImageMime(filePath))
			});

			if (progress != null)
				request.HandleUploadProgress((value, size) => progress(value, size), token);

			await request.Send(token);
			var result = await Read<BaseAsset>(request, $"POST {path}", token);

			if (result != null)
				AttachServer(result, ResolveServer(server));

			return result;
		}

		public async UniTask<IAsset> RemoveImage(
			string server,
			AssetEndpoint endpoint,
			string asset,
			int index,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || index < 0) {
				Logger.LogError($"Cannot remove image #{index} from '{endpoint}/{asset}'.", tag: LogTag);
				return null;
			}

			var path    = AssetRoutes.Image(endpoint, asset, index);
			var request = await NewRequest(server, path, RequestExtension.Method.DELETE);

			if (request == null)
				return null;

			await request.Send(token);
			var result = await Read<BaseAsset>(request, $"DELETE {path}", token);

			if (result != null)
				AttachServer(result, ResolveServer(server));

			return result;
		}

		private static string ImageMime(string filePath)
			=> Path.GetExtension(filePath)?.ToLowerInvariant() switch {
				".png"  => "image/png",
				".jpg"  => "image/jpeg",
				".jpeg" => "image/jpeg",
				".gif"  => "image/gif",
				".webp" => "image/webp",
				".avif" => "image/avif",
				_       => "application/octet-stream"
			};

		public async UniTask<IAsset> UpdateImage(
			string server,
			AssetEndpoint endpoint,
			string asset,
			int index,
			int priority,
			string alt,
			CancellationToken token = default
		) {
			if (!endpoint.IsValid || string.IsNullOrEmpty(asset) || index < 0) {
				Logger.LogError($"Cannot update image #{index} of '{endpoint}/{asset}'.", tag: LogTag);
				return null;
			}

			var path = AssetRoutes.Image(endpoint, asset, index);
			var body = new AssetImageUpdateRequest { Priority = priority, Alt = alt };

			var request = await NewRequest(server, path, RequestExtension.Method.PATCH);

			if (request == null)
				return null;

			request.SetBody(JsonConvert.SerializeObject(body), "application/json");

			await request.Send(token);
			var result = await Read<BaseAsset>(request, $"PATCH {path}", token);

			if (result != null)
				AttachServer(result, ResolveServer(server));

			return result;
		}

		#endregion
	}
}

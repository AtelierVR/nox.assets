using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Convertors;
using Nox.CCK.Network.Assets;
using Nox.CCK.Utils;
using Nox.Network.Assets;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Assets.Editor {
	/// <summary>
	/// The publish pipeline shared by every asset type: verify the asset, resolve the version
	/// (auto-increment, strict mode), build the variants through the mod's <see cref="IAssetBuilder"/>,
	/// create the release, then upload one file per platform while honouring the upload rules of the
	/// instance (<c>GET /api/configs</c>).
	/// <para>
	/// The mod only supplies what it knows: how to build, and its extra constraints
	/// (<see cref="AssetPublishConstraints"/>).
	/// </para>
	/// </summary>
	public static class AssetPublisher {
		public const string LogTag = "AssetsPublisher";

		/// <summary>Runs the whole publish. Never throws: a failure is reported in the result.</summary>
		public static async UniTask<AssetPublishResult> Publish(AssetPublishRequest request, CancellationToken token = default) {
			if (request == null)
				return AssetPublishResult.Failed("No publish request.");

			if (token == default)
				token = request.Token;

			void Report(float progress, string message)
				=> request.Progress?.Invoke(progress, message);

			var constraints = request.Constraints;

			// --- Local validation, before any request -------------------------------------------
			if (string.IsNullOrEmpty(request.Server))
				return AssetPublishResult.Failed("No server address available.");

			if (!request.Endpoint.IsValid)
				return AssetPublishResult.Failed("No asset endpoint resolved for this type.");

			if (string.IsNullOrEmpty(request.Asset))
				return AssetPublishResult.Failed("No asset attached: attach one before publishing.");

			if (request.Builder == null)
				return AssetPublishResult.Failed("No builder available for this asset type.");

			if (request.Version == 0)
				return AssetPublishResult.Failed("Asset version cannot be 0.");

			var error = constraints?.Validate(request);

			if (!string.IsNullOrEmpty(error))
				return AssetPublishResult.Failed(error);

			var platforms = constraints?.Platforms(request) ?? request.Platforms ?? Array.Empty<Platform>();

			if (platforms.Length == 0)
				platforms = new[] { PlatformExtensions.CurrentPlatform };

			foreach (var platform in platforms)
				if (!platform.IsSupported())
					return AssetPublishResult.Failed($"{platform.GetPlatformName()} is not supported.");

			var assets = Editor.Pipeline;

			if (assets == null)
				return AssetPublishResult.Failed("The asset pipeline is not available.");

			string tempBuildPath = null;

			try {
				// --- Verify the asset and the caller's rights -----------------------------------
				Report(0.05f, "Verifying the asset...");

				var asset = await assets.Fetch(request.Server, request.Endpoint, request.Asset, false, token);

				if (asset == null)
					return AssetPublishResult.Failed($"Failed to read the asset {request.Asset}.");

				error = constraints?.EnsureOwnership(request, asset);

				if (!string.IsNullOrEmpty(error))
					return AssetPublishResult.Failed(error);

				var assetRef = asset.Id > 0 ? asset.Id.ToString() : request.Asset;

				// --- Version policy: a release is named after the version it publishes ----------
				Report(0.1f, "Checking existing releases...");

				var version = request.Version;
				var release = await FetchRelease(assets, request, assetRef, version, token);

				var autoVersion = request.AutoVersion ?? Config.Load().Get("sdk.auto_version", true);
				var strict      = request.StrictVersion ?? Config.Load().Get("sdk.strict_version", true);

				if (release != null && autoVersion) {
					// Auto-increment has priority: publish under the next free version instead.
					var previous = version;

					while (release != null) {
						version++;
						release = await FetchRelease(assets, request, assetRef, version, token);
					}

					request.OnVersionResolved?.Invoke(version);
					constraints?.OnVersionResolved(request, version);

					Logger.Log($"Asset version {previous} already exists. Auto-incremented to version {version}", tag: LogTag);
				}
				else if (release != null && strict) {
					// Strict mode without auto-increment: block the upload.
					return AssetPublishResult.Failed(
						$"Asset version {version} already exists.\n\nPlease increment the version number, enable "
						+ "'Auto increment version', or disable 'Strict version checking' to overwrite."
					);
				}

				// --- Build --------------------------------------------------------------------
				Report(0.15f, "Preparing the build...");

				error = constraints?.Prepare(request, version);

				if (!string.IsNullOrEmpty(error))
					return AssetPublishResult.Failed(error);

				Report(0.2f, $"Building for {platforms.Length} platform(s)...");

				tempBuildPath = CreateTempBuildPath();

				var context = new AssetBuildContext(
					tempBuildPath,
					platforms,
					request.Engine,
					request.State,
					(progress, message) => Report(0.2f + (progress * 0.5f), message),
					token
				);

				var build = await request.Builder.Build(context);

				if (build == null || !build.Success)
					return AssetPublishResult.Failed($"Build failed: {build?.Message ?? "no result"}");

				if (build.Outputs.Count != platforms.Length)
					return AssetPublishResult.Failed($"Expected {platforms.Length} bundle(s), got {build.Outputs.Count}.");

				foreach (var (platform, file) in build.Outputs)
					if (!File.Exists(file))
						return AssetPublishResult.Failed($"Built file not found for {platform.GetPlatformName()}: {file}");

				// --- Release ------------------------------------------------------------------
				Report(0.75f, "Preparing the release...");

				if (release == null)
					release = await assets.CreateRelease(
						request.Server,
						request.Endpoint,
						assetRef,
						new AssetReleaseRequest {
							Name      = version.ToString(),
							Channel   = request.Channel,
							Changelog = string.IsNullOrWhiteSpace(request.ReleaseNote)
								? null
								: request.ReleaseNote.ToTranslated()
						},
						token
					);

				if (release == null)
					return AssetPublishResult.Failed($"Failed to create release {version}.");

				// The instance declares, per asset type, the attributes every file must carry
				// (`required`) and the combinations that must stay unique within a release (`unique`).
				// They are read after the build so the checks run before the first slot is reserved.
				Report(0.77f, "Checking the upload rules...");

				var rules         = await AssetRules.Fetch(request.Server, request.Endpoint, token);
				var existingFiles = await assets.FetchFiles(request.Server, request.Endpoint, assetRef, release.Name, token);

				if (existingFiles == null)
					Logger.LogWarning("Could not read the release files; an existing variant may not be replaced.", tag: LogTag);

				// --- Variants -----------------------------------------------------------------
				var published = new List<string>(platforms.Length);

				for (var i = 0; i < platforms.Length; i++) {
					var platform = platforms[i];

					if (!build.Outputs.TryGetValue(platform, out var bundlePath))
						return AssetPublishResult.Failed($"No bundle was built for {platform.GetPlatformName()}.");

					// One slice of the progress per platform.
					var from = 0.78f + ((0.95f - 0.78f) * i / platforms.Length);
					var to   = 0.78f + ((0.95f - 0.78f) * (i + 1) / platforms.Length);

					var variantError = await AssetUploader.PublishVariant(
						assets,
						request,
						assetRef,
						release,
						platform,
						bundlePath,
						rules,
						existingFiles,
						from,
						to,
						token
					);

					if (variantError != null)
						return AssetPublishResult.Failed(variantError);

					published.Add(platform.GetPlatformName());
				}

				Report(1f, "Published.");

				var result = AssetPublishResult.Published(version, release, published);

				request.OnPublished?.Invoke(result);
				constraints?.OnPublished(request, result);

				return result;
			} catch (OperationCanceledException) {
				return AssetPublishResult.Failed("The publish was cancelled.");
			} catch (Exception ex) {
				Logger.LogError(new Exception("Failed to publish the asset", ex), tag: LogTag);
				return AssetPublishResult.Failed($"An error occurred: {ex.Message}");
			} finally {
				// Whatever happened, the status line of the long steps is dropped.
				request.Status?.Invoke(null);
				CleanupTempPath(tempBuildPath);
			}
		}

		/// <summary>Release of <paramref name="version"/>, or <c>null</c> when it does not exist yet.</summary>
		public static async UniTask<IAssetRelease> FetchRelease(
			IAssetsAPI assets,
			AssetPublishRequest request,
			string assetRef,
			ushort version,
			CancellationToken token = default
		) => await assets.FetchRelease(request.Server, request.Endpoint, assetRef, version.ToString(), false, token);

		/// <summary>
		/// Latest version published for the asset (release names are versions), or <c>0</c> when none
		/// could be read. Used by the inspectors to pre-fill the next version.
		/// </summary>
		public static async UniTask<ushort> DetectLatestVersion(
			string server,
			AssetEndpoint endpoint,
			string assetRef,
			CancellationToken token = default
		) {
			var assets = Editor.Pipeline;

			if (assets == null)
				return 0;

			// The release the asset points at is the newest one (`auto`).
			var release = await assets.FetchPreferredRelease(server, endpoint, assetRef, token);

			if (release == null)
				return 0;

			var name = release.Name?.TrimStart('v', 'V');

			if (!ushort.TryParse(name, out var version)) {
				Logger.LogWarning($"Could not read a version out of release '{release.Name}'.", tag: LogTag);
				return 0;
			}

			return version;
		}

		/// <summary>Creates the temporary directory the builder writes the variants to.</summary>
		public static string CreateTempBuildPath() {
			var tempDir = Path.Combine(Path.GetTempPath(), "NoxAssetBuild", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempDir);
			return tempDir.Replace('\\', '/') + "/";
		}

		/// <summary>Removes the temporary build directory, ignoring failures.</summary>
		public static void CleanupTempPath(string tempPath) {
			try {
				if (!string.IsNullOrEmpty(tempPath) && Directory.Exists(tempPath))
					Directory.Delete(tempPath, true);
			} catch (Exception ex) {
				Logger.LogError($"Failed to cleanup temporary directory: {ex.Message}", tag: LogTag);
			}
		}
	}
}

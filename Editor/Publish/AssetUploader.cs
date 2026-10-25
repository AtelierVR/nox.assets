using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Network.Assets;
using Nox.CCK.Utils;
using Nox.Network.Assets;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Low-level upload helpers shared by every asset type: hash, reserve, stream, then wait for the
	/// server analysis. Everything asset-specific comes from the request (attributes, conflicts), so
	/// the same code publishes an avatar, a world or a mod.
	/// </summary>
	public static class AssetUploader {
		public const string LogTag = "AssetsUploader";

		/// <summary>
		/// Publishes <paramref name="filePath"/> as the <paramref name="platform"/> variant of
		/// <paramref name="release"/>, replacing whatever variant conflicts with it.
		/// </summary>
		/// <returns><c>null</c> on success, otherwise the error to report.</returns>
		public static async UniTask<string> PublishVariant(
			IAssetsAPI assets,
			AssetPublishRequest request,
			string assetRef,
			IAssetRelease release,
			Platform platform,
			string filePath,
			IAssetUploadRules rules,
			IAssetFile[] existing,
			float progressFrom = 0.78f,
			float progressTo = 0.95f,
			CancellationToken token = default
		) {
			if (assets == null)
				return "The asset pipeline is not available.";

			if (release == null)
				return "No release to publish to.";

			var name = platform.GetPlatformName();
			var length = new FileInfo(filePath).Length;
			var sizeMb = length / (1024f * 1024f);

			void Report(float ratio, string message)
				=> request.Progress?.Invoke(progressFrom + ((progressTo - progressFrom) * ratio), message);

			// Attributes declared with the slot: the server matches files by platform+engine, the mod
			// adds its own (capacity, variant…).
			var attributes = BuildAttributes(request, platform);

			// `required` rules: a slot omitting one of them is refused before any byte is sent.
			var missing = AssetRules.Missing(rules, attributes);

			if (missing.Length > 0)
				return $"The release requires {string.Join(", ", missing)} on every file, but the {name} variant "
					+ "does not declare it.";

			// Files are immutable: re-publishing the same version replaces the variant. The instance's
			// `unique` rules resolve the same way — every conflicting combination must be removed
			// before the new slot is reserved, otherwise the server refuses the reservation (400).
			var toRemove = new List<IAssetFile>();

			foreach (var conflict in Conflicts(request, existing, rules, attributes))
				if (conflict != null && !toRemove.Contains(conflict))
					toRemove.Add(conflict);

			// Re-publishing replaces the platform variant even when its engine version changed.
			var previous = existing.BestFile(platform, request.Engine);

			if (previous != null && !toRemove.Contains(previous))
				toRemove.Add(previous);

			foreach (var file in toRemove)
				if (!string.IsNullOrEmpty(file.Name)) {
					Report(0f, $"Replacing the existing {file.Name} variant...");

					if (!await assets.DeleteFile(request.Server, request.Endpoint, assetRef, release.Name, file.Name, token))
						Logger.LogWarning(
							$"Could not remove the previous '{file.Name}' variant of version {release.Name}.",
							tag: LogTag
						);
				}

			Report(0.05f, $"Hashing the {name} bundle ({sizeMb:F1} MB)...");

			var hash = await Hashing.HashFileAsync(
				AssetHash.Sha256,
				filePath,
				ratio => Report(0.05f + (ratio * 0.15f), $"Hashing the {name} bundle... {ratio * 100:F0}%")
			);

			if (string.IsNullOrEmpty(hash))
				return $"Failed to hash the {name} bundle.";

			Report(0.25f, $"Uploading {name} ({sizeMb:F1} MB)...");

			var uploaded = await assets.Upload(
				request.Server,
				request.Endpoint,
				assetRef,
				release.Name,
				filePath,
				new AssetFileReservation {
					Name       = Path.GetFileName(filePath),
					Mime       = "application/octet-stream",
					Attributes = attributes
				},
				new AssetUploadOptions {
					Hash   = AssetHash.Parse(hash),
					Length = length
				},
				(sent, bytes) => {
					if (sent <= 0f && bytes > 0 && length > 0)
						sent = (float)((double)bytes / length);

					Report(0.25f + (sent * 0.35f), $"Uploading {name}... {sent * sizeMb:F2} MB / {sizeMb:F2} MB - {sent * 100:F0}%");
				},
				token
			);

			if (uploaded == null)
				return $"Failed to upload the {name} bundle.";

			Report(0.6f, $"Processing the {name} bundle...");

			var processed = await WaitForProcessing(
				assets,
				request,
				assetRef,
				release,
				uploaded,
				message => request.Status?.Invoke(message),
				token: token
			);

			// The analysis is over: drop the status line, the caller owns the progress bar again.
			request.Status?.Invoke(null);

			if (processed == null)
				return $"Processing the {name} bundle timed out. Please check the server status.";

			if (processed.Status?.Status == AssetState.Failed)
				return $"The {name} bundle was rejected: {processed.Status.Message ?? "Unknown error"}";

			return null;
		}

		/// <summary>
		/// Attributes of a slot: <c>platform</c>, <c>engine</c>, then the ones the mod declares through
		/// its <see cref="AssetPublishConstraints"/>.
		/// </summary>
		public static AssetAttribute[] BuildAttributes(AssetPublishRequest request, Platform platform) {
			var attributes = new List<AssetAttribute> {
				new("platform", platform.GetPlatformName()),
				new("engine", request.Engine.GetEngineName())
			};

			var extra = request.Constraints?.Attributes(request, platform);

			if (extra != null)
				foreach (var attribute in extra)
					if (attribute != null && !string.IsNullOrEmpty(attribute.Key))
						attributes.Add(attribute as AssetAttribute ?? new AssetAttribute(attribute.Key, attribute.Value));

			return attributes.ToArray();
		}

		/// <summary>
		/// Files to remove before reserving the slot: the instance's <c>unique</c> conflicts plus
		/// whatever the mod adds.
		/// </summary>
		public static IAssetFile[] Conflicts(
			AssetPublishRequest request,
			IAssetFile[] existing,
			IAssetUploadRules rules,
			IAssetAttribute[] attributes
		) {
			var generic = AssetRules.Conflicts(existing, rules, attributes);
			var extra   = request.Constraints?.Conflicts(request, existing, rules, attributes);

			if (extra is not { Length: > 0 })
				return generic;

			if (generic is not { Length: > 0 })
				return extra;

			var result = new List<IAssetFile>(generic);

			foreach (var file in extra)
				if (file != null && !result.Contains(file))
					result.Add(file);

			return result.ToArray();
		}

		/// <summary>
		/// Waits for the server to finish analyzing an uploaded file. The upload only receives the
		/// bytes (the file comes back <c>queued</c>), so the analysis runs out of band: the file is
		/// polled — honouring <c>refetch_at</c> — until it completes, fails, or the deadline is
		/// reached.
		/// </summary>
		public static async UniTask<IAssetFile> WaitForProcessing(
			IAssetsAPI assets,
			AssetPublishRequest request,
			string assetRef,
			IAssetRelease release,
			IAssetFile file,
			Action<string> status = null,
			float timeoutSeconds = 300f,
			CancellationToken token = default
		) {
			var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
			var current  = file;

			while (current?.Status != null && current.Status.Status.IsPending()) {
				if (token.IsCancellationRequested)
					return null;

				if (DateTime.UtcNow >= deadline) {
					Logger.LogError($"Asset processing timed out for {assetRef}/{release?.Name}/{current.Name}.", tag: LogTag);
					return null;
				}

				var delay = current.RefetchAt > DateTime.UtcNow
					? (current.RefetchAt - DateTime.UtcNow).TotalSeconds
					: 2d;

				// The label under the bar carries the server's own status text (its message, or the
				// analyzer counter). Nothing to say keeps whatever phase text is already displayed.
				var detail = DescribeStatus(current.Status);

				if (!string.IsNullOrEmpty(detail))
					status?.Invoke(detail);

				await UniTask.Delay(TimeSpan.FromSeconds(Math.Clamp(delay, 0.5d, 30d)), ignoreTimeScale: true, cancellationToken: token);

				// The status route addresses the file by its numeric id, not by its name.
				if (current.Id == 0) {
					Logger.LogError($"No file id available to poll the status of {assetRef}/{release?.Name}/{current.Name}.", tag: LogTag);
					return null;
				}

				current = await assets.FetchFile(
					request.Server,
					request.Endpoint,
					assetRef,
					release?.Name,
					current.Id.ToString(),
					token
				);

				if (current == null) {
					Logger.LogError($"Failed to read the status of {assetRef}/{release?.Name}/{file.Name}.", tag: LogTag);
					return null;
				}
			}

			Logger.Log($"Asset processing completed: {current?.Status?.Status} ({current?.Size ?? 0} bytes).", tag: LogTag);
			return current;
		}

		/// <summary>
		/// Text a pending status carries: the server message when there is one, otherwise the analyzer
		/// counter (<c>45/227</c>) when it can be estimated, otherwise <c>null</c> (the caller keeps the
		/// phase text it already displays).
		/// </summary>
		public static string DescribeStatus(IAssetStatus status) {
			if (!string.IsNullOrEmpty(status?.Message))
				return status.Message;

			if (status?.Progress is { Total: > 0 } progress)
				return $"{progress.Current}/{progress.Total}";

			return null;
		}
	}
}

using System;
using System.Collections.Generic;
using Nox.Network.Assets;

namespace Nox.Assets.Editor {
	/// <summary>Outcome of a publish: what was published, under which version, or why it failed.</summary>
	public sealed class AssetPublishResult {
		private AssetPublishResult(bool success, string message, ushort version, IAssetRelease release, string[] variants) {
			Success  = success;
			Message  = message;
			Version  = version;
			Release  = release;
			Variants = variants ?? Array.Empty<string>();
		}

		public bool Success { get; }

		/// <summary>Failure reason, or the success summary.</summary>
		public string Message { get; }

		/// <summary>Version actually published (after a possible auto-increment).</summary>
		public ushort Version { get; }

		/// <summary>Release the variants were uploaded to, when the publish succeeded.</summary>
		public IAssetRelease Release { get; }

		/// <summary>Platform names whose variant was uploaded.</summary>
		public string[] Variants { get; }

		public static AssetPublishResult Published(ushort version, IAssetRelease release, IReadOnlyList<string> variants, string message = null)
			=> new(
				true,
				message ?? $"Published version {version} ({string.Join(", ", variants)}).",
				version,
				release,
				Copy(variants)
			);

		public static AssetPublishResult Failed(string message)
			=> new(false, message ?? "The publish failed.", 0, null, null);

		private static string[] Copy(IReadOnlyList<string> variants) {
			if (variants is not { Count: > 0 })
				return Array.Empty<string>();

			var copy = new string[variants.Count];
			for (var i = 0; i < copy.Length; i++)
				copy[i] = variants[i];

			return copy;
		}
	}
}

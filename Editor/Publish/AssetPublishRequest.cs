using System;
using System.Threading;
using Nox.CCK.Utils;
using Nox.Network.Assets;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Everything a publish needs: which asset, which version, which platforms, how to build it and
	/// which extra rules the mod enforces. The publisher fills the gaps with the instance
	/// configuration (<c>GET /api/configs</c>) and the shared defaults.
	/// </summary>
	public sealed class AssetPublishRequest {
		/// <summary>Server the asset lives on (<c>https://…</c>).</summary>
		public string Server { get; set; }

		/// <summary>Collection served by the node (<see cref="AssetEndpoint"/>).</summary>
		public AssetEndpoint Endpoint { get; set; }

		/// <summary>Asset reference (numeric id or name) to publish a release for.</summary>
		public string Asset { get; set; }

		/// <summary>Version to publish; <c>0</c> is refused. A release is named after it.</summary>
		public ushort Version { get; set; }

		/// <summary>Release channel (defaults to <see cref="AssetChannel.Stable"/>).</summary>
		public AssetChannel Channel { get; set; } = AssetChannel.Stable;

		/// <summary>Release note (<c>changelog</c>) stored on the created release; may be empty.</summary>
		public string ReleaseNote { get; set; }

		/// <summary>
		/// Platforms to publish. When empty, the mod constraints decide, then the current platform.
		/// </summary>
		public Platform[] Platforms { get; set; } = Array.Empty<Platform>();

		/// <summary>Engine the bundles are built for (defaults to the current engine).</summary>
		public Engine Engine { get; set; } = EngineExtensions.CurrentEngine;

		/// <summary>Builds the variants of this asset type.</summary>
		public IAssetBuilder Builder { get; set; }

		/// <summary>
		/// Rules the mod adds on top of the generic pipeline (required values, extra attributes,
		/// variants to replace…). Optional.
		/// </summary>
		public AssetPublishConstraints Constraints { get; set; }

		/// <summary>Mod-specific state passed to the builder (descriptor, scene, targets…).</summary>
		public object State { get; set; }

		/// <summary>Progress and status of the whole operation.</summary>
		public Action<float, string> Progress { get; set; }

		/// <summary>
		/// Message of a long step that must not take over the progress bar (the server analysis of an
		/// upload). The mod shows it in its own UI — typically <c>AssetNotificationHelper.Status</c> —
		/// and the publisher clears it (passes <c>null</c>) once the step is over.
		/// </summary>
		public Action<string> Status { get; set; }

		/// <summary>
		/// Bumps the version to the next free one instead of failing on an existing release.
		/// <c>null</c> reads <c>sdk.auto_version</c>.
		/// </summary>
		public bool? AutoVersion { get; set; }

		/// <summary>
		/// Refuses an existing release instead of replacing its variants.
		/// <c>null</c> reads <c>sdk.strict_version</c>.
		/// </summary>
		public bool? StrictVersion { get; set; }

		/// <summary>
		/// Called when the version differs from the requested one (auto-increment), so the mod can
		/// write it back to its descriptor.
		/// </summary>
		public Action<ushort> OnVersionResolved { get; set; }

		/// <summary>Called once every variant is published.</summary>
		public Action<AssetPublishResult> OnPublished { get; set; }

		public CancellationToken Token { get; set; } = default;
	}
}

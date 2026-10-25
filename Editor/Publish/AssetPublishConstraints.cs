using System;
using Nox.CCK.Network.Assets;
using Nox.CCK.Utils;
using Nox.Network.Assets;

namespace Nox.Assets.Editor {
	/// <summary>
	/// The rules a mod adds on top of the generic publish pipeline. Every member has a neutral
	/// default, so an asset type only overrides what it actually needs (an avatar version that
	/// cannot be 0, world capacity attributes, prop variants to replace…).
	/// <para>
	/// The rules the <b>instance</b> declares (<c>GET /api/configs</c> → <c>rules.required</c> and
	/// <c>rules.unique</c>) are always enforced by the publisher; this class adds the client-side
	/// ones, applied before anything is sent to the server.
	/// </para>
	/// </summary>
	public abstract class AssetPublishConstraints {
		/// <summary>Checks the request before any request is sent. Return the error, or <c>null</c>.</summary>
		public virtual string Validate(AssetPublishRequest request)
			=> null;

		/// <summary>Platforms to publish. Defaults to the ones of the request.</summary>
		public virtual Platform[] Platforms(AssetPublishRequest request)
			=> request?.Platforms ?? Array.Empty<Platform>();

		/// <summary>
		/// Checks that the current user may publish this asset. Return the error, or <c>null</c>.
		/// The default requires the user to be the owner or a contributor.
		/// </summary>
		public virtual string EnsureOwnership(AssetPublishRequest request, IAsset asset)
			=> asset == null ? "The asset could not be read." : null;

		/// <summary>
		/// Attributes added to every slot on top of <c>platform</c> and <c>engine</c> (a world may
		/// declare its capacity, a mod its dependencies…).
		/// </summary>
		public virtual IAssetAttribute[] Attributes(AssetPublishRequest request, Platform platform)
			=> Array.Empty<IAssetAttribute>();

		/// <summary>
		/// Files of the release that must be removed before the new slot is reserved: the instance's
		/// <c>unique</c> groups by default. Override to replace more variants (older engine builds…).
		/// </summary>
		public virtual IAssetFile[] Conflicts(
			AssetPublishRequest request,
			IAssetFile[] existing,
			IAssetUploadRules rules,
			IAssetAttribute[] attributes
		) => existing.UniqueConflicts(rules, attributes);

		/// <summary>Called when the published version differs from the requested one.</summary>
		public virtual void OnVersionResolved(AssetPublishRequest request, ushort version) { }

		/// <summary>
		/// Called once the version is resolved and before the build: write it back to the descriptor,
		/// save the scene the build reads… Return the error to abort, or <c>null</c>.
		/// </summary>
		public virtual string Prepare(AssetPublishRequest request, ushort version)
			=> null;

		/// <summary>Called once the release and its variants are published.</summary>
		public virtual void OnPublished(AssetPublishRequest request, AssetPublishResult result) { }
	}

	/// <summary>
	/// Declarative <see cref="AssetPublishConstraints"/>: an asset type passes the few rules it needs
	/// as lambdas instead of writing a class. Everything left <c>null</c> keeps the neutral default of
	/// the base class (the instance rules from <c>/api/configs</c> are always enforced).
	/// </summary>
	public sealed class AssetPublishRules : AssetPublishConstraints {
		/// <summary>Checks the request before anything is sent; return the error, or <c>null</c>.</summary>
		public Func<AssetPublishRequest, string> ValidateFunc { get; set; }

		/// <summary>Platforms to publish; defaults to the request ones.</summary>
		public Func<AssetPublishRequest, Platform[]> PlatformsFunc { get; set; }

		/// <summary>Ownership check; return the error, or <c>null</c>.</summary>
		public Func<AssetPublishRequest, IAsset, string> OwnershipFunc { get; set; }

		/// <summary>Extra attributes declared with every slot (capacity, variant…).</summary>
		public Func<AssetPublishRequest, Platform, IAssetAttribute[]> AttributesFunc { get; set; }

		/// <summary>Variants to remove before reserving the slot.</summary>
		public Func<AssetPublishRequest, IAssetFile[], IAssetUploadRules, IAssetAttribute[], IAssetFile[]> ConflictsFunc { get; set; }

		/// <summary>Called when the auto-increment changed the version.</summary>
		public Action<AssetPublishRequest, ushort> VersionResolvedAction { get; set; }

		/// <summary>Called before the build (write the version, save the scene…); return an error to abort.</summary>
		public Func<AssetPublishRequest, ushort, string> PrepareFunc { get; set; }

		/// <summary>Called once the release and its variants are published.</summary>
		public Action<AssetPublishRequest, AssetPublishResult> PublishedAction { get; set; }

		public override string Validate(AssetPublishRequest request)
			=> ValidateFunc?.Invoke(request);

		public override Platform[] Platforms(AssetPublishRequest request)
			=> PlatformsFunc?.Invoke(request) ?? base.Platforms(request);

		public override string EnsureOwnership(AssetPublishRequest request, IAsset asset)
			=> OwnershipFunc != null ? OwnershipFunc(request, asset) : base.EnsureOwnership(request, asset);

		public override IAssetAttribute[] Attributes(AssetPublishRequest request, Platform platform)
			=> AttributesFunc?.Invoke(request, platform) ?? base.Attributes(request, platform);

		public override IAssetFile[] Conflicts(
			AssetPublishRequest request,
			IAssetFile[] existing,
			IAssetUploadRules rules,
			IAssetAttribute[] attributes
		) => ConflictsFunc != null
			? ConflictsFunc(request, existing, rules, attributes)
			: base.Conflicts(request, existing, rules, attributes);

		public override void OnVersionResolved(AssetPublishRequest request, ushort version)
			=> VersionResolvedAction?.Invoke(request, version);

		public override string Prepare(AssetPublishRequest request, ushort version)
			=> PrepareFunc?.Invoke(request, version);

		public override void OnPublished(AssetPublishRequest request, AssetPublishResult result)
			=> PublishedAction?.Invoke(request, result);
	}
}

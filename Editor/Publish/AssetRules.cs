using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Network.Assets;
using Nox.CCK.Utils;
using Nox.Network.Assets;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Upload rules of an asset type, as enforced by the instance (<c>GET /api/configs</c> →
	/// <c>assets[].rules</c>). The generic publisher reads them once per publish and applies them to
	/// every variant; a mod can also read them from its own inspector to warn before a build.
	/// </summary>
	public static class AssetRules {
		public const string LogTag = "AssetsEditor";

		/// <summary>Endpoint the instance declares for the identifier key (<c>a</c>, <c>w</c>, <c>m</c>…).</summary>
		public static async UniTask<AssetEndpoint> Endpoint(string key, string server, CancellationToken token = default) {
			var cache = Editor.Cache;

			if (cache == null) {
				Logger.LogWarning("The asset cache is not available: cannot resolve the endpoint.", tag: LogTag);
				return default;
			}

			var endpoint = await cache.ResolveEndpoint(key, server, token);

			if (!endpoint.IsValid)
				Logger.LogWarning($"The instance does not declare an asset type for the '{key}' key.", tag: LogTag);

			return endpoint;
		}

		/// <summary>
		/// Rules declared for <paramref name="endpoint"/>, or <c>null</c> when the configuration could
		/// not be read (the server still enforces them).
		/// </summary>
		public static async UniTask<IAssetUploadRules> Fetch(string server, AssetEndpoint endpoint, CancellationToken token = default) {
			var assets = Editor.Pipeline;

			if (assets == null) {
				Logger.LogWarning("The asset pipeline is not available: cannot read the upload rules.", tag: LogTag);
				return null;
			}

			var rules = await assets.FetchRules(server, endpoint, token);

			if (rules == null)
				Logger.LogWarning("Could not read the instance upload rules; the server will still enforce them.", tag: LogTag);

			return rules;
		}

		/// <summary>Required attribute keys missing from <paramref name="attributes"/>.</summary>
		public static string[] Missing(IAssetUploadRules rules, IAssetAttribute[] attributes)
			=> rules?.MissingRequired(attributes) ?? Array.Empty<string>();

		/// <summary>Files colliding with <paramref name="attributes"/> under the unique groups.</summary>
		public static IAssetFile[] Conflicts(IAssetFile[] files, IAssetUploadRules rules, IAssetAttribute[] attributes)
			=> files.UniqueConflicts(rules, attributes);

		/// <summary>Human-readable summary of the rules, for the inspectors.</summary>
		public static string Describe(IAssetUploadRules rules) {
			if (rules == null)
				return "unknown";

			var required = rules.Required is { Length: > 0 } req ? string.Join(", ", req) : "none";
			var unique   = rules.Unique is { Length: > 0 } un ? string.Join(" / ", DescribeGroups(un)) : "none";

			return $"required: {required}; unique: {unique}";
		}

		private static IEnumerable<string> DescribeGroups(string[][] groups) {
			foreach (var group in groups)
				yield return group is { Length: > 0 } ? $"[{string.Join("+", group)}]" : "[]";
		}
	}
}

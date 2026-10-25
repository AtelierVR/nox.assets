using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Nox.Assets.Editor {
	/// <summary>
	/// State notifications every publisher shares: « not connected » and « nothing selected ». They are
	/// computed from the <see cref="AssetPublisherSource"/> (its label, its candidates, the descriptor
	/// type), so a mod no longer needs its own notification class.
	/// <para>
	/// The uid is prefixed with the panel id, so several asset types (avatar, world, mod…) can show
	/// their own warning at the same time.
	/// </para>
	/// </summary>
	public static class AssetPublisherNotifications {
		private const string NotConnectedState = "not_connected";
		private const string NoSelectionState  = "no_selection";

		/// <summary>Updates the notifications of <paramref name="source"/> for the current state.</summary>
		public static void Update(AssetPublisherSource source, bool loggedIn) {
			if (source == null)
				return;

			UpdateNotConnected(source, loggedIn);
			UpdateNoSelection(source, loggedIn);
		}

		/// <summary>Removes the notifications of <paramref name="source"/> (panel closed).</summary>
		public static void Clear(AssetPublisherSource source) {
			if (source == null)
				return;

			AssetNotificationHelper.Remove(Uid(source, NotConnectedState));
			AssetNotificationHelper.Remove(Uid(source, NoSelectionState));
		}

		private static void UpdateNotConnected(AssetPublisherSource source, bool loggedIn) {
			var uid = Uid(source, NotConnectedState);

			if (loggedIn) {
				AssetNotificationHelper.Remove(uid);
				return;
			}

			Replace(new AssetNotification(
				uid,
				AssetNotificationType.Warning,
				new[] { "assets.notifications.not_connected", source.Label.ToLowerInvariant() }
			));
		}

		private static void UpdateNoSelection(AssetPublisherSource source, bool loggedIn) {
			var uid = Uid(source, NoSelectionState);

			// Only meaningful once logged in: otherwise « not connected » is the real problem.
			if (!loggedIn || source.Selection != null) {
				AssetNotificationHelper.Remove(uid);
				return;
			}

			var candidates = source.Candidates()?.Where(candidate => candidate != null).ToArray()
				?? System.Array.Empty<UnityEngine.Object>();

			var actions = candidates.Length > 0
				? new[] {
					new AssetNotificationAction(new[] { "assets.common.select" }, () => SelectCandidate(candidates[0])),
					new AssetNotificationAction(new[] { "assets.common.create" }, () => CreateDescriptor(source))
				}
				: new[] {
					new AssetNotificationAction(new[] { "assets.common.create" }, () => CreateDescriptor(source))
				};

			Replace(new AssetNotification(
				uid,
				AssetNotificationType.Warning,
				candidates.Length > 0
					? new[] { "assets.notifications.no_selection", source.Label.ToLowerInvariant() }
					: new[] { "assets.notifications.none_found", source.Label },
				actions
			));
		}

		private static void SelectCandidate(UnityEngine.Object candidate) {
			Selection.activeGameObject = candidate is Component component
				? component.gameObject
				: candidate as GameObject;
		}

		private static void CreateDescriptor(AssetPublisherSource source) {
			var gameObject = new GameObject(source.DescriptorType.Name, source.DescriptorType);
			Selection.activeGameObject = gameObject;
			EditorGUIUtility.PingObject(gameObject);
		}

		/// <summary>
		/// Replaces the entry of the same uid (<see cref="AssetNotificationHelper.Set"/> compares the
		/// instance, not the uid, so it would stack a new entry on every refresh).
		/// </summary>
		private static void Replace(AssetNotification notification) {
			AssetNotificationHelper.Remove(notification.Uid);
			AssetNotificationHelper.Add(notification);
		}

		private static string Uid(AssetPublisherSource source, string state)
			=> $"{source.Panel}/{state}";
	}
}

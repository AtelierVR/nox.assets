using System;
using System.Collections.Generic;
using UnityEngine.Events;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Editor notifications shared by every asset type: one list of messages (errors, warnings,
	/// info, success), each addressed by a UID so an inspector can replace or remove its own entries
	/// without touching the others.
	/// <para>
	/// A UID may end with <c>*</c> to address a family (<c>mesh.rw.*</c> removes every
	/// <c>mesh.rw.</c> entry). The editor blocks a publish/build while an error is present
	/// (see <see cref="Allowed"/>).
	/// </para>
	/// </summary>
	public static class AssetNotificationHelper {
		public static readonly UnityEvent<AssetNotification[]> OnNotificationsChanged = new();
		public static readonly List<AssetNotification>         Notifications          = new();

		/// <summary><c>false</c> when an error is pending, i.e. when the asset cannot be published.</summary>
		public static bool Allowed
			=> !Notifications.Exists(n => n.Type == AssetNotificationType.Error);

		public static void Add(AssetNotification notification) {
			Notifications.Add(notification);
			OnNotificationsChanged?.Invoke(Notifications.ToArray());
		}

		public static void Remove(AssetNotification notification) {
			Notifications.Remove(notification);
			OnNotificationsChanged?.Invoke(Notifications.ToArray());
		}

		public static void Remove(string uid) {
			foreach (var notification in GetMany(uid))
				Notifications.Remove(notification);
			OnNotificationsChanged?.Invoke(Notifications.ToArray());
		}

		public static AssetNotification Get(string uid)
			=> Notifications.Find(n => n.Uid == uid);

		public static List<AssetNotification> GetMany(string uid)
			=> uid.StartsWith("*")
				? Notifications.FindAll(n => n.Uid.EndsWith(uid[1..]))
				: uid.EndsWith("*")
					? Notifications.FindAll(n => n.Uid.StartsWith(uid[..^1]))
					: Notifications.FindAll(n => n.Uid == uid);

		public static bool Has(string uid)
			=> GetMany(uid).Count > 0;

		public static void Clear() {
			Notifications.Clear();
			OnNotificationsChanged?.Invoke(Notifications.ToArray());
		}

		/// <summary>Adds the notification, or replaces the one carrying the same instance.</summary>
		public static void Set(AssetNotification notification) {
			Notifications.Remove(notification);
			Notifications.Add(notification);
			OnNotificationsChanged?.Invoke(Notifications.ToArray());
		}
	}

	/// <summary>One editor message about an asset, carrying the actions the user can run from it.</summary>
	public class AssetNotification {
		public string                  Uid;
		public AssetNotificationType   Type;
		public string[]                Content;
		public AssetNotificationAction[] Actions;

		public AssetNotification(
			string uid,
			AssetNotificationType type,
			string[] content,
			AssetNotificationAction[] actions = null
		) {
			Uid     = uid;
			Type    = type;
			Content = content;
			Actions = actions ?? Array.Empty<AssetNotificationAction>();
		}

		public override string ToString()
			=> $"{GetType().Name}[Uid={Uid}, Type={Type}, Content={Content}]";
	}

	/// <summary>Button of a notification: a label and the callback it runs.</summary>
	public class AssetNotificationAction {
		public readonly string[]   Content;
		public readonly UnityEvent Action = new();

		public AssetNotificationAction(string[] content, UnityAction callback = null) {
			Content = content;

			if (callback != null)
				Action.AddListener(callback);
		}
	}

	public enum AssetNotificationType {
		Success,
		Warning,
		Error,
		Info
	}
}

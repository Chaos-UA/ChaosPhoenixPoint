using System;
using System.Reflection;
using HarmonyLib;
using PhoenixPoint.Tactical.Entities;

namespace ChaosFasterTurn
{
	/// <summary>
	/// Appends " (xN)" to the little name popup that already appears above an enemy unit as it
	/// starts acting during the AI turn (TacticalActorViewBase.ShowNameNotification, called once
	/// per unit from TacticalFaction.AIUpdateCrt) - this is the speed indicator, chosen instead of
	/// building a standalone HUD element (a new GameObject/Canvas construction, tried first, but
	/// reverted - see CLAUDE.md) because it only ever reuses a vanilla notification the game
	/// already knows how to show, rather than adding new UI plumbing of our own.
	/// </summary>
	[HarmonyPatch(typeof(TacticalActorViewBase), "ShowNameNotification")]
	internal static class ChaosFasterTurnActivitySuffixPatch
	{
		// NamePopup is a private nested struct with a single public "Name" field - reflected once
		// here rather than referenced directly, since it's not accessible from outside the class.
		private static readonly Type NamePopupType =
			typeof(TacticalActorViewBase).GetNestedType("NamePopup", BindingFlags.NonPublic);
		private static readonly FieldInfo NamePopupNameField = NamePopupType?.GetField("Name");

		/// <summary>
		/// Returning false skips the original method entirely (we've already shown our own
		/// suffixed version of the exact same notification); returning true lets vanilla run
		/// completely untouched, which is also the fallback for every failure path here, so this
		/// patch can never leave the popup missing, only unsuffixed.
		/// </summary>
		private static bool Prefix(TacticalActorViewBase __instance)
		{
			if (NamePopupType == null || NamePopupNameField == null)
			{
				return true;
			}
			if (FasterTurnState.Controller == null)
			{
				return true;
			}
			bool isAITurn = FasterTurnState.Controller.CurrentFaction?.IsControlledByAI == true;
			int multiplier = FasterTurnState.Multiplier;
			if (!isAITurn || multiplier == 1)
			{
				return true;
			}

			try
			{
				object popup = Activator.CreateInstance(NamePopupType);
				NamePopupNameField.SetValue(popup, $"{__instance.ActorBase.DisplayName} (x{multiplier})");
				__instance.ShowNotification("SelectedActor", new object[] { popup, __instance.ActorBase });
			}
			catch (Exception ex)
			{
				FasterTurnState.Log($"ActivitySuffixPatch failed, falling back to vanilla: {ex.Message}");
				return true;
			}
			return false;
		}
	}
}

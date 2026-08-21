using System;
using System.Reflection;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View.ViewModules;
using UnityEngine;
using UnityEngine.UI;

namespace ChaosFaction
{
	/// <summary>
	/// Small "Reinforcements in X turns" label pinned to the top-left of the Tactical HUD
	/// while a Phoenix-base-defense mission has a delayed ally reinforcement pending —
	/// explicit request: "ChaosFasterTurn has text displayed on top left about current
	/// speed, I want you to use similar approach to show text with how many turns left for
	/// allied reinforcement in Phoenix defense mission (perhaps light blue color)."
	/// <para/>
	/// Deliberately copies the sibling ChaosFasterTurn mod's own FasterTurnSpeedBadge
	/// pattern almost verbatim (see that class, ChaosFasterTurn/FasterTurnSpeedBadge.cs)
	/// rather than inventing a new one — same "rebuilt from scratch every Tactical level,
	/// parented under the vanilla End Turn button's own HUD Canvas root since that whole
	/// Canvas is itself re-created per level" approach, and the same reflection trick for
	/// Text.font/.alignment (both typed with UnityEngine.Font/TextAnchor, neither of which
	/// this mod's ModSDK references directly — copy an existing Text's font, and write
	/// alignment via the property's own runtime-resolved enum type). ChaosFactionTactical
	/// calls BuildFor in OnTacticalStart and Clear in OnTacticalEnd so a stale/destroyed
	/// reference is never touched afterwards.
	/// <para/>
	/// Anchored a bit lower than where ChaosFasterTurn's own badge sits (y -70 instead of
	/// -20, same x) purely so the two can coexist without visually overlapping if both mods
	/// happen to be enabled at once — this mod has no dependency on ChaosFasterTurn being
	/// installed at all, the offset is just cheap defensive spacing.
	/// </summary>
	internal static class ReinforcementCountdownBadge
	{
		private static readonly PropertyInfo TextFontProp = typeof(Text).GetProperty("font");
		private static readonly PropertyInfo TextAlignmentProp = typeof(Text).GetProperty("alignment");

		private static Text _label;

		/// <summary>Drops the reference to the previous level's badge (already destroyed with its Canvas).</summary>
		public static void Clear()
		{
			_label = null;
		}

		/// <summary>
		/// Builds the (initially hidden) badge, parented under the vanilla HUD's Canvas
		/// root. Anything going wrong here (a null module somewhere, an unexpected UI
		/// layout) just leaves the badge absent — this is cosmetic, so it must never be
		/// allowed to break the reinforcement spawn itself.
		/// </summary>
		public static void BuildFor(TacticalLevelController controller)
		{
			_label = null;
			try
			{
				UIModuleEndTurnContainer endTurn = controller?.View?.TacticalModules?.EndTurnContainer;
				Transform anchorRoot = endTurn != null ? endTurn.transform.root : null;
				if (anchorRoot == null)
					return;
				// EndTurnContainer's own button may be icon-only (no Text child) - fall back
				// to any Text found anywhere under the HUD root, which is effectively
				// guaranteed.
				Text fontSource = endTurn.GetComponentInChildren<Text>(true) ?? anchorRoot.GetComponentInChildren<Text>(true);

				GameObject badgeGO = new GameObject("ChaosFactionReinforcementBadge", typeof(RectTransform), typeof(Text));
				badgeGO.transform.SetParent(anchorRoot, false);
				badgeGO.transform.SetAsLastSibling();

				RectTransform rect = (RectTransform)badgeGO.transform;
				rect.anchorMin = new Vector2(0f, 1f);
				rect.anchorMax = new Vector2(0f, 1f);
				rect.pivot = new Vector2(0f, 1f);
				rect.anchoredPosition = new Vector2(20f, -70f);
				rect.sizeDelta = new Vector2(340f, 44f);

				Text text = badgeGO.GetComponent<Text>();
				text.text = string.Empty;
				text.fontSize = 26;
				text.color = ChaosFactionColors.LightBlue;
				text.raycastTarget = false;
				if (fontSource != null)
				{
					object font = TextFontProp?.GetValue(fontSource, null);
					if (font != null)
					{
						TextFontProp?.SetValue(text, font, null);
					}
				}
				if (TextAlignmentProp != null)
				{
					object middleLeft = Enum.ToObject(TextAlignmentProp.PropertyType, 3); // TextAnchor.MiddleLeft
					TextAlignmentProp.SetValue(text, middleLeft, null);
				}

				badgeGO.SetActive(false);
				_label = text;
			}
			catch (Exception ex)
			{
				Log.Debug("ReinforcementCountdownBadge.BuildFor failed: " + ex);
				_label = null;
			}
		}

		/// <summary>
		/// Shows (creating no new GameObject — reuses the one from BuildFor) the given
		/// already-localized text, only touching the GameObject/Text state when it actually
		/// changed. Called from ChaosFactionTactical whenever the pending-reinforcement
		/// countdown changes (mission start, and every subsequent player-turn-start).
		/// </summary>
		public static void Show(string text)
		{
			if (_label == null)
				return;
			if (!_label.gameObject.activeSelf)
			{
				_label.gameObject.SetActive(true);
			}
			if (_label.text != text)
			{
				_label.text = text;
			}
		}

		/// <summary>Hidden once the reinforcement squad actually spawns (or if the mission has none pending at all).</summary>
		public static void Hide()
		{
			if (_label == null)
				return;
			if (_label.gameObject.activeSelf)
			{
				_label.gameObject.SetActive(false);
			}
		}
	}
}

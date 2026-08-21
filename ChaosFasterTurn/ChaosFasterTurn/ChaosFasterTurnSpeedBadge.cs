using System;
using System.Reflection;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View.ViewModules;
using UnityEngine;
using UnityEngine.UI;

namespace ChaosFasterTurn
{
	/// <summary>
	/// Small "x3"-style label pinned to the top-left of the Tactical HUD whenever the enemy turn
	/// speed multiplier isn't the default x1. Rebuilt from scratch every Tactical level (parented
	/// under the same Canvas root as the vanilla End Turn button, since that whole HUD Canvas is
	/// itself re-created per level) - <see cref="ChaosFasterTurnTactical"/> calls <see cref="BuildFor"/>
	/// in OnTacticalStart and <see cref="Clear"/> in OnTacticalEnd so a stale/destroyed reference is
	/// never touched afterwards.
	///
	/// This only ever creates and reads a small self-owned GameObject/Text - it never touches
	/// TacticalLevelController.Timing or anything the UI's own coroutines depend on (see CLAUDE.md
	/// "Incident"), so it's unrelated to the hotkey/UI-freeze bug that hit this mod previously.
	/// </summary>
	internal static class FasterTurnSpeedBadge
	{
		// UnityEngine.UI.Text.font/.alignment are typed with UnityEngine.Font/TextAnchor, both of
		// which live outside this mod's ModSDK references (UnityEngine.TextRenderingModule) - same
		// reflection trick ChaosInventory uses elsewhere in this project: copy an existing Text's
		// font, and write alignment via the property's own runtime-resolved enum type, so neither
		// Font nor TextAnchor is ever named directly in source.
		private static readonly PropertyInfo TextFontProp = typeof(Text).GetProperty("font");
		private static readonly PropertyInfo TextAlignmentProp = typeof(Text).GetProperty("alignment");

		private static Text _label;

		/// <summary>Drops the reference to the previous level's badge (already destroyed with its Canvas).</summary>
		public static void Clear() {
			_label = null;
		}

		/// <summary>
		/// Builds the badge, parented under the vanilla HUD's Canvas root, pinned to the top-left
		/// corner. Anything going wrong here (a null module somewhere, an unexpected UI layout)
		/// just leaves the badge absent - this is cosmetic, so it must never be allowed to break
		/// the speed control itself.
		/// </summary>
		public static void BuildFor(TacticalLevelController controller) {
			_label = null;
			try {
				UIModuleEndTurnContainer endTurn = controller?.View?.TacticalModules?.EndTurnContainer;
				Transform anchorRoot = endTurn != null ? endTurn.transform.root : null;
				if (anchorRoot == null) {
					return;
				}
				// EndTurnContainer's own button may be icon-only (no Text child) - fall back to
				// any Text found anywhere under the HUD root, which is effectively guaranteed.
				Text fontSource = endTurn.GetComponentInChildren<Text>(true) ?? anchorRoot.GetComponentInChildren<Text>(true);

				GameObject badgeGO = new GameObject("ChaosFasterTurnSpeedBadge", typeof(RectTransform), typeof(Text));
				badgeGO.transform.SetParent(anchorRoot, false);
				badgeGO.transform.SetAsLastSibling();

				RectTransform rect = (RectTransform)badgeGO.transform;
				rect.anchorMin = new Vector2(0f, 1f);
				rect.anchorMax = new Vector2(0f, 1f);
				rect.pivot = new Vector2(0f, 1f);
				rect.anchoredPosition = new Vector2(20f, -20f);
				rect.sizeDelta = new Vector2(160f, 44f);

				Text text = badgeGO.GetComponent<Text>();
				text.text = string.Empty;
				text.fontSize = 30;
				text.color = new Color(1f, 0.85f, 0.2f);
				text.raycastTarget = false;
				if (fontSource != null) {
					object font = TextFontProp?.GetValue(fontSource, null);
					if (font != null) {
						TextFontProp?.SetValue(text, font, null);
					}
				}
				if (TextAlignmentProp != null) {
					object middleLeft = Enum.ToObject(TextAlignmentProp.PropertyType, 3); // TextAnchor.MiddleLeft
					TextAlignmentProp.SetValue(text, middleLeft, null);
				}

				badgeGO.SetActive(false);
				_label = text;
			}
			catch (Exception ex) {
				Debug.LogWarning($"[ChaosFasterTurn] FasterTurnSpeedBadge.BuildFor failed: {ex.Message}");
				_label = null;
			}
		}

		/// <summary>
		/// Called every frame from ChaosFasterTurnUpdater. Shows "x{multiplier}" while it's an
		/// AI turn and the multiplier isn't the default x1; hidden otherwise.
		/// </summary>
		public static void Refresh(bool isAITurn, int multiplier) {
			if (_label == null) {
				return;
			}
			bool shouldShow = isAITurn && multiplier != 1;
			if (_label.gameObject.activeSelf != shouldShow) {
				_label.gameObject.SetActive(shouldShow);
			}
			if (shouldShow) {
				string desired = "x" + multiplier;
				if (_label.text != desired) {
					_label.text = desired;
				}
			}
		}
	}
}

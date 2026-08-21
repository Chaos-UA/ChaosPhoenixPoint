using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Base.Input;
using Base.UI;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.View.ViewControllers;
using PhoenixPoint.Common.View.ViewControllers.Inventory;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Geoscape.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.DataObjects;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.View.ViewControllers.VehicleEquipmentInventory;
using PhoenixPoint.Geoscape.View.ViewControllers.VehicleRoster;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChaosInventory
{
	internal static class Log
	{
		public static void Debug(string message)
		{
			if (ChaosInventoryMain.Cfg != null && ChaosInventoryMain.Cfg.EnableDebugLog)
			{
				UnityEngine.Debug.Log("[ChaosInventory] " + message);
			}
		}
	}

	/// <summary>
	/// Which kind of logical location a merged-view entry/destination represents. Introduced by the
	/// metadata-overlay redesign: a Vehicle and GlobalUntagged location can share the exact same
	/// real backing ItemStorage (the faction's own real GeoFaction.ItemStorage), so `ItemStorage`
	/// reference equality can no longer be used to tell locations apart the way it could when every
	/// vehicle had its own separate real storage object.
	/// </summary>
	internal enum LocationKind { Base, Vehicle, GlobalUntagged }

	/// <summary>
	/// Identity of one logical location (a specific base, a specific aircraft, or "the untagged
	/// remainder of a faction's Global Inventory"), independent of which real ItemStorage object
	/// physically backs it. Value-equality struct (not reference/ItemStorage-based) so a Vehicle
	/// location and a GlobalUntagged location of the same faction - which resolve to the identical
	/// real ItemStorage - still compare as different locations.
	/// </summary>
	internal readonly struct LocationKey : IEquatable<LocationKey>
	{
		public readonly LocationKind Kind;
		public readonly int VehicleId;
		public readonly GeoSite Site;
		public readonly GeoFaction Faction;

		private LocationKey(LocationKind kind, int vehicleId, GeoSite site, GeoFaction faction)
		{
			Kind = kind;
			VehicleId = vehicleId;
			Site = site;
			Faction = faction;
		}

		public static LocationKey ForBase(GeoSite site) => new LocationKey(LocationKind.Base, 0, site, null);
		public static LocationKey ForVehicle(GeoVehicle vehicle) => new LocationKey(LocationKind.Vehicle, vehicle.VehicleID, null, vehicle.Owner);
		public static LocationKey ForGlobalUntagged(GeoFaction faction) => new LocationKey(LocationKind.GlobalUntagged, 0, null, faction);

		public bool Equals(LocationKey other)
		{
			if (Kind != other.Kind)
			{
				return false;
			}
			switch (Kind)
			{
				case LocationKind.Base: return Site == other.Site;
				case LocationKind.Vehicle: return VehicleId == other.VehicleId;
				case LocationKind.GlobalUntagged: return Faction == other.Faction;
				default: return false;
			}
		}

		public override bool Equals(object obj) => obj is LocationKey other && Equals(other);

		public override int GetHashCode()
		{
			switch (Kind)
			{
				case LocationKind.Base: return (Site?.GetHashCode() ?? 0) * 397 ^ 1;
				case LocationKind.Vehicle: return VehicleId * 397 ^ 2;
				case LocationKind.GlobalUntagged: return (Faction?.GetHashCode() ?? 0) * 397 ^ 3;
				default: return 0;
			}
		}
	}

	/// <summary>
	/// One named storage location: an aircraft's own storage, a base's own storage, or the
	/// faction-wide global pool. Used both for the "move to..." destination list and for the Vehicle
	/// Roster tab's combined cargo summary.
	/// </summary>
	internal sealed class StorageOption
	{
		public string Label;
		public ItemStorage Storage;
		public bool IsBase;
		public LocationKey Key;

		// Null for base options. Set for aircraft options so callers can check docked state
		// (e.g. "is this aircraft currently docked at a base") without a second lookup.
		public GeoVehicle Vehicle;
	}

	/// <summary>
	/// Which real ItemStorage the soldier currently being displayed actually belongs to (their own
	/// aircraft if crewing one, otherwise the base they're stationed at), plus every other location
	/// reachable from there right now (docked base/aircraft, or docked-aircraft-at-this-base) and the
	/// faction reference needed to enumerate every OTHER location for the merged display.
	/// </summary>
	internal sealed class LocationContext
	{
		public string Label;
		public ItemStorage Storage;
		public bool IsBase;
		public List<StorageOption> Destinations;
		public GeoFaction Faction;
		public LocationKey Key;

		// Null when the context is a base; set to the aircraft when the context is a vehicle,
		// so callers can check its own docked state (see the global-item destination rule).
		public GeoVehicle Vehicle;
	}

	/// <summary>
	/// One item as displayed in the merged Storage list: which real storage it actually lives in,
	/// what to label it, and whether it's currently reachable (equipable) from here.
	/// </summary>
	internal sealed class StorageEntry
	{
		public GeoItem Item;
		public string LocationLabel;
		public bool Reachable;
		public ItemStorage SourceStorage;
		public LocationKey Location;
		public int OriginalCount;

		// Snapshotted at the same moment as OriginalCount (the last time this entry was tracked),
		// NOT re-read from the live Item reference later - see its use in the "disappeared entirely"
		// removal branch for why. Vanilla's own UIInventoryList.TryLoadItemWithItem (the auto-reload-
		// on-equip mechanic, GetSingleItem().Clone() then Subtract(itself)) can mutate a tracked
		// GeoItem clone IN PLACE between one commit and the next - confirmed via Player.log: a
		// tracked 'Tyr-1 Magazine' clone showed Count=0/CurrentCharges=12 (a stale value left over
		// from CommonItemData.Subtract's own ChargesMax-borrow arithmetic reaching exactly zero) by
		// the time this mod's own "disappeared" removal logic read it, even though it was tracked at
		// Count=1/charges=9 moments earlier - reading the item's LIVE state at removal time is not
		// safe. This field preserves the charges as of the last known-good tracking point instead.
		public int OriginalCharges;
	}

	/// <summary>
	/// ROOT CAUSE of the "split fragment sometimes silently vanishes" mystery that persisted across
	/// v25-v41: found via direct empirical proof, not just source-reading. `GeoItem` defines a
	/// same-signature `public bool Equals(GeoItem other)` helper method (used only by its own `>=`/
	/// `>` operators) comparing purely by (ItemDef, Count, CurrentCharges) - VALUE equality, not
	/// identity. It does NOT implement IEquatable&lt;GeoItem&gt; and does NOT override
	/// object.Equals(object)/GetHashCode() in the source, which should mean
	/// Dictionary&lt;GeoItem,_&gt;/HashSet&lt;GeoItem&gt;/.Distinct() all fall back to plain reference
	/// equality by .NET's own rules - but a direct side-by-side log comparison this session proved
	/// otherwise: `currentlyShown` (built via `.Concat(...).Distinct().ToList()`) was empirically
	/// missing a freshly-created split fragment that an INDEPENDENT, immediately-following re-query
	/// of the identical underlying UnfilteredItems/Slots data (same commit, mere lines later) DID
	/// find - the only way two reads of unchanged data can disagree is if `.Distinct()` folded the
	/// new fragment together with an already-tracked one. This matches EXACTLY what would happen if
	/// two same-ItemDef, same-Count, same-Charges fragments (e.g. two individual 1-unit splits off
	/// the same stack) were being treated as "equal" - i.e. whatever this game's specific Mono/.NET
	/// runtime build actually does when resolving EqualityComparer&lt;GeoItem&gt;.Default, it is
	/// picking up value-based equality from somewhere, contrary to what the source alone suggests.
	/// Every fragment created after the first one that happens to share (ItemDef, Count, Charges)
	/// with an existing tracked entry got silently folded into it - explaining why only ever the
	/// FIRST split of a repeated sequence was ever correctly credited, going all the way back to the
	/// original v25 reports. Fixed by never relying on the default/implicit comparer anywhere a
	/// GeoItem is used as a dictionary/set/distinct key - this explicit reference-identity comparer
	/// is used everywhere instead, regardless of which underlying mechanism was actually at fault.
	/// </summary>
	internal sealed class GeoItemReferenceComparer : IEqualityComparer<GeoItem>
	{
		public static readonly GeoItemReferenceComparer Instance = new GeoItemReferenceComparer();

		public bool Equals(GeoItem x, GeoItem y) => ReferenceEquals(x, y);

		public int GetHashCode(GeoItem obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}

	/// <summary>
	/// Runtime-added click handler for elements this mod builds itself (context menu rows/backdrop) -
	/// a plain Image/Text has no built-in click support. Reused across every clickable element the
	/// context menu creates.
	/// </summary>
	internal sealed class ChaosClickHandler : MonoBehaviour, IPointerClickHandler
	{
		public Action OnClicked;

		public void OnPointerClick(PointerEventData eventData)
		{
			if (eventData.button == PointerEventData.InputButton.Left)
			{
				OnClicked?.Invoke();
			}
		}
	}

	/// <summary>
	/// Runtime-added double-left-click detector, added alongside (not instead of) a real
	/// UIInventorySlot/PhoenixGeneralButton so it gets its own independent IPointerClickHandler
	/// callback from Unity's EventSystem - confirmed safe: Unity invokes every IPointerClickHandler
	/// component present on the hit GameObject, not just the first, so this never interferes with
	/// whatever the other component's own OnPointerClick does (or, for our own EventHandlersEnabled
	/// = false cargo cards, doesn't do). eventData.clickCount is Unity's own built-in multi-click
	/// tracking (timing/position threshold), so no custom timer bookkeeping is needed here.
	/// </summary>
	internal sealed class ChaosDoubleClickHandler : MonoBehaviour, IPointerClickHandler
	{
		public Action OnDoubleClick;

		public void OnPointerClick(PointerEventData eventData)
		{
			if (eventData.button == PointerEventData.InputButton.Left && eventData.clickCount >= 2)
			{
				OnDoubleClick?.Invoke();
			}
		}
	}

	/// <summary>
	/// Runtime-added hover detector, same rationale as ChaosDoubleClickHandler: added ALONGSIDE a
	/// real UIInventorySlot so it gets its own independent IPointerEnter/ExitHandler callback,
	/// entirely unaffected by EventHandlersEnabled=false (that flag only guards UIInventorySlot's
	/// OWN handlers, not a separate component's).
	/// </summary>
	internal sealed class ChaosHoverTooltipHandler : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
	{
		public Action OnHoverEnter;
		public Action OnHoverExit;

		public void OnPointerEnter(PointerEventData eventData)
		{
			OnHoverEnter?.Invoke();
		}

		public void OnPointerExit(PointerEventData eventData)
		{
			OnHoverExit?.Invoke();
		}
	}

	/// <summary>
	/// A small floating tooltip showing an item's display name near the cursor, per explicit user
	/// request for the Air Force tab's cargo cards. Self-built the same way as every other overlay
	/// in this mod (ChaosContextMenu, ChaosPerSlotAircraftLabel) - a background Image plus a Text,
	/// parented under whatever screen root is passed in, forced topmost via the same Canvas-
	/// sorting-order reflection trick used elsewhere so it can't end up hidden behind vanilla UI.
	/// </summary>
	internal static class ChaosItemNameTooltip
	{
		private static GameObject _current;

		public static void Close()
		{
			if (_current != null)
			{
				UnityEngine.Object.Destroy(_current);
				_current = null;
			}
		}

		public static void Show(Transform parentTransform, Vector2 screenPosition, string text, object font)
		{
			Show(parentTransform, screenPosition, text, font, Color.white);
		}

		// Overload accepting an explicit text color - added for the Storage-list "Dock to base to
		// equip" hint (shown in red, matching ChaosPerSlotAircraftLabel.UnreachableColor), while the
		// original Air Force tab cargo-card caller keeps its plain white text unchanged.
		public static void Show(Transform parentTransform, Vector2 screenPosition, string text, object font, Color textColor)
		{
			Close();
			if (parentTransform == null || string.IsNullOrEmpty(text))
			{
				return;
			}

			GameObject root = new GameObject("ChaosItemNameTooltip", typeof(RectTransform), typeof(Image));
			root.transform.SetParent(parentTransform, false);
			ChaosContextMenu.MakeTopmost(root, 1000);
			RectTransform rootRect = (RectTransform)root.transform;
			rootRect.pivot = new Vector2(0f, 1f);
			// No upper clamp on width (previously capped at 400) - a hard cap forces long/Cyrillic
			// strings (e.g. "Dock to МАНТИКОРА 1 to equip") to wrap inside a fixed 36px-tall box, and
			// this mod already confirmed elsewhere in-game (see ChaosGeoscapeVehicleBadge notes) that
			// a wrapped/overflowing Text in this game can render as a completely BLANK label rather
			// than a merely-clipped one - exactly matching the "menu shows but empty" bug report this
			// fixes. Sizing wide enough for the whole string on one line avoids wrapping entirely.
			rootRect.sizeDelta = new Vector2(Mathf.Max(text.Length * 14f + 24f, 80f), 36f);
			rootRect.position = new Vector3(screenPosition.x + 16f, screenPosition.y - 16f, 0f);
			Image rootImage = root.GetComponent<Image>();
			rootImage.color = new Color(0.05f, 0.05f, 0.05f, 0.95f);
			// The actual root cause of the "tooltip is empty" bug: Image defaults raycastTarget to
			// true, and this box is positioned right next to the cursor (offset by only 16px) - so
			// the instant it appears it starts intercepting the pointer raycast the HOVERED SLOT
			// below it depends on to stay "entered". That steals the raycast hit away from the slot,
			// firing the slot's own OnPointerExit in the same/next frame, which calls
			// ChaosItemNameTooltip.Close() - destroying the tooltip almost as soon as it's created.
			// The result is a show/destroy flicker too fast to ever read any text, which is exactly
			// what reads as "broken and empty" (unlike ChaosContextMenu's identically-built panel,
			// which isn't gated by continuous hover so this self-blocking never matters there).
			// Purely visual info, never meant to be interactive - must never be a raycast target.
			rootImage.raycastTarget = false;

			GameObject labelGO = new GameObject("Label", typeof(RectTransform), typeof(Text));
			labelGO.transform.SetParent(root.transform, false);
			RectTransform labelRect = (RectTransform)labelGO.transform;
			labelRect.anchorMin = Vector2.zero;
			labelRect.anchorMax = Vector2.one;
			labelRect.offsetMin = new Vector2(10f, 4f);
			labelRect.offsetMax = new Vector2(-10f, -4f);

			Text label = labelGO.GetComponent<Text>();
			ChaosContextMenu.SetFont(label, font);
			// Left-aligned (upper-left default, same as ChaosContextMenu's own rows - confirmed
			// working in-game), not centered: with an uncapped width the box can now extend well past
			// the right edge of the screen for a long string, and center-aligned text in an
			// off-screen-extending box ends up drawn outside the visible area entirely, which is the
			// other concrete way this same bug could reproduce as "box shows, no visible text."
			label.color = textColor;
			label.fontSize = 22;
			label.raycastTarget = false;
			label.text = text;

			_current = root;
		}
	}

	/// <summary>
	/// A fully self-built right-click context menu (background + rows + an invisible full-screen
	/// backdrop to close on outside-click). Built entirely from runtime GameObjects/Components under
	/// whatever transform is passed in - no dependency on any existing-but-possibly-hidden vanilla UI
	/// element, unlike the two prior repurposing attempts that both turned out to sit inside a
	/// permanently inactive parent in the shipped scene.
	/// </summary>
	internal static class ChaosContextMenu
	{
		public sealed class Option
		{
			public string Label;
			public Action OnClick;

			// Null uses the standard light-blue OptionColor. Set to OwnLocationColor (white) for
			// the option that moves the item back to whatever base/aircraft this screen is
			// currently showing, per explicit user request - that one destination reads as the
			// "usual"/default action, the rest as more deliberate cross-location moves.
			public Color? TextColor;
		}

		// Doubled per user feedback that the first pass rendered too small.
		private const float RowHeight = 52f;
		private const float MenuWidth = 440f;
		private const float Padding = 12f;
		private const int FontSize = 26;

		// Rough average glyph width at FontSize 26 - same per-character estimation approach already
		// used by ChaosItemNameTooltip's own sizing (there, 14f at its smaller FontSize 22; scaled up
		// proportionally here). Used to widen the menu panel to fit its longest row instead of
		// letting SetNoWrap's un-wrapped text overhang past the dark background for long/Cyrillic
		// strings (e.g. "Пристикуйтесь до МАНТИКОРА 1, щоб екіпірувати") - per explicit user request.
		private const float CharWidth = 16f;
		// Matches CreateRow's label offsetMin/offsetMax (8f padding each side).
		private const float RowHorizontalPadding = 16f;

		private static float EstimateRowWidth(string text)
		{
			return string.IsNullOrEmpty(text) ? 0f : text.Length * CharWidth + RowHorizontalPadding;
		}

		// Title row shows the item's current owner (not the item name) and is never clickable -
		// grayed out to read as informational rather than a selectable option. Destination rows
		// are tinted light blue so they read clearly as actionable choices against the dark panel.
		private static readonly Color TitleColor = new Color(0.6f, 0.6f, 0.6f);
		private static readonly Color OptionColor = new Color(0.55f, 0.78f, 1f);

		// UnityEngine.UI.Text.font is typed as UnityEngine.Font, but that type lives in an assembly
		// not present in the ModSDK references (only CoreModule/UI are available here) - copying an
		// existing Text's font via reflection avoids ever naming the Font type directly.
		private static readonly PropertyInfo TextFontProp = typeof(Text).GetProperty("font");

		public static object GetFont(Text source)
		{
			return source == null ? null : TextFontProp?.GetValue(source, null);
		}

		// Same trick as Font: Text.alignment is typed as UnityEngine.TextAnchor, which also lives in
		// an assembly not shipped in the ModSDK (UnityEngine.TextRenderingModule). Enum.ToObject
		// against the property's own runtime-resolved type builds a valid boxed TextAnchor.MiddleCenter
		// (value 4 in Unity's enum) without ever naming the TextAnchor type in this mod's source.
		private static readonly PropertyInfo TextAlignmentProp = typeof(Text).GetProperty("alignment");

		public static void SetCenterAlignment(Text target)
		{
			if (TextAlignmentProp == null)
			{
				return;
			}
			object middleCenter = Enum.ToObject(TextAlignmentProp.PropertyType, 4);
			TextAlignmentProp.SetValue(target, middleCenter, null);
		}

		// Same reflection trick again: Text.horizontalOverflow is typed as UnityEngine.
		// HorizontalWrapMode, also not shipped in the ModSDK. Fixes a truncation bug reported for
		// long/Cyrillic row text (e.g. "Пристикуйтесь до МАНТИКОРА 1, щоб екіпірувати"): with the
		// default Wrap mode, a string too wide for MenuWidth wraps onto a second line, but each
		// row's fixed RowHeight only has room for one - the default VerticalWrapMode.Truncate then
		// silently drops whatever doesn't fit, which is exactly what looked like the trailing
		// ", щоб екіпірувати" clause going missing. HorizontalWrapMode.Overflow (value 1) keeps the
		// row single-line and lets it overflow horizontally instead - readable in full, at the cost
		// of occasionally drawing past the panel's own edge for a very long string, which is
		// preferable to silently losing text.
		private static readonly PropertyInfo TextHorizontalOverflowProp = typeof(Text).GetProperty("horizontalOverflow");

		public static void SetNoWrap(Text target)
		{
			if (TextHorizontalOverflowProp == null)
			{
				return;
			}
			object overflow = Enum.ToObject(TextHorizontalOverflowProp.PropertyType, 1);
			TextHorizontalOverflowProp.SetValue(target, overflow, null);
		}

		public static void SetFont(Text target, object font)
		{
			if (font != null)
			{
				TextFontProp?.SetValue(target, font, null);
			}
		}

		// Same reflection trick as Font/TextAnchor above: UnityEngine.Canvas lives in
		// UnityEngine.UIModule, an assembly not present in this mod's ModSDK references (confirmed
		// back in the v5 notes - CS0246/CS0012 if named directly) - but it's certainly already
		// loaded in the running game's AppDomain (Canvas is fundamental to all of Unity's own UI),
		// so Type.GetType can still resolve and use it at runtime without ever naming it in source.
		// Used as a general "make this self-built element actually render on top" escape hatch:
		// sibling order alone only controls draw order WITHIN the same Canvas, and self-built
		// elements parented deep inside existing vanilla UI (a button's own icon area, an aircraft
		// portrait Image) may sit under a DIFFERENT, lower-sorted sub-canvas than whatever's
		// stacked on top of them elsewhere in that same vanilla hierarchy - a plain SetAsLastSibling
		// can't fix that, since it can't out-rank an entirely different Canvas's own sortingOrder.
		private static readonly Type CanvasType = Type.GetType("UnityEngine.Canvas, UnityEngine.UIModule");
		private static readonly PropertyInfo CanvasOverrideSortingProp = CanvasType?.GetProperty("overrideSorting");
		private static readonly PropertyInfo CanvasSortingOrderProp = CanvasType?.GetProperty("sortingOrder");

		public static void MakeTopmost(GameObject go, int sortingOrder)
		{
			if (go == null || CanvasType == null)
			{
				Log.Debug($"ChaosContextMenu.MakeTopmost: skipped for '{go?.name}' - CanvasType resolved={CanvasType != null}.");
				return;
			}
			Component canvas = go.GetComponent(CanvasType);
			if (canvas == null)
			{
				canvas = go.AddComponent(CanvasType);
			}
			CanvasOverrideSortingProp?.SetValue(canvas, true, null);
			CanvasSortingOrderProp?.SetValue(canvas, sortingOrder, null);
		}

		private static GameObject _current;

		public static void Close()
		{
			if (_current != null)
			{
				UnityEngine.Object.Destroy(_current);
				_current = null;
			}
		}

		// parentTransform.root (rather than a located Canvas component) sidesteps needing the
		// UnityEngine.Canvas type at all - same reflected-assembly problem as Font. Any Transform
		// already part of the visible UI tree (e.g. an existing inventory list) has its Canvas as an
		// ancestor, typically at or very near the root.
		public static void Show(Transform parentTransform, Vector2 screenPosition, string title, object font, List<Option> options, Action onTitleClick = null)
		{
			Close();
			if (parentTransform == null || options == null || options.Count == 0)
			{
				Log.Debug($"ChaosContextMenu.Show: aborted - parentTransform={parentTransform != null}, options={options?.Count ?? -1}.");
				return;
			}

			int rowCount = options.Count + (string.IsNullOrEmpty(title) ? 0 : 1);
			float menuHeight = Padding * 2f + rowCount * RowHeight;
			float menuWidth = Mathf.Max(MenuWidth, EstimateRowWidth(title));
			foreach (Option widthOption in options)
			{
				menuWidth = Mathf.Max(menuWidth, EstimateRowWidth(widthOption.Label));
			}

			GameObject root = new GameObject("ChaosContextMenuRoot", typeof(RectTransform));
			root.transform.SetParent(parentTransform, false);
			RectTransform rootRect = (RectTransform)root.transform;
			rootRect.anchorMin = Vector2.zero;
			rootRect.anchorMax = Vector2.one;
			rootRect.offsetMin = Vector2.zero;
			rootRect.offsetMax = Vector2.zero;
			Log.Debug($"ChaosContextMenu.Show: building menu - parent='{parentTransform.name}' (activeInHierarchy={parentTransform.gameObject.activeInHierarchy}), siblingIndex={root.transform.GetSiblingIndex()}, childCount={parentTransform.childCount}, screenPos={screenPosition}, rows={rowCount}.");

			GameObject backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
			backdrop.transform.SetParent(rootRect, false);
			RectTransform backdropRect = (RectTransform)backdrop.transform;
			backdropRect.anchorMin = Vector2.zero;
			backdropRect.anchorMax = Vector2.one;
			backdropRect.offsetMin = Vector2.zero;
			backdropRect.offsetMax = Vector2.zero;
			backdrop.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.001f);
			backdrop.AddComponent<ChaosClickHandler>().OnClicked = Close;

			GameObject panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
			panel.transform.SetParent(rootRect, false);
			RectTransform panelRect = (RectTransform)panel.transform;
			panelRect.pivot = new Vector2(0f, 1f);
			panelRect.sizeDelta = new Vector2(menuWidth, menuHeight);

			// RectTransformUtility isn't in an assembly the ModSDK ships, so this sets world position
			// directly instead of properly converting screen -> local canvas space. Correct for the
			// common Screen Space Overlay setup (world position ~= screen position there); if this
			// game's UI actually uses Screen Space Camera/World Space the menu may appear offset from
			// the click, worth confirming in-game.
			panelRect.position = new Vector3(screenPosition.x, screenPosition.y, 0f);
			panel.GetComponent<Image>().color = new Color(0.07f, 0.07f, 0.07f, 0.97f);

			int row = 0;
			if (!string.IsNullOrEmpty(title))
			{
				// Title row is clickable only when the caller supplies onTitleClick (e.g. the red
				// hint menu's aircraft/base name, per explicit user request to navigate there the
				// same way the hint text below it already does) - wrapped with the same
				// invoke-then-close pattern as a normal option row. Every other existing caller
				// leaves this null, so the title stays purely informational there, unchanged.
				Action wrappedTitleClick = onTitleClick != null ? (Action)(() =>
				{
					onTitleClick();
					Close();
				}) : null;
				CreateRow(panelRect, font, title, true, wrappedTitleClick, row, new Color(0f, 0f, 0f, 0f), TitleColor);
				row++;
			}
			foreach (Option option in options)
			{
				Action onClick = option.OnClick;
				CreateRow(panelRect, font, option.Label, false, () =>
				{
					onClick?.Invoke();
					Close();
				}, row, new Color(0.18f, 0.18f, 0.18f, 0.9f), option.TextColor ?? OptionColor);
				row++;
			}

			_current = root;
			Log.Debug($"ChaosContextMenu.Show: menu built - activeInHierarchy={root.activeInHierarchy}, worldPos={panelRect.position}.");
		}

		private static void CreateRow(RectTransform parent, object font, string text, bool isTitle, Action onClick, int rowIndex, Color background, Color textColor)
		{
			GameObject rowGO = new GameObject(isTitle ? "Title" : "Option", typeof(RectTransform), typeof(Image));
			rowGO.transform.SetParent(parent, false);
			RectTransform rowRect = (RectTransform)rowGO.transform;
			rowRect.anchorMin = new Vector2(0f, 1f);
			rowRect.anchorMax = new Vector2(1f, 1f);
			rowRect.pivot = new Vector2(0.5f, 1f);
			rowRect.sizeDelta = new Vector2(0f, RowHeight);
			rowRect.anchoredPosition = new Vector2(0f, -(Padding + rowIndex * RowHeight));
			rowGO.GetComponent<Image>().color = background;

			GameObject labelGO = new GameObject("Label", typeof(RectTransform), typeof(Text));
			labelGO.transform.SetParent(rowRect, false);
			RectTransform labelRect = (RectTransform)labelGO.transform;
			labelRect.anchorMin = Vector2.zero;
			labelRect.anchorMax = Vector2.one;
			labelRect.offsetMin = new Vector2(8f, 0f);
			labelRect.offsetMax = new Vector2(-8f, 0f);
			Text labelText = labelGO.GetComponent<Text>();
			SetFont(labelText, font);
			SetNoWrap(labelText);
			labelText.text = text;
			labelText.color = textColor;
			labelText.fontSize = FontSize;
			labelText.raycastTarget = false;
			// TextAnchor/FontStyle live in an assembly the ModSDK doesn't ship, so alignment/bold
			// styling is skipped - text defaults to upper-left, normal weight. Functional, not pretty.

			if (onClick != null)
			{
				rowGO.AddComponent<ChaosClickHandler>().OnClicked = onClick;
			}
		}
	}

	/// <summary>
	/// A location-name tag pinned to the bottom-center of every populated slot in the Storage list -
	/// now driven per-item from the merged multi-location display (SetLabel), rather than one name
	/// applied to the whole list. Built the same self-built-GameObject way as ChaosContextMenu
	/// (confirmed to actually render correctly in-game).
	/// </summary>
	internal static class ChaosPerSlotAircraftLabel
	{
		private static readonly ConditionalWeakTable<UIInventorySlot, Text> _labels =
			new ConditionalWeakTable<UIInventorySlot, Text>();

		// White: item belongs to the currently selected base/aircraft's own storage. Light blue:
		// item is equipable (reachable) but actually lives at a different location (a docked
		// neighbor, or the global pool). Dim red: unreachable, grayed-out, non-interactive - an
		// extra cue on top of the slot's own Incompatible visual state (see EventHandlersEnabled
		// usage elsewhere).
		public static readonly Color OwnLocationColor = Color.white;
		public static readonly Color OtherReachableColor = new Color(0.55f, 0.78f, 1f);
		public static readonly Color UnreachableColor = new Color(1f, 0.55f, 0.55f);

		public static void SetLabel(UIInventorySlot slot, string text, Color color, object font)
		{
			if (slot == null)
			{
				return;
			}
			Text label = GetOrCreateLabel(slot, font);
			bool show = !string.IsNullOrEmpty(text);
			label.gameObject.SetActive(show);
			if (show)
			{
				label.text = text;
				label.color = color;
			}
		}

		public static void HideAll(UIInventoryList list)
		{
			if (list == null)
			{
				return;
			}
			foreach (UIInventorySlot slot in list.Slots)
			{
				if (slot == null)
				{
					continue;
				}
				Text existing;
				if (_labels.TryGetValue(slot, out existing) && existing != null)
				{
					existing.gameObject.SetActive(false);
				}
			}
		}

		private static Text GetOrCreateLabel(UIInventorySlot slot, object font)
		{
			Text existing;
			if (_labels.TryGetValue(slot, out existing))
			{
				if (existing != null)
				{
					return existing;
				}
				_labels.Remove(slot);
			}

			GameObject labelGO = new GameObject("ChaosSlotAircraftLabel", typeof(RectTransform), typeof(Text));
			labelGO.transform.SetParent(slot.transform, false);
			RectTransform rect = (RectTransform)labelGO.transform;
			rect.anchorMin = new Vector2(0f, 0f);
			rect.anchorMax = new Vector2(1f, 0f);
			rect.pivot = new Vector2(0.5f, 0f);
			rect.anchoredPosition = new Vector2(0f, 0f);
			rect.sizeDelta = new Vector2(0f, 28f);

			Text label = labelGO.GetComponent<Text>();
			ChaosContextMenu.SetFont(label, font);
			label.color = Color.white;
			label.fontSize = 20;
			label.raycastTarget = false;
			ChaosContextMenu.SetCenterAlignment(label);

			_labels.Add(slot, label);
			return label;
		}
	}

	/// <summary>
	/// Read-only slot grid for the Air Force (Vehicle Roster) tab, showing the selected aircraft's
	/// claimed GENERAL items (ChaosVehicleAssignment - ammo/medkits/weapons/armor, the same GeoItem
	/// data shown in the Edit Soldier/Edit Vehicle screens) alongside the existing but structurally
	/// unrelated weapon/hull/module equipment grid on this screen. Built the same
	/// clone-a-real-UIInventorySlot-prefab technique this mod already used (and confirmed correctly
	/// renders even with ParentList == null - verified directly in the
	/// UIInventorySlot.SetItem this round, not just from historical notes) for the v15/v16
	/// ChaosUnreachableItemsDisplay - reused here purely for its rendering, none of that feature's
	/// reachability/architecture is involved. Fully inert: EventHandlersEnabled=false plus
	/// raycastTarget=false on every child Graphic, so nothing here can ever be dragged, dropped, or
	/// mistaken for a real interactive slot.
	/// </summary>
	internal static class ChaosAircraftItemsDisplay
	{
		// v47 originally overlaid clones directly INTO realStorageList.transform (as extra
		// siblings among the real UIVehicleEquipmentInventorySlots), copying each target empty
		// slot's own RectTransform values. Confirmed via an in-game screenshot this was wrong:
		// that list's cell positions are actually driven by a Layout Group keyed on SIBLING
		// ORDER, not by each child's own anchoredPosition/sizeDelta (those get silently
		// overwritten by the layout pass) - so inserting new siblings interleaved among the real
		// ones shifted every subsequent real slot's assigned cell, producing a checkerboard of
		// [original, injected, original, injected...] across the whole grid instead of filling
		// the actual empty cells. Fixed by never becoming a sibling INSIDE that list at all:
		// clones are parented under the list's own PARENT instead (confirmed safe - v46/v47's
		// short-lived "panel below the grid" already added children there without upsetting
		// anything), and each one's world position/size is copied from its target empty slot's
		// already-laid-out WORLD rect (via GetWorldCorners + Transform.InverseTransformPoint -
		// RectTransformUtility isn't available in this mod's ModSDK references, same constraint
		// noted throughout this mod's history), so the real list's own layout group never sees
		// or rearranges our clones at all.
		// A ScrollRect/RectMask2D/GridLayoutGroup-based scrollable redesign was tried and reverted
		// in v62/v63 because cards overflowed past the top of the STORAGE box. Root cause: Content
		// was not top-anchored, so its first row appeared above the viewport instead of flush with
		// it. Fixed in v64 via BuildScrollableGrid: Content uses anchorMin/Max.y=1 + pivot.y=1 +
		// anchoredPosition=(0,0), which pins the content's top to the viewport's top, letting
		// GridLayoutGroup grow rows downward from there. Scroll is now mouse-wheel driven and
		// shows all claimed items without any truncation cap.
		private static readonly List<GameObject> _slots = new List<GameObject>();

		// Cached from the last successful Show() call so the new "Aircraft Cargo Inventory"
		// filter button (and the 3 existing vanilla filter buttons, which change which real
		// cells are empty) can trigger a Refresh() without needing their own click handlers to
		// separately track which vehicle/module is currently active. Also read by the right-click
		// context menu (TryShowCargoItemContextMenu) so it doesn't need its own separate tracking.
		private static UIModuleVehicleEquip _lastVehicleEquipModule;
		private static UIModuleSoldierEquip _lastSoldierEquipModule;
		private static GeoVehicle _lastVehicle;
		private static object _lastFont;

		// The cargo filter button's own count-display label (see AddCargoButtonBadge) - kept live
		// here rather than only inside FilterTextLabel, so the button itself always shows the
		// currently selected aircraft's total regardless of whether the cargo filter is the
		// active one.
		private static Text _cargoButtonCountLabel;

		public static void RegisterCargoButtonCountLabel(Text label)
		{
			_cargoButtonCountLabel = label;
		}

		// Only this newly added 4th filter tells this mod "the player specifically wants to see
		// cargo now" - per explicit user feedback that showing cargo cards under All/Weapons/
		// Modules too was wrong ("displayed everywhere"). Toggled by AddCargoFilterButton's click
		// listeners, reset to false whenever the screen is left (see Close()) to match vanilla's
		// own "always reset to All" behavior on screen entry.
		private static bool _cargoFilterActive;

		public static UIModuleVehicleEquip LastVehicleEquipModule => _lastVehicleEquipModule;
		public static GeoVehicle LastVehicle => _lastVehicle;
		public static object LastFont => _lastFont;

		// The cargo filter button itself (registered once by AddCargoFilterButton) - tracked so the
		// right-click suppression patch can recognize "the hit landed on the filter button" the
		// same way it recognizes an owned card slot, for the "unload all" menu.
		private static Transform _cargoButtonTransform;
		private static PhoenixGeneralButton _cargoButton;

		public static void RegisterCargoButton(Transform buttonTransform)
		{
			_cargoButtonTransform = buttonTransform;
			_cargoButton = buttonTransform.GetComponent<PhoenixGeneralButton>();
		}

		public static bool IsCargoButton(Transform hit)
		{
			return hit != null && _cargoButtonTransform != null && hit.IsChildOf(_cargoButtonTransform);
		}

		/// Selects the cargo filter by default whenever the Air Force tab is (re-)opened, per
		/// explicit user request, overriding vanilla's own Init(InputController) - which
		/// unconditionally selects and clicks AllTypeFilterButton right before this runs (confirmed
		/// in the source: "this.AllTypeFilterButton.TabbingControl.SelectedButton =
		/// this.AllTypeFilterButton; this.AllTypeFilterButton.BaseButton.onClick.Invoke();"). Runs as
		/// a Postfix on that same method, so it always has the final say on which button ends up
		/// selected/clicked for this screen-open. No-ops (leaving vanilla's own "All" default in
		/// place) if the cargo button was never successfully added (e.g. AddCargoFilterButton bailed
		/// on a missing dependency).
		public static void SelectCargoFilterByDefault()
		{
			if (_cargoButton == null || _cargoButton.TabbingControl == null || _cargoButton.BaseButton == null)
			{
				return;
			}
			_cargoButton.TabbingControl.SelectedButton = _cargoButton;
			_cargoButton.BaseButton.onClick.Invoke();
		}

		/// Total ManufacturePrice across every resource type - used purely as a sort key for
		/// "most valuable first", not shown anywhere. Defensive against a null ManufacturePrice
		/// (shouldn't happen per its getter always lazily constructing one, but this mod doesn't
		/// control that code) - treated as 0 (sorts last) rather than throwing.
		/// Internal (not private) so ChaosInventoryPatchUtl.BuildAllEntries can reuse the same
		/// value metric for the soldier/vehicle equip screens' Storage list sort, rather than
		/// duplicating this logic a second time.
		internal static float GetItemValue(ItemDef itemDef)
		{
			ResourcePack price = itemDef?.ManufacturePrice;
			return price != null ? price.Values.Sum(ru => ru.Value) : 0f;
		}

		private static void ClearSlots()
		{
			foreach (GameObject go in _slots)
			{
				if (go != null)
				{
					UnityEngine.Object.Destroy(go);
				}
			}
			_slots.Clear();
		}

		public static void Close()
		{
			ClearSlots();
			_lastVehicleEquipModule = null;
			_lastSoldierEquipModule = null;
			_lastVehicle = null;
			_lastFont = null;
			_cargoFilterActive = false;
			ChaosItemNameTooltip.Close();
		}

		/// Flips whether cargo cards should be showing at all, then immediately re-renders against
		/// the last known vehicle/module - used by all 4 filter buttons (the new cargo one passes
		/// true, the 3 vanilla ones pass false) so switching category always leaves the display in
		/// the correct state instead of relying on each button to separately reason about it.
		public static void SetCargoFilterActive(bool active)
		{
			_cargoFilterActive = active;
			Refresh();
		}

		/// Re-runs Show() with whatever parameters were passed to it last time - used by the
		/// filter buttons (switching category changes which real cells are empty, so our cards'
		/// positions/visibility need recomputing) instead of each button tracking its own state.
		public static void Refresh()
		{
			if (_lastVehicle == null)
			{
				return;
			}
			Show(_lastVehicleEquipModule, _lastSoldierEquipModule, _lastVehicle, _lastFont);
		}

		/// True if the given hit transform belongs to (or is a child of) one of this mod's own
		/// cargo card slots - used both to keep the right-click "Cancel" suppression scoped to
		/// only our own cards (leaving every other right-click on this screen untouched) and by
		/// TryShowCargoItemContextMenu to find which slot was actually clicked.
		public static bool IsOwnedSlot(Transform hit)
		{
			if (hit == null)
			{
				return false;
			}
			foreach (GameObject go in _slots)
			{
				if (go != null && hit.IsChildOf(go.transform))
				{
					return true;
				}
			}
			return false;
		}

		public static void Show(UIModuleVehicleEquip vehicleEquipModule, UIModuleSoldierEquip soldierEquipModuleForPrefab, GeoVehicle vehicle, object font)
		{
			ClearSlots();
			if (vehicle == null)
			{
				Log.Debug("ChaosAircraftItemsDisplay.Show: no vehicle selected, nothing to show.");
				return;
			}

			UIInventorySlot prefab = soldierEquipModuleForPrefab?.StorageList?.ItemSlotPrefab;
			UIVehicleEquipmentInventoryList realStorageList = vehicleEquipModule?.StorageList;
			Transform listParent = realStorageList != null ? realStorageList.transform.parent : null;
			if (prefab == null || realStorageList == null || listParent == null)
			{
				Log.Debug($"ChaosAircraftItemsDisplay.Show: '{vehicle.Name}' - missing dependency (prefab={(prefab != null)}, realStorageList={(realStorageList != null)}, listParent={(listParent != null)}), nothing shown.");
				return;
			}

			// Remembered even if we end up showing nothing this call (e.g. no claims, or the
			// cargo filter isn't the active one), so a later Refresh()/SetCargoFilterActive() call
			// still knows which vehicle/module to re-check.
			_lastVehicleEquipModule = vehicleEquipModule;
			_lastSoldierEquipModule = soldierEquipModuleForPrefab;
			_lastVehicle = vehicle;
			_lastFont = font;

			// Sorted most-valuable-first, per explicit user request. Phoenix Point has no single
			// "gold value" field on ItemDef - ManufacturePrice (a ResourcePack: materials/tech/etc.
			// needed to build it) is the closest existing concept of "how valuable is this item",
			// so its total across every resource type is used as the sort key.
			List<KeyValuePair<ItemDef, int>> claims = ChaosVehicleAssignment.GetAll(vehicle)
				.OrderByDescending(kv => GetItemValue(kv.Key))
				.ToList();
			int totalUnits = claims.Sum(kv => kv.Value);

			// The filter BUTTON's own count label is kept live regardless of whether the cargo
			// filter is the currently active one, per explicit user request ("instead of icon show
			// count of items") - it's a property of the selected aircraft, not of which filter tab
			// happens to be open.
			if (_cargoButtonCountLabel != null)
			{
				_cargoButtonCountLabel.text = totalUnits.ToString();
			}

			if (!_cargoFilterActive)
			{
				Log.Debug($"ChaosAircraftItemsDisplay.Show: '{vehicle.Name}' - cargo filter not active, not showing cards (cached for later).");
				return;
			}

			// Per explicit user request ("rename Aircraft Cargo to 'Cargo: {aircraftItemsCount}'"),
			// kept in sync with the live claim total here (not set once at button-click time) so it
			// stays correct across vehicle cycling and this mod's own unload actions too.
			if (vehicleEquipModule.FilterTextLabel != null)
			{
				vehicleEquipModule.FilterTextLabel.text = ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_CARGO_FILTER_LABEL", totalUnits);
			}

			if (claims.Count == 0)
			{
				Log.Debug($"ChaosAircraftItemsDisplay.Show: '{vehicle.Name}' has no claimed general items, nothing shown.");
				return;
			}

			BuildScrollableGrid(realStorageList, prefab, listParent, vehicle, claims, font);
		}

		// Builds a ScrollRect over the exact slot-cell area of realStorageList, populated with one
		// UIInventorySlot clone per claimed item type. All claims are shown without truncation;
		// mouse-wheel scroll (and a visible scrollbar) reveals rows that overflow the visible height.
		// Positioned from actual slot[0]/slot[last-col] world corners rather than the list's bounding
		// rect, so cards align exactly with the underlying real slot cells.
		// The ScrollRect container (scrollGO) is added to _slots so ClearSlots() destroys everything.
		private static void BuildScrollableGrid(
			UIVehicleEquipmentInventoryList realStorageList,
			UIInventorySlot prefab,
			Transform listParent,
			GeoVehicle vehicle,
			List<KeyValuePair<ItemDef, int>> claims,
			object font)
		{
			List<UIVehicleEquipmentInventorySlot> slotList = realStorageList.Slots;
			if (slotList == null || slotList.Count == 0)
			{
				Log.Debug("BuildScrollableGrid: no real slots to measure from, aborting.");
				return;
			}

			// --- Cell size from slot[0] ---
			RectTransform s0rt = slotList[0]?.transform as RectTransform;
			if (s0rt == null)
			{
				Log.Debug("BuildScrollableGrid: slot[0] has no RectTransform, aborting.");
				return;
			}
			Vector3[] slot0C = new Vector3[4];
			s0rt.GetWorldCorners(slot0C); // 0=BL, 1=TL, 2=TR, 3=BR

			Vector3 localS0BL = listParent.InverseTransformPoint(slot0C[0]);
			Vector3 localS0TL = listParent.InverseTransformPoint(slot0C[1]);
			Vector3 localS0TR = listParent.InverseTransformPoint(slot0C[2]);
			float cellW = localS0TR.x - localS0TL.x;
			float cellH = localS0TL.y - localS0BL.y;
			if (cellW <= 0f || cellH <= 0f)
			{
				Log.Debug("BuildScrollableGrid: could not measure cell size, aborting.");
				return;
			}

			// Column count from list rect width / cell width (list rect used only for this ratio).
			RectTransform listRect = realStorageList.transform as RectTransform;
			float gridListLocalW = 0f;
			if (listRect != null)
			{
				Vector3[] lc = new Vector3[4];
				listRect.GetWorldCorners(lc);
				gridListLocalW = Mathf.Abs(
					listParent.InverseTransformPoint(lc[2]).x -
					listParent.InverseTransformPoint(lc[0]).x);
			}
			int numCols = Mathf.Max(1, Mathf.RoundToInt(gridListLocalW / cellW));

			// --- X/Y spacing between adjacent slots ---
			float spacingX = 0f, spacingY = 0f;
			if (slotList.Count > 1)
			{
				RectTransform s1rt = slotList[1]?.transform as RectTransform;
				if (s1rt != null)
				{
					Vector3[] c = new Vector3[4];
					s1rt.GetWorldCorners(c);
					spacingX = Mathf.Max(0f, listParent.InverseTransformPoint(c[1]).x - localS0TR.x);
				}
			}
			if (slotList.Count > numCols)
			{
				RectTransform snrt = slotList[numCols]?.transform as RectTransform;
				if (snrt != null)
				{
					Vector3[] c = new Vector3[4];
					snrt.GetWorldCorners(c);
					spacingY = Mathf.Max(0f, localS0BL.y - listParent.InverseTransformPoint(c[1]).y);
				}
			}

			// --- Slot area bounds from actual first/last-column/last-row slots ---
			int numRows     = Mathf.CeilToInt((float)slotList.Count / numCols);
			int lastColIdx  = Mathf.Min(numCols - 1, slotList.Count - 1);
			int lastRowIdx  = Mathf.Min((numRows - 1) * numCols, slotList.Count - 1);

			RectTransform slotLastColRT = slotList[lastColIdx]?.transform as RectTransform;
			Vector3[] slotLastColC = new Vector3[4];
			if (slotLastColRT != null) slotLastColRT.GetWorldCorners(slotLastColC);
			else slot0C.CopyTo(slotLastColC, 0);

			RectTransform slotLastRowRT = slotList[lastRowIdx]?.transform as RectTransform;
			Vector3[] slotLastRowC = new Vector3[4];
			if (slotLastRowRT != null) slotLastRowRT.GetWorldCorners(slotLastRowC);
			else slot0C.CopyTo(slotLastRowC, 0);

			Vector3 localLastColTR  = listParent.InverseTransformPoint(slotLastColC[2]);
			Vector3 localLastRowBL  = listParent.InverseTransformPoint(slotLastRowC[0]);
			float slotAreaLocalW = localLastColTR.x - localS0TL.x;
			float slotAreaLocalH = localS0TL.y    - localLastRowBL.y;
			if (slotAreaLocalW <= 0f || slotAreaLocalH <= 0f)
			{
				Log.Debug("BuildScrollableGrid: slot area has zero local size, aborting.");
				return;
			}

			// --- Build hierarchy ---
			const float scrollbarW = 8f;

			// scrollGO: left edge = slot[0] TL world x, vertical center derived from slot height.
			// pivot=(0,0.5): position.x is the LEFT edge, position.y is the vertical center.
			float worldLeftX   = slot0C[1].x;
			float worldCenterY = slot0C[1].y - slotAreaLocalH * listParent.lossyScale.y / 2f;

			GameObject scrollGO = new GameObject("ChaosCargoScrollView", typeof(RectTransform));
			scrollGO.transform.SetParent(listParent, false);
			RectTransform scrollRT = (RectTransform)scrollGO.transform;
			scrollRT.pivot     = new Vector2(0f, 0.5f);
			scrollRT.sizeDelta = new Vector2(slotAreaLocalW + scrollbarW, slotAreaLocalH);
			scrollRT.position  = new Vector3(worldLeftX, worldCenterY, slot0C[1].z);

			// Transparent bg so mouse-wheel events hit the ScrollRect on empty areas between cards.
			Image scrollBg = scrollGO.AddComponent<Image>();
			scrollBg.color         = new Color(0f, 0f, 0f, 0f);
			scrollBg.raycastTarget = true;

			ScrollRect sr = scrollGO.AddComponent<ScrollRect>();
			sr.horizontal        = false;
			sr.vertical          = true;
			sr.movementType      = ScrollRect.MovementType.Clamped;
			sr.scrollSensitivity = 60f;
			sr.inertia           = false;

			// Viewport: fills scrollGO minus the right scrollbarW column, clips content.
			GameObject viewportGO = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
			viewportGO.transform.SetParent(scrollRT, false);
			RectTransform viewportRT = (RectTransform)viewportGO.transform;
			viewportRT.anchorMin = Vector2.zero;
			viewportRT.anchorMax = Vector2.one;
			viewportRT.offsetMin = Vector2.zero;
			viewportRT.offsetMax = new Vector2(-scrollbarW, 0f);

			// Content: TOP-anchored so first card lands exactly on slot[0].
			GameObject contentGO = new GameObject("Content", typeof(RectTransform));
			contentGO.transform.SetParent(viewportRT, false);
			RectTransform contentRT = (RectTransform)contentGO.transform;
			contentRT.anchorMin        = new Vector2(0f, 1f);
			contentRT.anchorMax        = new Vector2(1f, 1f);
			contentRT.pivot            = new Vector2(0.5f, 1f);
			contentRT.anchoredPosition = Vector2.zero;
			contentRT.sizeDelta        = new Vector2(0f, 0f);

			GridLayoutGroup grid = contentGO.AddComponent<GridLayoutGroup>();
			grid.cellSize        = new Vector2(cellW, cellH);
			grid.spacing         = new Vector2(spacingX, spacingY);
			grid.startCorner     = GridLayoutGroup.Corner.UpperLeft;
			grid.startAxis       = GridLayoutGroup.Axis.Horizontal;
			grid.constraint      = GridLayoutGroup.Constraint.FixedColumnCount;
			grid.constraintCount = numCols;

			ContentSizeFitter fitter = contentGO.AddComponent<ContentSizeFitter>();
			fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
			fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

			// Scrollbar: right strip of scrollGO, sibling of viewportGO (not inside it,
			// so RectMask2D doesn't clip it).
			GameObject scrollbarGO = new GameObject("Scrollbar", typeof(RectTransform));
			scrollbarGO.transform.SetParent(scrollRT, false);
			RectTransform scrollbarRT = (RectTransform)scrollbarGO.transform;
			scrollbarRT.anchorMin = new Vector2(1f, 0f);
			scrollbarRT.anchorMax = new Vector2(1f, 1f);
			scrollbarRT.pivot     = new Vector2(1f, 0.5f);
			scrollbarRT.offsetMin = new Vector2(-scrollbarW, 0f);
			scrollbarRT.offsetMax = new Vector2(0f, 0f);

			Image sbBg = scrollbarGO.AddComponent<Image>();
			sbBg.color         = new Color(0.1f, 0.1f, 0.1f, 0.85f);
			sbBg.raycastTarget = false;

			GameObject slidingAreaGO = new GameObject("SlidingArea", typeof(RectTransform));
			slidingAreaGO.transform.SetParent(scrollbarRT, false);
			RectTransform slidingAreaRT = (RectTransform)slidingAreaGO.transform;
			slidingAreaRT.anchorMin = Vector2.zero;
			slidingAreaRT.anchorMax = Vector2.one;
			slidingAreaRT.offsetMin = Vector2.zero;
			slidingAreaRT.offsetMax = Vector2.zero;

			GameObject handleGO = new GameObject("Handle", typeof(RectTransform));
			handleGO.transform.SetParent(slidingAreaRT, false);
			RectTransform handleRT = (RectTransform)handleGO.transform;
			handleRT.anchorMin = Vector2.zero;
			handleRT.anchorMax = new Vector2(1f, 0.2f);
			handleRT.offsetMin = Vector2.zero;
			handleRT.offsetMax = Vector2.zero;

			Image handleImg = handleGO.AddComponent<Image>();
			handleImg.color         = new Color(0.6f, 0.6f, 0.6f, 0.9f);
			handleImg.raycastTarget = true;

			Scrollbar scrollbar = scrollbarGO.AddComponent<Scrollbar>();
			scrollbar.direction     = Scrollbar.Direction.BottomToTop;
			scrollbar.value         = 1f;
			scrollbar.targetGraphic = handleImg;
			scrollbar.handleRect    = handleRT;

			sr.viewport                    = viewportRT;
			sr.content                     = contentRT;
			sr.verticalScrollbar           = scrollbar;
			sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

			_slots.Add(scrollGO);

			for (int i = 0; i < claims.Count; i++)
			{
				KeyValuePair<ItemDef, int> claim = claims[i];
				UIInventorySlot slot = UnityEngine.Object.Instantiate(prefab, contentRT, false);
				slot.name = "ChaosAircraftItemSlot " + i;
				GeoItem cargoItem = new GeoItem(claim.Key, claim.Value, -1, null, -100);
				slot.Item = cargoItem;
				slot.EventHandlersEnabled = false;

				// Always-visible item name under the icon, per explicit user request ("also on bottom
				// center position also show in text item name") - reuses ChaosPerSlotAircraftLabel's
				// already-proven bottom-center-anchored Text (confirmed rendering correctly in-game on
				// the real Storage list) rather than building yet another one-off Text from scratch.
				// Replaces the mouse-over ChaosItemNameTooltip this screen used to also show - removed
				// per explicit user request, this always-visible label already covers the same info.
				string itemDisplayName = cargoItem.CommonItemData.GetDisplayName() ?? "";
				ChaosPerSlotAircraftLabel.SetLabel(slot, itemDisplayName, ChaosPerSlotAircraftLabel.OwnLocationColor, font);

				UIInventorySlot capturedSlot = slot;
				GeoVehicle capturedVehicle = vehicle;
				ChaosDoubleClickHandler dblClick = slot.gameObject.AddComponent<ChaosDoubleClickHandler>();
				dblClick.OnDoubleClick = () => ChaosInventoryPatchUtl.ShowCargoItemContextMenuForSlot(capturedVehicle, capturedSlot);
			}

			Log.Debug($"BuildScrollableGrid: '{vehicle.Name}' - {claims.Count} item(s), {numCols} col(s), cell=({cellW:F1}x{cellH:F1}), spacing=({spacingX:F1}x{spacingY:F1}), slotArea=({slotAreaLocalW:F1}x{slotAreaLocalH:F1}).");
		}
	}

	/// <summary>
	/// Shows each aircraft's total claimed cargo units right next to the roster row's EXISTING
	/// crew-count text, rather than building an entirely separate display - per the user's own
	/// suggestion, after two earlier attempts (a top-left icon badge, v52-v56; a cloned 4th
	/// equipment slot, v55-v56) ran into rendering issues (invisible text, wrong sort order, wrong
	/// scale, a stale count on the wrong row, a squeezed/clipped width) that never fully resolved.
	/// v57 baked the count directly into VehicleCrewCount.text ("4/6" -> "4/6 (12)"); v58 tried
	/// tinting just that portion light blue via an inline &lt;color&gt; tag, still reported hard to
	/// read. Now reverted to plain crew-count text, with the "(count)" shown in a SEPARATE small
	/// Text on a dark semi-transparent background Image instead, positioned immediately after the
	/// crew count via Text.preferredWidth (an exact measurement of "however wide THIS aircraft's
	/// own crew-count text actually is," not a guessed fixed offset) - both elements are plain
	/// siblings of VehicleCrewCount itself (not nested inside anything scaled or layout-managed),
	/// which is what made every earlier attempt fragile.
	/// </summary>
	internal static class ChaosVehicleCrewCountSuffix
	{
		// Background Image and label Text are separate GameObjects, both siblings of VehicleCrewCount
		// (not the label as a child of the Image). Keeping them at the same hierarchy level as the
		// already-rendering VehicleCrewCount guarantees the label renders: a newly-created Text with
		// font copied via reflection has repeatedly failed to render when nested as a child of a
		// non-Text parent (Image), while a Text at the same sibling level as VehicleCrewCount
		// inherits the same Canvas, scale, and mask context that makes the crew count itself visible.
		private static readonly ConditionalWeakTable<GeoVehicleRosterSlot, GameObject> _badges =
			new ConditionalWeakTable<GeoVehicleRosterSlot, GameObject>();
		private static readonly ConditionalWeakTable<GeoVehicleRosterSlot, GameObject> _labels =
			new ConditionalWeakTable<GeoVehicleRosterSlot, GameObject>();

		public static void UpdateSuffix(GeoVehicleRosterSlot slot)
		{
			VehicleDisplayData vehicleDisplay = slot?.Vehicle;
			GeoVehicle vehicle = vehicleDisplay?.GetBaseObject<GeoVehicle>();
			if (vehicle == null || slot.VehicleCrewCount == null)
			{
				return;
			}

			int total = ChaosVehicleAssignment.GetAll(vehicle).Sum(kv => kv.Value);
			slot.VehicleCrewCount.text = vehicleDisplay.AircraftInfoData.CurrentCrew + "/" + vehicleDisplay.AircraftInfoData.MaxCrew;

			bool show = total > 0;
			GameObject badge = GetOrCreateBadge(slot);
			GameObject labelGO = GetOrCreateLabel(slot);
			badge.SetActive(show);
			labelGO.SetActive(show);

			if (show)
			{
				Text label = labelGO.GetComponent<Text>();
				if (label != null)
				{
					label.text = ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_CARGO_COUNT_BADGE", total);
				}
				RepositionElements(slot, badge, labelGO);
				Log.Debug($"ChaosVehicleCrewCountSuffix.UpdateSuffix: '{vehicle.Name}' - total={total}, shown={show}, "
					+ $"labelText='{label?.text}', labelColor={label?.color}, labelFontSize={label?.fontSize}.");
			}
			else
			{
				Log.Debug($"ChaosVehicleCrewCountSuffix.UpdateSuffix: '{vehicle.Name}' - total={total}, shown={show}.");
			}
		}

		/// Re-checks every currently instantiated roster row - this mod's own unload/transfer
		/// actions can change a DIFFERENT aircraft's claim count than whichever row vanilla itself
		/// last refreshed.
		public static void RefreshAll()
		{
			foreach (GeoVehicleRosterSlot slot in UnityEngine.Object.FindObjectsOfType<GeoVehicleRosterSlot>())
			{
				UpdateSuffix(slot);
			}
		}

		private static GameObject GetOrCreateBadge(GeoVehicleRosterSlot slot)
		{
			GameObject existing;
			if (_badges.TryGetValue(slot, out existing))
			{
				if (existing != null) return existing;
				_badges.Remove(slot);
			}

			GameObject badgeGO = new GameObject("ChaosCrewCountCargoBadge", typeof(RectTransform), typeof(Image));
			badgeGO.transform.SetParent(slot.VehicleCrewCount.transform.parent, false);
			((RectTransform)badgeGO.transform).pivot = new Vector2(0f, 0.5f);
			Image bg = badgeGO.GetComponent<Image>();
			bg.sprite = null;
			bg.color = new Color(0f, 0f, 0f, 0.6f);
			bg.raycastTarget = false;

			_badges.Add(slot, badgeGO);
			return badgeGO;
		}

		private static GameObject GetOrCreateLabel(GeoVehicleRosterSlot slot)
		{
			GameObject existing;
			if (_labels.TryGetValue(slot, out existing))
			{
				if (existing != null) return existing;
				_labels.Remove(slot);
			}

			// Clone VehicleCrewCount itself rather than building a new Text from scratch. A clone is
			// guaranteed to render because the original already does — same font, same canvas, same
			// scale, same everything. Every attempt to create a fresh Text and copy the font via
			// reflection has produced an invisible label (Image sibling renders fine; child Text does
			// not), and this is the established fix for that class of problem elsewhere in this mod
			// (e.g. UIInventorySlot cloning in ChaosAircraftItemsDisplay).
			GameObject clone = UnityEngine.Object.Instantiate(slot.VehicleCrewCount.gameObject);
			clone.name = "ChaosCrewCountCargoLabel";
			clone.transform.SetParent(slot.VehicleCrewCount.transform.parent, false);
			((RectTransform)clone.transform).pivot = new Vector2(0f, 0.5f);

			Text label = clone.GetComponent<Text>();
			if (label != null)
			{
				label.text = "";
				label.color = Color.white;
				label.raycastTarget = false;
			}

			_labels.Add(slot, clone);
			return clone;
		}

		private static void RepositionElements(GeoVehicleRosterSlot slot, GameObject badge, GameObject labelGO)
		{
			RectTransform crewRect = slot.VehicleCrewCount.transform as RectTransform;
			RectTransform badgeRect = badge.transform as RectTransform;
			RectTransform labelRect = labelGO.transform as RectTransform;
			Text label = labelGO.GetComponent<Text>();
			if (crewRect == null || badgeRect == null || labelRect == null || label == null)
			{
				return;
			}

			float baseWidth = slot.VehicleCrewCount.preferredWidth;
			float elemWidth = Mathf.Max(label.preferredWidth + 10f, 40f);
			float elemHeight = crewRect.rect.height > 0f ? crewRect.rect.height : 20f;
			float scale = crewRect.lossyScale.x;

			Vector3[] corners = new Vector3[4];
			crewRect.GetWorldCorners(corners); // 0=bottom-left, 1=top-left
			float centerY = (corners[0].y + corners[1].y) / 2f;
			float startX = corners[0].x + (baseWidth + 6f) * scale;

			// Both badge (background Image) and label (Text) sit at the same world position/size.
			// Badge is added first so it renders behind the label (earlier sibling = drawn first).
			badgeRect.sizeDelta = new Vector2(elemWidth, elemHeight);
			badgeRect.position = new Vector3(startX, centerY, 0f);
			labelRect.sizeDelta = new Vector2(elemWidth, elemHeight);
			labelRect.position = new Vector3(startX, centerY, 0f);
		}
	}

	[HarmonyPatch(typeof(GeoVehicleRosterSlot), "InitSlot")]
	internal static class Patch_GeoVehicleRosterSlot_InitSlot
	{
		private static void Postfix(GeoVehicleRosterSlot __instance)
		{
			ChaosVehicleCrewCountSuffix.UpdateSuffix(__instance);
		}
	}

	[HarmonyPatch(typeof(GeoVehicleRosterSlot), "UpdateVehicleEquipments")]
	internal static class Patch_GeoVehicleRosterSlot_UpdateVehicleEquipments
	{
		private static void Postfix(GeoVehicleRosterSlot __instance)
		{
			ChaosVehicleCrewCountSuffix.UpdateSuffix(__instance);
		}
	}

	// v15's ChaosUnreachableItemsDisplay (rendering unreachable items as cards structurally
	// separate from the real UIInventoryList, so nothing outside this mod could act on them) was
	// reverted here - it introduced two confirmed regressions (the right-click menu breaking
	// entirely, and reachable docked-neighbor items disappearing from view altogether) for a
	// benefit (hiding foreign items from vanilla reload / a third-party loadout mod) that wasn't
	// worth that cost. Back to the v10-v14 single-list design: every item, reachable or not, is
	// fed into the real StorageList, with EventHandlersEnabled gating interaction per slot - see
	// ApplySlotTags below.

	// Fixes the actual, confirmed cause of reported stacked-item loss during ordinary
	// drag-and-drop (traced by reading UIModuleSoldierEquip.AttemptSlotSwap and
	// UIInventoryList.RemoveItem in the source, not guessed): AttemptSlotSwap
	// truncates a dragged item with Count > 1 down to a fresh Count=1 GeoItem whenever the
	// DESTINATION list has AllowStacking=false - which this mod's own StorageList deliberately
	// is, precisely so same-ItemDef items from different real locations never silently merge into
	// one indistinguishable slot (see the v10 notes below). It then calls
	// sourceList.RemoveItem(thatCount1Item, sourceSlot) against the SOURCE slot - and
	// RemoveItem's own "if (this.AllowStacking) ... else { slot.Item = null; }" branch means that
	// whenever the SOURCE list ALSO has AllowStacking=false (true for this mod's own Storage list
	// itself, e.g. rearranging within the merged view), removing "1 unit" wipes the WHOLE slot
	// unconditionally, regardless of the real count actually being removed - so of a stack of 6,
	// only 1 lands in the destination and the other 5 are wiped from the source with nowhere to
	// go. Fixed narrowly: temporarily flip AllowStacking to true for the single RemoveItem call
	// only when this exact mismatch is detected (list is normally non-stacking, but the slot's
	// real count exceeds what's actually being removed) - vanilla's OWN "if (AllowStacking)"
	// branch then does the correct thing (CommonItemData.Subtract, leaving the remainder intact)
	// using its own already-tested code, rather than this mod reimplementing removal itself. Not
	// scoped to only this mod's own list: a mismatch between a slot's real count and the amount
	// being removed on a non-stacking list is not a state normal vanilla lists (single-equip slots
	// always hold Count=1) should ever legitimately produce, so this should be safe everywhere
	// UIInventoryList is used, not just this screen.
	[HarmonyPatch(typeof(UIInventoryList), "RemoveItem")]
	internal static class Patch_UIInventoryList_RemoveItem_PartialStackFix
	{
		// v22's original fix only checked the CALLER-supplied `slot` parameter - but confirmed via
		// the source (UIInventorySlotSideButton.OnSideButtonPressed, the equipped
		// weapon's "LoadAmmo" side-button icon - a completely separate call site from the
		// drag-and-drop path v22 was written against) that RemoveItem is ALSO called with
		// `slot = null` deliberately, and its OWN body then resolves a real slot internally
		// (`this.Slots.Where(sl => !sl.Empty && sl.Item.ItemDef == removedItem.ItemDef).FirstOrDefault()`)
		// before falling through to the exact same unconditional "AllowStacking == false -> wipe the
		// whole slot" branch v22 already fixed - just reached via a path our Prefix's `slot != null`
		// condition couldn't see, since the mismatch only exists AFTER vanilla's own internal
		// resolution, not in the parameter we're handed. This is precisely what let the "LoadAmmo"
		// side button consume an entire multi-unit Storage stack in one click instead of
		// just the single unit it actually loaded into the weapon (confirmed by direct user report
		// and Player.log: a Count=2 combined magazine stack collapsed to Count=0 from one reload,
		// even though the weapon only ever received one magazine's worth of charges).
		//
		// Fixed by replicating that same internal resolution here, so the mismatch is detected
		// regardless of whether the caller passed an explicit slot or left it null for vanilla to
		// resolve.
		private static void Prefix(UIInventoryList __instance, ICommonItem removedItem, UIInventorySlot slot, out bool __state)
		{
			__state = false;
			if (__instance.AllowStacking || removedItem == null)
			{
				return;
			}
			UIInventorySlot resolvedSlot = slot ?? __instance.Slots.FirstOrDefault(sl => !sl.Empty && sl.Item.ItemDef == removedItem.ItemDef);
			if (resolvedSlot != null && resolvedSlot.Item != null && resolvedSlot.Item != removedItem
				&& resolvedSlot.Item.CommonItemData.Count > removedItem.CommonItemData.Count)
			{
				// Diagnostic (per direct user report of "Devastation Kaos Cannon" +Ammo apparently
				// loading a weapon for free while leaving the real Storage stack untouched): if
				// removedItem isn't reference-equal to the slot's own Item, it's disconnected from real
				// storage - possibly a stale ghost from UnfilteredItems/Slots desyncing (the class of
				// bug this mod has hit many times before, e.g. v25-v42/v76-v81). Logging identity
				// hashes and list-membership directly instead of guessing further which object vanilla's
				// own OnSideButtonPressed/TryLoadAmmo actually picked.
				int removedHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(removedItem);
				int slotItemHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(resolvedSlot.Item);
				bool removedInUnfiltered = __instance.UnfilteredItems.Any(i => ReferenceEquals(i, removedItem));
				bool slotItemInUnfiltered = __instance.UnfilteredItems.Any(i => ReferenceEquals(i, resolvedSlot.Item));
				int matchingUnfilteredCount = __instance.UnfilteredItems.Count(i => i.ItemDef == removedItem.ItemDef);
				Log.Debug($"RemoveItem PartialStackFix: '{resolvedSlot.Item.CommonItemData.GetDisplayName()}' slot holds x{resolvedSlot.Item.CommonItemData.Count} (hash={slotItemHash}, inUnfilteredItems={slotItemInUnfiltered}) but only x{removedItem.CommonItemData.Count} is being removed (hash={removedHash}, inUnfilteredItems={removedInUnfiltered}, slot param was {(slot == null ? "null, resolved internally" : "explicit")}) - {matchingUnfilteredCount} total UnfilteredItems entries share this ItemDef - temporarily allowing stacking so vanilla's own Subtract logic runs instead of wiping the whole slot.");
				// Reasoning about the exact call site from the source alone hasn't settled
				// why removedItem's Count reads as low as 0 here (GetSingleItem() should always hand
				// back either the real Count=1 item or a freshly built Count=1 stand-in - never 0) -
				// capturing the real call stack directly instead of guessing further which method
				// actually produced this call.
				Log.Debug($"RemoveItem PartialStackFix call stack:\n{Environment.StackTrace}");
				__instance.AllowStacking = true;
				__state = true;
			}
		}

		private static void Postfix(UIInventoryList __instance, bool __state)
		{
			if (__state)
			{
				__instance.AllowStacking = false;
			}
		}
	}

	// Purely diagnostic - traces vanilla's own ammoItem.Count/CurrentCharges immediately before and
	// after TryLoadItemWithItem (the reload-button mechanism, called from ReloadItemHandler ->
	// TryLoadAmmo -> here) - so a Player.log capture shows definitively how much vanilla itself
	// actually consumed from the Storage stack, independent of anything this mod's own
	// reconciliation does afterward. Added after a user report that reloading a weapon from a
	// 2-unit magazine stack left the weapon at 12/12 (matching one magazine) but consumed the ENTIRE
	// stack (both units) from Storage - which hand-tracing the source predicted should
	// NOT happen (the loop should stop after the first single-unit load once the weapon reports
	// full, leaving the second unit behind) - needs direct confirmation of what's actually true at
	// runtime instead of another hand-derivation.
	[HarmonyPatch(typeof(UIInventoryList), "TryLoadItemWithItem")]
	internal static class Patch_UIInventoryList_TryLoadItemWithItem_Diagnostic
	{
		private static void Prefix(ICommonItem item, ICommonItem ammoItem, UIInventorySlot ammoSlot)
		{
			Log.Debug($"TryLoadItemWithItem.Prefix: item='{item?.CommonItemData?.GetDisplayName()}' itemAmmoCharges={item?.CommonItemData?.Ammo?.CurrentCharges} itemChargesMax={item?.ItemDef?.ChargesMax} | ammoItem='{ammoItem?.CommonItemData?.GetDisplayName()}' hash={(ammoItem != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ammoItem) : 0)} count={ammoItem?.CommonItemData?.Count} charges={ammoItem?.CommonItemData?.CurrentCharges} | ammoSlot={(ammoSlot != null ? "present" : "null")}.");
		}

		private static void Postfix(ICommonItem item, ICommonItem ammoItem, UIInventorySlot ammoSlot, bool __result)
		{
			Log.Debug($"TryLoadItemWithItem.Postfix: result={__result} | item='{item?.CommonItemData?.GetDisplayName()}' itemAmmoCharges={item?.CommonItemData?.Ammo?.CurrentCharges} | ammoItem='{ammoItem?.CommonItemData?.GetDisplayName()}' hash={(ammoItem != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ammoItem) : 0)} count={ammoItem?.CommonItemData?.Count} charges={ammoItem?.CommonItemData?.CurrentCharges} isEmpty={ammoItem?.CommonItemData?.IsEmpty()} | ammoSlot.Item={(ammoSlot != null ? (ammoSlot.Item != null ? "present" : "NULL") : "n/a")}.");
		}
	}

	[HarmonyPatch(typeof(UIInventoryList), "TryLoadAmmo")]
	internal static class Patch_UIInventoryList_TryLoadAmmo_Diagnostic
	{
		private static void Prefix(ICommonItem item, UIInventorySlot itemSlot, UIInventoryList sourceList)
		{
			Log.Debug($"TryLoadAmmo.Prefix: item='{item?.CommonItemData?.GetDisplayName()}' hasAmmo={item?.CommonItemData?.Ammo != null} currentCharges={item?.CommonItemData?.Ammo?.CurrentCharges} chargesMax={item?.ItemDef?.ChargesMax} sourceList={(sourceList != null ? "present" : "null")} sourceListAllowStacking={sourceList?.AllowStacking}.");
		}

		private static void Postfix(ICommonItem item)
		{
			Log.Debug($"TryLoadAmmo.Postfix: item='{item?.CommonItemData?.GetDisplayName()}' finalCurrentCharges={item?.CommonItemData?.Ammo?.CurrentCharges}.");
		}
	}

	// TryLoadAmmo (the reload-button mechanism, and vanilla's own auto-reload-on-equip) draws ammo
	// from ANY item currently sitting in the Storage list's Slots/UnfilteredItems, with no regard
	// for this mod's own reachability gating - a red-tagged (EventHandlersEnabled = false, e.g. an
	// item sitting in a different, non-docked aircraft) item is just as fair game to it as a
	// genuinely reachable white/blue one, confirmed by direct user report. This mod's own click/
	// drag handling already respects that gate (UIInventorySlot.CanAcceptItem folds in
	// !EventHandlersEnabled), but TryLoadAmmo bypasses slot interaction entirely and reads the
	// underlying data directly.
	//
	// Fixed by temporarily hiding every unreachable item - from both its Slot and from
	// UnfilteredItems, since TryLoadAmmo's own two-loop fallback (Slots first, then
	// UnfilteredItems) checks both - for the exact duration of the vanilla call, then restoring
	// everything immediately after in the Postfix. This makes vanilla's own selection loops simply
	// never see an unreachable item in the first place, without this mod needing to reimplement any
	// of TryLoadAmmo's own picking logic.
	// v86 sixth follow-up: TryLoadAmmo's OWN guard (`if (!sourceList.AllowStacking) return;`) bails
	// out entirely whenever the ammo is sitting in a non-stacking source list - which this mod's own
	// Storage list always is (forced AllowStacking=false since v10, deliberately, to keep same-
	// ItemDef stacks from different real locations from silently merging in the merged display).
	// Confirmed by direct user report/comparison: with this mod disabled, equipping a weapon
	// straight from Storage auto-loads compatible ammo immediately (true vanilla behavior); with
	// this mod enabled, the weapon comes out empty every time, because this guard silently blocks
	// vanilla's own auto-reload-on-equip the instant sourceList is this mod's Storage list. The
	// user then has to reach for the manual +Ammo button to get the weapon loaded at all - which is
	// what led to the separate, now-understood "TFTV MarketplaceAmmoFallback" interaction traced in
	// this file's history above; that mod's fallback firing at all was itself a symptom of THIS gap,
	// not an unrelated coincidence.
	internal class TryLoadAmmoState
	{
		public List<KeyValuePair<UIInventorySlot, ICommonItem>> Hidden = new List<KeyValuePair<UIInventorySlot, ICommonItem>>();
		public bool FlippedAllowStacking;
	}

	[HarmonyPatch(typeof(UIInventoryList), "TryLoadAmmo")]
	internal static class Patch_UIInventoryList_TryLoadAmmo_RespectReachability
	{
		private static void Prefix(UIInventoryList sourceList, out TryLoadAmmoState __state)
		{
			__state = new TryLoadAmmoState();
			if (sourceList == null)
			{
				return;
			}
			foreach (UIInventorySlot slot in sourceList.Slots)
			{
				if (!slot.EventHandlersEnabled && slot.Item != null)
				{
					__state.Hidden.Add(new KeyValuePair<UIInventorySlot, ICommonItem>(slot, slot.Item));
					sourceList.UnfilteredItems.Remove(slot.Item);
					slot.Item = null;
				}
			}
			// Temporarily allow stacking for the exact duration of this call only, so vanilla's own
			// auto-reload logic isn't blocked purely because this mod forces the Storage list
			// non-stacking for an unrelated (display) reason. Any partial-stack removal this enables
			// (e.g. via TryLoadItemWithItem's own internal RemoveItem call) still goes through
			// vanilla's own correct AllowStacking==true Subtract-based branch, not this mod's own
			// PartialStackFix workaround - genuinely safer than leaving it non-stacking here.
			if (!sourceList.AllowStacking)
			{
				sourceList.AllowStacking = true;
				__state.FlippedAllowStacking = true;
			}
		}

		private static void Postfix(UIInventoryList sourceList, TryLoadAmmoState __state)
		{
			if (sourceList == null || __state == null)
			{
				return;
			}
			foreach (KeyValuePair<UIInventorySlot, ICommonItem> hidden in __state.Hidden)
			{
				hidden.Key.Item = hidden.Value;
				if (!sourceList.UnfilteredItems.Contains(hidden.Value))
				{
					sourceList.UnfilteredItems.Add(hidden.Value);
				}
			}
			if (__state.FlippedAllowStacking)
			{
				sourceList.AllowStacking = false;
			}
		}
	}

	// The soldier/vehicle equip screen's own "+Ammo"/"+Item" side-button icon (the small icon that
	// sits directly on an equipped slot, offering to pull a compatible item straight out of
	// Storage for free when one's already sitting there - a DIFFERENT mechanism from
	// ReloadItemHandler/TryLoadAmmo above) has the exact same reachability gap: its
	// "ActionNeededFree" case reads `_parentModule.StorageList.UnfilteredItems.Where(item =>
	// item.ItemDef == _itemToProduce).FirstOrDefault()` directly, with no regard for
	// EventHandlersEnabled - confirmed by direct user report that clicking it pulled a medkit out
	// of red (unreachable) storage.
	//
	// Fixed the same way as TryLoadAmmo above, but with an extra safety check first: vanilla's own
	// code here has NO null-guard on that FirstOrDefault() call - if this mod simply hid every
	// unreachable match the way the TryLoadAmmo fix does, and it turned out NOTHING reachable was
	// left, vanilla would immediately NullReferenceException on the very next line
	// (`.GetSingleItem()`). So this only hides the unreachable matches (letting vanilla correctly
	// fall through to a genuinely reachable one) when a reachable match is CONFIRMED to exist first;
	// if the ONLY match(es) are unreachable, the whole action is skipped instead (the click does
	// nothing), rather than risking vanilla crashing or silently using the wrong item.
	[HarmonyPatch(typeof(UIInventorySlotSideButton), "OnSideButtonPressed")]
	internal static class Patch_UIInventorySlotSideButton_OnSideButtonPressed_RespectReachability
	{
		// v90 fix - root cause found directly in Player.log, confirmed against the source:
		// the "silently does nothing" repro was this Prefix's OWN "return false" (block the click)
		// path. Vanilla's real OnSideButtonPressed() body only ever calls
		// `_parentModule.RefreshSideButtons()` - the ONE thing that re-runs GetState()/RefreshState()
		// and corrects a stale `_currentState` field - at the very END of the method, after actually
		// performing the action. Every early-return branch (Hidden/Unaffordable/NeedsTime/
		// Impossible/NoStorage) skips it - and so does returning `false` from THIS Prefix, since that
		// skips vanilla's whole method body outright. Player.log showed the exact sequence: click 1
		// manufactures successfully (state was Affordable) and vanilla's own manufacture-and-equip
		// flow immediately consumes/auto-loads the fresh item elsewhre ("Updating preferred inventory
		// loadout of ... added: '...'"), leaving nothing reachable - but the button's cached
		// `_currentState` still reads the stale "ActionNeededFree" from the refresh right after
		// purchase, and every following click re-reads that same stale field, finds nothing
		// reachable, and gets silently blocked by the `return false` below with NO GetState.Postfix
		// ever firing again - confirmed directly, zero such log lines appear between the first block
		// and the screen being closed. Only closing/reopening the screen (a full, unrelated refresh
		// path) ever calls GetState() again.
		//
		// Fixed by no longer blocking blindly: when the cached state disagrees with reality, this
		// forces a fresh RefreshState() call on the SAME button right here - re-entering GetState(),
		// whose own Postfix (below in this file) already recomputes the correct fallback state
		// (Affordable/Unaffordable/NeedsTime/Impossible) whenever nothing reachable exists - then
		// re-reads the corrected LIVE field value (the `____currentState` parameter is a one-time
		// snapshot taken at Prefix entry, so it can't reflect this correction). If the corrected
		// state is still ActionNeededFree (nothing should legitimately land back here after
		// GetState()'s own fix, but kept as a safety net), fall back to the old silent block rather
		// than risk vanilla grabbing a red item; for any other corrected state, let vanilla's own
		// method body run normally - it reads `_currentState` as a live field, not a snapshot, so it
		// sees this same correction and does exactly the right thing (manufacture, or no-op for
		// Impossible/Unaffordable/NeedsTime) on THIS click, not just the next one.
		private static readonly FieldInfo CurrentStateField =
			typeof(UIInventorySlotSideButton).GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);

		private static bool Prefix(UIInventorySlotSideButton __instance, UIModuleSoldierEquip ____parentModule, ItemDef ____itemToProduce, UIInventorySlotSideButton.GeneralState ____currentState, out List<KeyValuePair<UIInventorySlot, ICommonItem>> __state)
		{
			__state = null;
			if (____currentState.State != UIInventorySlotSideButton.SideButtonState.ActionNeededFree
				|| ____parentModule?.StorageList == null || ____itemToProduce == null)
			{
				return true;
			}
			UIInventoryList storageList = ____parentModule.StorageList;
			bool reachableExists = storageList.Slots.Any(sl => !sl.Empty && sl.Item != null
				&& sl.Item.ItemDef == ____itemToProduce && sl.EventHandlersEnabled);
			if (!reachableExists)
			{
				Log.Debug($"OnSideButtonPressed: cached state says '{____itemToProduce.name}' is free but nothing REACHABLE actually exists - forcing a fresh RefreshState() instead of silently blocking (the stale-cache bug).");
				__instance.RefreshState();
				UIInventorySlotSideButton.GeneralState corrected = ____currentState;
				object rawCorrected = CurrentStateField?.GetValue(__instance);
				if (rawCorrected is UIInventorySlotSideButton.GeneralState correctedState)
				{
					corrected = correctedState;
				}
				Log.Debug($"OnSideButtonPressed: after RefreshState(), '{____itemToProduce.name}' corrected state={corrected.State} action={corrected.Action}.");
				if (corrected.State == UIInventorySlotSideButton.SideButtonState.ActionNeededFree)
				{
					Log.Debug($"OnSideButtonPressed: still ActionNeededFree after a fresh recompute for '{____itemToProduce.name}' with nothing reachable - blocking as a safety net.");
					return false;
				}
				// Corrected to a real, live state (Affordable/Unaffordable/NeedsTime/Impossible/
				// Hidden) - let vanilla's own body act on it now, on this same click, instead of
				// requiring one more (still-blocked) click first.
				return true;
			}

			// Diagnostic (per direct user report that +Ammo on "Devastator" loaded the weapon "for
			// free" without visibly touching the real Storage stack): vanilla's own ActionNeededFree
			// branch picks its item via `UnfilteredItems.Where(...).FirstOrDefault()`, a DIFFERENT list
			// than the `Slots`-based reachability check just above - if these two lists have drifted
			// apart (the class of bug this mod has hit many times before, e.g. v25-v42/v76-v81), vanilla
			// could grab a stale/disconnected object instead of the real reachable stack. Logging both
			// lists' matching entries (identity hash + count) here, right before vanilla's own pick, so
			// the next Player.log capture shows directly whether they agree.
			var unfilteredMatches = storageList.UnfilteredItems.Where(i => i.ItemDef == ____itemToProduce)
				.Select(i => $"[hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(i)} count={i.CommonItemData.Count}]");
			var slotMatches = storageList.Slots.Where(sl => !sl.Empty && sl.Item != null && sl.Item.ItemDef == ____itemToProduce)
				.Select(sl => $"[hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(sl.Item)} count={sl.Item.CommonItemData.Count} reachable={sl.EventHandlersEnabled}]");
			Log.Debug($"OnSideButtonPressed: '{____itemToProduce.name}' UnfilteredItems matches: {string.Join(", ", unfilteredMatches)} | Slots matches: {string.Join(", ", slotMatches)}.");

			__state = new List<KeyValuePair<UIInventorySlot, ICommonItem>>();
			foreach (UIInventorySlot slot in storageList.Slots)
			{
				if (!slot.EventHandlersEnabled && slot.Item != null && slot.Item.ItemDef == ____itemToProduce)
				{
					__state.Add(new KeyValuePair<UIInventorySlot, ICommonItem>(slot, slot.Item));
					storageList.UnfilteredItems.Remove(slot.Item);
					slot.Item = null;
				}
			}
			return true;
		}

		private static void Postfix(UIModuleSoldierEquip ____parentModule, List<KeyValuePair<UIInventorySlot, ICommonItem>> __state)
		{
			if (__state == null || ____parentModule?.StorageList == null)
			{
				return;
			}
			foreach (KeyValuePair<UIInventorySlot, ICommonItem> hidden in __state)
			{
				hidden.Key.Item = hidden.Value;
				if (!____parentModule.StorageList.UnfilteredItems.Contains(hidden.Value))
				{
					____parentModule.StorageList.UnfilteredItems.Add(hidden.Value);
				}
			}
		}
	}

	// Follow-up to the patch above, per direct user report: blocking the click outright (the
	// Prefix above) correctly stopped equipping from red storage, but also broke the LEGITIMATE
	// fallback vanilla itself has for exactly this situation - originally, when NOTHING of this
	// ItemDef exists in Storage at all, GetState() falls through past the "ActionNeededFree" check
	// into the normal manufacture-eligibility checks (CanManufacture/cost/time/wallet), showing a
	// price and manufacturing a fresh one. But GetState()'s own "does one already exist" check
	// (`_parentModule.StorageList.UnfilteredItems.Where(item => item.ItemDef ==
	// _itemToProduce).FirstOrDefault() != null`) - same as OnSideButtonPressed's - never looks at
	// EventHandlersEnabled either, so when the ONLY existing match is red/unreachable, vanilla still
	// computes State = ActionNeededFree (as if a free one were genuinely available) instead of
	// falling through to manufacture - and the Prefix above then correctly refuses to actually hand
	// over the red item, but has no way to retroactively fix a state that was already computed
	// wrong, so the click just goes inert instead of manufacturing.
	//
	// Fixed at the actual source: a Postfix on the private GetState() itself. When vanilla computes
	// ActionNeededFree but no REACHABLE match exists, this replicates vanilla's own subsequent
	// checks (the exact same CanManufacture/ManufacturePointsCost/Wallet.HasResources sequence from
	// GetState()'s own body, just re-run here since GetState() already returned past that point) to
	// recompute the correct fallback state - Impossible/NeedsTime/Unaffordable/Affordable - so the
	// button displays and behaves exactly as if the red item didn't exist at all, matching true
	// vanilla behavior for "nothing usable in Storage." Only .State is touched; .Action (already
	// correctly set earlier in GetState() to LoadAmmo/AddAmmo/AddItem) is untouched.
	[HarmonyPatch(typeof(UIInventorySlotSideButton), "GetState")]
	internal static class Patch_UIInventorySlotSideButton_GetState_RespectReachability
	{
		private static void Postfix(UIModuleSoldierEquip ____parentModule, ItemDef ____itemToProduce, ref UIInventorySlotSideButton.GeneralState __result)
		{
			if (__result.State != UIInventorySlotSideButton.SideButtonState.ActionNeededFree
				|| ____parentModule?.StorageList == null || ____itemToProduce == null)
			{
				return;
			}
			UIInventoryList storageList = ____parentModule.StorageList;
			bool reachableExists = storageList.Slots.Any(sl => !sl.Empty && sl.Item != null
				&& sl.Item.ItemDef == ____itemToProduce && sl.EventHandlersEnabled);
			if (reachableExists)
			{
				return;
			}

			Log.Debug($"GetState: only red/unreachable '{____itemToProduce.name}' exists - falling through to manufacture eligibility instead of ActionNeededFree.");
			if (!____parentModule.CanManufacture(____itemToProduce))
			{
				__result.State = UIInventorySlotSideButton.SideButtonState.ActionNeededImpossible;
				return;
			}
			if (____itemToProduce.ManufacturePointsCost > 0f)
			{
				__result.State = UIInventorySlotSideButton.SideButtonState.ActionNeededNeedsTime;
				return;
			}
			if (____parentModule.ModuleData?.Wallet == null || !____parentModule.ModuleData.Wallet.HasResources(____itemToProduce.ManufacturePrice))
			{
				__result.State = UIInventorySlotSideButton.SideButtonState.ActionNeededUnaffordable;
				return;
			}
			__result.State = UIInventorySlotSideButton.SideButtonState.ActionNeededAffordable;
		}
	}


	// v25-v32 tried tracking split-off stack fragments into UnfilteredItems via a Harmony patch on
	// AddItem (Patch_UIInventoryList_AddItem_TrackSplitFragment), since AttemptSlotSwap's
	// truncation (see the fix above) creates a brand-new GeoItem that AddItem's own
	// "slot.Item = item;" never adds to that list. That approach was abandoned after Player.log
	// proved it was itself introducing a genuine duplicate reference into UnfilteredItems (the
	// same object counted twice, confirmed via identity hash) through a mechanism never fully
	// pinned down despite extensive tracing - rather than keep patching the patch, TryCustomUpdateStorage
	// below now reads Slots directly instead of UnfilteredItems, which sidesteps the whole problem:
	// a slot can only ever display one item at a time, so there is no scattered list to keep in
	// sync and no way for the same slot to contribute a duplicate.

	// Drops the vehicle's claims when the aircraft itself is destroyed/dismantled. Under the
	// metadata-overlay design this is no longer a data-loss event: the real items were always
	// physically sitting in the faction's own Global Inventory, so dropping the claim just means
	// they stop being counted as "this aircraft's cargo" and show up as ordinary untagged Global
	// Inventory instead - a deliberate behavior change from the old design, where destroying a
	// vehicle destroyed its entire separate real ItemStorage along with it.
	[HarmonyPatch(typeof(GeoVehicle), "Destroy")]
	internal static class Patch_GeoVehicle_Destroy
	{
		private static void Postfix(GeoVehicle __instance)
		{
			ChaosVehicleAssignment.Remove(__instance);
		}
	}

	// New feature (v90), per explicit user request: mission-reward items ("ITEMS RECOVERED" on the
	// post-mission screen) previously always landed as plain, untagged Global Inventory - the user
	// wanted them credited to a player aircraft sitting at the mission's site instead, picking one
	// at random if more than one is docked there. `GeoFactionReward.Apply` is vanilla's own single
	// entry point for granting a mission's reward (called with the mission's own relatedSite/
	// relatedVehicle in scope) - its private `GiveItems` step (unchanged, still writes every
	// recovered item into the SAME real `faction.ItemStorage` this mod's own claims already overlay)
	// already runs by the time this Postfix fires, so all that's needed is layering a claim on top -
	// exactly the same "pure metadata overlay, no physical item ever moves" model this mod's entire
	// v38+ architecture is built on.
	//
	// Two cases, in priority order: (1) `relatedVehicle` - the exact aircraft that actually carried
	// the squad on this mission (also used by vanilla itself for aircraft damage/soldier injuries a
	// few lines above `GiveItems`'s own call in `Apply`) is the most accurate, least ambiguous choice
	// whenever it's available - no randomness needed, the loot goes straight into the plane that was
	// actually there. (2) Falls back to a random pick among every player-owned `GeoVehicle` docked at
	// `relatedSite` (the exact "if multiple then select randomly... under the site" behavior the user
	// asked for) for reward contexts that only carry a site, not a specific vehicle (e.g. some
	// non-mission encounters/objectives). If neither yields a candidate, items are left exactly as
	// before - plain, unclaimed Global Inventory.
	[HarmonyPatch(typeof(GeoFactionReward), "Apply")]
	internal static class Patch_GeoFactionReward_Apply_AssignLootToDockedAircraft
	{
		private static void Postfix(GeoFaction faction, GeoSite relatedSite, GeoVehicle relatedVehicle, GeoFactionRewardApplyResult __result)
		{
			if (faction == null || __result?.Items == null || __result.Items.IsEmpty)
			{
				return;
			}

			GeoVehicle target = (relatedVehicle != null && relatedVehicle.Owner == faction) ? relatedVehicle : null;
			if (target == null && relatedSite != null)
			{
				List<GeoVehicle> docked = relatedSite.Vehicles.Where(v => v.Owner == faction).ToList();
				if (docked.Count > 0)
				{
					target = docked[UnityEngine.Random.Range(0, docked.Count)];
				}
			}
			if (target == null)
			{
				return;
			}

			foreach (KeyValuePair<ItemDef, GeoItem> kv in __result.Items.Items)
			{
				ChaosVehicleAssignment.Adjust(target, kv.Key, kv.Value.CommonItemData.Count);
			}
			Log.Debug($"GeoFactionReward.Apply: claimed {__result.Items.Items.Count} recovered item type(s) for aircraft '{target.Name}' (vehicle #{target.VehicleID}), relatedVehicle={(relatedVehicle != null)}, relatedSite='{relatedSite?.SiteName?.Localize(null)}'.");
		}
	}

	// New in the metadata-overlay redesign: vehicle-claimed quantity now physically lives in the
	// same real GeoFaction.ItemStorage as ordinary Global Inventory, so without this patch it would
	// count against the faction's manufacturing capacity check - contradicting "aircraft storage is
	// unlimited." Patches GetStorageUsed() itself (rather than each capacity-comparison call site
	// individually) so every consumer of it is automatically covered: confirmed via
	// source this includes ItemDef.CanManufacture/CanFinishManufacture (the two hard gates),
	// GeoPhoenixFaction.EvaluateStorageState (drives the "Storage is full!" log message),
	// GeoFactionReward (mission/diplomacy reward item overflow), and UIModuleInfoBar's geoscape
	// "used/total" readout - all five would otherwise be thrown off by vehicle-claimed quantity.
	[HarmonyPatch(typeof(ItemStorage), "GetStorageUsed")]
	internal static class Patch_ItemStorage_GetStorageUsed_ExcludeVehicleClaims
	{
		private static void Postfix(ItemStorage __instance, ref int __result)
		{
			int excluded = ChaosVehicleAssignment.GetTaggedStorageUsedForStorage(__instance);
			if (excluded > 0)
			{
				__result = Math.Max(0, __result - excluded);
			}
		}
	}

	// GeoPhoenixFaction.TrimStorage() -> ItemStorage.TrimStorageToCapacity(capacity) randomly
	// evicts real items down to capacity whenever real usage exceeds it (confirmed via
	// source; called from GeoPhoenixBase.UpdateStats() whenever base storage-facility stats
	// change). Since vehicle-claimed quantity now physically lives in the same real storage this
	// operates on, it is no longer structurally exempt the way it was when vehicle storage was a
	// wholly separate ItemStorage object this method never touched at all. Fully reimplements the
	// trim loop (widening just the capacity parameter would only make eviction of claimed items
	// rarer, not impossible, since GetRandomElement still picks blind to any tag) restricted to the
	// untagged remainder per ItemDef only - claimed quantity can never be randomly evicted, no
	// matter how far over "true" capacity the pool nominally runs.
	[HarmonyPatch(typeof(ItemStorage), "TrimStorageToCapacity")]
	internal static class Patch_ItemStorage_TrimStorageToCapacity_ProtectVehicleClaims
	{
		private static bool Prefix(ItemStorage __instance, int capacity, ref IEnumerable<GeoItem> __result)
		{
			int protectedAmount = ChaosVehicleAssignment.GetTaggedStorageUsedForStorage(__instance);
			if (protectedAmount <= 0)
			{
				return true;
			}

			int used = __instance.GetStorageUsed();
			ItemStorage removed = new ItemStorage();
			int guard = 0;
			while (used > capacity && guard++ < 100000)
			{
				List<ItemDef> candidates = __instance.Items.Keys
					.Where(def => UntaggedRemainder(__instance, def) > 0)
					.ToList();
				if (candidates.Count == 0)
				{
					Log.Debug("TrimStorageToCapacity: no untagged remainder left to evict from - stopping without touching vehicle-claimed quantity.");
					break;
				}
				ItemDef pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
				GeoItem single = (GeoItem)__instance.Items[pick].GetSingleItem();
				removed.AddItem(single);
				ChaosInventoryPatchUtl.RemoveFromStorage(__instance, single);
				used -= pick.StorageUsed;
			}
			__result = removed.Items.Values;
			return false;
		}

		private static int UntaggedRemainder(ItemStorage storage, ItemDef itemDef)
		{
			GeoItem existing;
			int real = storage.Items.TryGetValue(itemDef, out existing) ? existing.CommonItemData.Count : 0;
			int tagged = ChaosVehicleAssignment.GetTaggedCountForStorage(storage, itemDef);
			return Math.Max(0, real - tagged);
		}
	}

	// Shows a combined read-only cargo summary (this aircraft + docked base + every other aircraft
	// docked at the same base, each clearly labeled) in the Vehicle Roster tab.
	// AircraftInfoController.AircraftDescription is a Text field that vanilla always calls SetInfo
	// with description=null (confirmed - every caller of UpdateAircraftInfoController passes null),
	// so it's genuinely blank space in the existing, always-visible aircraft info panel
	// (name/art/HP/armor/speed/range). This is the vehicle roster's own equipment/weapons/modules
	// screen (UIVehicleEquipmentInventoryList, a different item type than GeoItem) - it cannot render
	// this mod's soldier-item storage as an interactive list, so this is read-only text, not drag/drop.
	[HarmonyPatch(typeof(UIModuleVehicleCycle), "UpdateAircraftInfoController")]
	internal static class Patch_UIModuleVehicleCycle_UpdateAircraftInfoController
	{
		private static void Postfix(UIModuleVehicleCycle __instance)
		{
			ChaosInventoryPatchUtl.ShowAircraftCargoSummary(__instance);
		}
	}

	// Adds a new, separate read-only slot grid to the Air Force (Vehicle Roster) tab showing the
	// selected aircraft's claimed GENERAL items (ammo/medkits/weapons/armor via
	// ChaosVehicleAssignment) - distinct from the existing interactive weapon/hull/module
	// equipment grid on this same screen, which is built for a completely different item type
	// (GeoVehicleEquipmentUIData) and structurally cannot hold GeoItem. OnVehicleCycled is the
	// same private method vanilla itself uses to refresh the equipment grid on selection change,
	// so patching it here rebuilds this mod's own display at exactly the right time too.
	[HarmonyPatch(typeof(UIStateVehicleRoster), "OnVehicleCycled")]
	internal static class Patch_UIStateVehicleRoster_OnVehicleCycled
	{
		private static void Postfix(UIStateVehicleRoster __instance, VehicleDisplayData newVehicle)
		{
			ChaosInventoryPatchUtl.ShowAircraftItemsDisplay(__instance, newVehicle);
		}
	}

	[HarmonyPatch(typeof(UIStateVehicleRoster), "ExitState")]
	internal static class Patch_UIStateVehicleRoster_ExitState
	{
		private static void Postfix()
		{
			ChaosAircraftItemsDisplay.Close();
		}
	}

	/// Shows the total claimed cargo unit count above the aircraft name in the geoscape top-bar
	/// vehicle icon (UIModuleVehicleSelection.AircraftIDText). Only shows when count > 0.
	///
	/// First attempt baked "(N)" as a second line directly into AircraftIDText.text
	/// ("(N)\n&lt;name&gt;"), relying on verticalOverflow=Overflow to avoid clipping. Confirmed
	/// in-game to fail completely: any '\n' in that Text's string made the WHOLE label disappear
	/// (not just get clipped), even with overflow unclipped - most likely this Text has
	/// resizeTextForBestFit enabled and a fixed single-line-height RectTransform, so adding a
	/// second line forced Unity's best-fit sizing to shrink the font down to (near-)nothing rather
	/// than actually overflow. Rather than guess further at that one shared Text field - the same
	/// class of dead end this mod hit repeatedly with the Air Force tab's icon-corner badge
	/// (v52-56) before abandoning it - this reuses the approach that DID end up working there
	/// (ChaosVehicleCrewCountSuffix): a separate Text, cloned from the original label itself
	/// (guarantees identical font/canvas/scale/mask context, since a from-scratch Text with a
	/// reflected-in font has repeatedly failed to render elsewhere in this mod), added as a plain
	/// sibling and repositioned via world position rather than edited in place. CONFIRMED WORKING
	/// in-game in this form (centered above the name, "(N)" with parentheses).
	///
	/// A follow-up attempt to reposition this to top-left instead of centered (dropping the
	/// parentheses, targeting corners instead of center, then - after that still rendered
	/// centered - escaping the clone to the grandparent transform on the theory that a Layout
	/// Group on the immediate parent was fighting the manual position) made things strictly
	/// worse (label rendered far outside the icon entirely), and was reverted back to this
	/// exact confirmed-good form per explicit user request rather than continue guessing blind
	/// at a live-only rendering problem. If top-left positioning is wanted again, it needs an
	/// actual look at the live scene hierarchy (or much more targeted Player.log diagnostics)
	/// rather than another blind attempt.
	internal static class ChaosGeoscapeVehicleBadge
	{
		private static UIModuleVehicleSelection _lastModule;
		private static GeoVehicle _lastVehicle;

		private static readonly ConditionalWeakTable<UIModuleVehicleSelection, GameObject> _labels =
			new ConditionalWeakTable<UIModuleVehicleSelection, GameObject>();

		public static void Update(UIModuleVehicleSelection module, GeoVehicle vehicle)
		{
			_lastModule = module;
			_lastVehicle = vehicle;
			Apply(module, vehicle);
		}

		public static void RefreshCurrent()
		{
			if (_lastModule != null && _lastVehicle != null)
			{
				Apply(_lastModule, _lastVehicle);
			}
		}

		private static void Apply(UIModuleVehicleSelection module, GeoVehicle vehicle)
		{
			if (module?.AircraftIDText == null || vehicle == null) return;

			module.AircraftIDText.text = vehicle.ToString();

			int total = ChaosVehicleAssignment.GetAll(vehicle).Sum(kv => kv.Value);
			GameObject labelGO = GetOrCreateLabel(module);
			if (labelGO == null) return;

			bool show = total > 0;
			labelGO.SetActive(show);
			if (show)
			{
				Text label = labelGO.GetComponent<Text>();
				if (label != null)
				{
					label.text = ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_CARGO_COUNT_BADGE", total);
				}
				RepositionAbove(module.AircraftIDText, labelGO);
			}
			Log.Debug($"ChaosGeoscapeVehicleBadge.Apply: '{vehicle.Name}' - total={total}, shown={show}.");
		}

		private static GameObject GetOrCreateLabel(UIModuleVehicleSelection module)
		{
			GameObject existing;
			if (_labels.TryGetValue(module, out existing))
			{
				if (existing != null) return existing;
				_labels.Remove(module);
			}

			// Clone AircraftIDText itself rather than building a new Text from scratch - the clone
			// is guaranteed to render (same font/canvas/scale/mask context as the already-visible
			// name label), matching the fix already proven for this exact problem in
			// ChaosVehicleCrewCountSuffix.
			GameObject clone = UnityEngine.Object.Instantiate(module.AircraftIDText.gameObject);
			clone.name = "ChaosGeoscapeCargoCountLabel";
			clone.transform.SetParent(module.AircraftIDText.transform.parent, false);

			Text label = clone.GetComponent<Text>();
			if (label != null)
			{
				label.text = "";
				label.raycastTarget = false;
			}

			_labels.Add(module, clone);
			return clone;
		}

		/// Places labelGO directly above nameLabel by shifting its world position up by exactly
		/// nameLabel's own world-space height - correct regardless of either Text's pivot value,
		/// since both share an identical rect size (labelGO is a clone) and shifting a rect's
		/// position by its own height always moves its near edge to sit exactly where the other
		/// rect's far edge is, independent of where the pivot point sits within that rect.
		private static void RepositionAbove(Text nameLabel, GameObject labelGO)
		{
			RectTransform nameRect = nameLabel.transform as RectTransform;
			RectTransform labelRect = labelGO.transform as RectTransform;
			if (nameRect == null || labelRect == null) return;

			float height = nameRect.rect.height * nameRect.lossyScale.y;
			Vector3 namePos = nameRect.position;
			labelRect.position = new Vector3(namePos.x, namePos.y + height, namePos.z);
		}
	}

	[HarmonyPatch(typeof(UIModuleVehicleSelection), "SetVehicleInfo")]
	internal static class Patch_UIModuleVehicleSelection_SetVehicleInfo_CargoBadge
	{
		private static void Postfix(UIModuleVehicleSelection __instance, GeoVehicle vehicle)
		{
			ChaosGeoscapeVehicleBadge.Update(__instance, vehicle);
		}
	}

	// Mirrors Patch_UIStateEditSoldier_OnInputEvent_SuppressRightClickCancel, but scoped to this
	// mod's own cargo overlay cards (ChaosAircraftItemsDisplay.IsOwnedSlot) instead of
	// UIModuleSoldierEquip's four lists - this screen has no such module of its own. Right-clicking
	// anywhere else on the Air Force tab (the real Weapon/Module/Storage grids, the vehicle roster,
	// the 3D viewport) is completely untouched and still exits the screen normally, same as before
	// this mod ever touched this screen.
	[HarmonyPatch(typeof(UIStateVehicleRoster), "OnInputEvent")]
	internal static class Patch_UIStateVehicleRoster_OnInputEvent_SuppressRightClickCancel
	{
		private static bool Prefix(InputEvent ev, ref bool __result)
		{
			if (ev.Type != InputEventType.Pressed || ev.Name != "Cancel" || !ChaosInventoryPatchUtl.IsRightMouseButtonDown())
			{
				return true;
			}

			List<RaycastResult> hits = ChaosInventoryPatchUtl.RaycastPointer();

			if (hits.Any(h => ChaosAircraftItemsDisplay.IsOwnedSlot(h.gameObject.transform)))
			{
				ChaosInventoryPatchUtl.TryShowCargoItemContextMenu(ChaosAircraftItemsDisplay.LastVehicle, hits);
				Log.Debug("OnInputEvent (vehicle roster): swallowed right-click Cancel over a cargo card.");
				__result = true;
				return false;
			}

			// Right-clicking the filter BUTTON itself (not a card) offers to unload everything at
			// once, per explicit user request - checked separately since a right-click there would
			// otherwise still exit the screen via vanilla's own Cancel handling regardless of the
			// button's own PhoenixGeneralButton/ChaosDoubleClickHandler components.
			if (hits.Any(h => ChaosAircraftItemsDisplay.IsCargoButton(h.gameObject.transform)))
			{
				ChaosInventoryPatchUtl.ShowUnloadAllMenu(ChaosAircraftItemsDisplay.LastVehicle);
				Log.Debug("OnInputEvent (vehicle roster): swallowed right-click Cancel over the cargo filter button.");
				__result = true;
				return false;
			}

			return true;
		}
	}

	// Adds a 4th filter button ("Aircraft Cargo Inventory") alongside the vanilla All/Weapons/
	// Modules ones on the Air Force tab's STORAGE panel, per explicit user request. Awake() runs
	// exactly once per UIModuleVehicleEquip instance (confirmed via source: the
	// repeated-per-screen-open reset logic lives in the separate Init(InputController) method,
	// not here), so this Postfix - and the listeners it adds to the 3 EXISTING vanilla buttons -
	// only ever runs/registers once, with no risk of duplicate listeners piling up across
	// repeated Air Force tab visits.
	[HarmonyPatch(typeof(UIModuleVehicleEquip), "Awake")]
	internal static class Patch_UIModuleVehicleEquip_Awake_AddCargoFilterButton
	{
		private static void Postfix(UIModuleVehicleEquip __instance)
		{
			ChaosInventoryPatchUtl.AddCargoFilterButton(__instance);
		}
	}

	// Vanilla's own Init(InputController) (called every time the Air Force tab is opened)
	// unconditionally selects and clicks AllTypeFilterButton as the last thing it does - per
	// explicit user request, the cargo filter (this mod's own 4th button) should be selected by
	// default instead. Running as a Postfix means this always fires right after vanilla's own
	// selection, so it has the final say.
	[HarmonyPatch(typeof(UIModuleVehicleEquip), "Init", typeof(InputController))]
	internal static class Patch_UIModuleVehicleEquip_Init_DefaultToCargoFilter
	{
		private static void Postfix()
		{
			ChaosAircraftItemsDisplay.SelectCargoFilterByDefault();
		}
	}

	// Close any leftover context menu whenever the screen is (re-)entered, the displayed character
	// changes, or the screen exits - it should never survive past the moment it was opened for.
	[HarmonyPatch(typeof(UIStateEditSoldier), "EnterState")]
	internal static class Patch_UIStateEditSoldier_EnterState
	{
		private static void Postfix()
		{
			ChaosContextMenu.Close();
			ChaosItemNameTooltip.Close();
		}
	}

	[HarmonyPatch(typeof(UIStateEditSoldier), "ExitState")]
	internal static class Patch_UIStateEditSoldier_ExitState
	{
		private static void Postfix(UIStateEditSoldier __instance)
		{
			ChaosContextMenu.Close();
			ChaosItemNameTooltip.Close();
			ChaosInventoryPatchUtl.ClearLocationState(__instance);
		}
	}

	[HarmonyPatch(typeof(UIStateEditSoldier), "CharacterChangedHandler")]
	internal static class Patch_UIStateEditSoldier_CharacterChangedHandler
	{
		// MUST be a Prefix, not a Postfix - confirmed via the source that vanilla's own
		// CharacterChangedHandler body calls this.RefreshStorage() SYNCHRONOUSLY near its own end
		// (after updating _currentCharacter to the new character), which - via our patched
		// TryCustomRefreshStorage - populates fresh, correct _lastEntries tracking for the new
		// character before CharacterChangedHandler even returns. A Postfix here ran AFTER that
		// refresh completed and immediately wiped the tracking it had just built, leaving the very
		// next commit (UpdateStorage(), deferred to a later tick via _uiRefreshNeeded) with an
		// EMPTY lastEntries - which then treats every currently-shown item, across every location,
		// as brand new and re-adds/re-claims all of it. Confirmed directly in Player.log: right
		// after "TryCustomRefreshStorage: ... 39 item entries", the next line was
		// "TryCustomUpdateStorage: ... lastEntries=0, currentlyShown=39" followed by 39 consecutive
		// "new item -> 'HELIOS 3'" lines (every single shown item, not just one) - exactly matching
		// a reported "global items displayed as if moved to aircraft" followed by items multiplying
		// (6 medkits becoming ~100 after repeated switches/commits). Moving this to a Prefix clears
		// stale state from the OLD character/screen BEFORE the handler's own body - and its
		// internal refresh for the NEW character - ever runs, so nothing freshly built gets wiped
		// afterward.
		private static void Prefix(UIStateEditSoldier __instance)
		{
			ChaosContextMenu.Close();
			ChaosItemNameTooltip.Close();
			ChaosInventoryPatchUtl.ClearLocationState(__instance);
		}
	}

	// The storage-routing patch for the two vanilla call sites that still fall back to it (when the
	// custom RefreshStorage/UpdateStorage below decline because the soldier isn't tracked by this mod
	// at all). Resolved fresh from the CURRENTLY DISPLAYED character - routed to their own aircraft's
	// or base's storage whenever they're actually there, regardless of how the screen was opened.
	[HarmonyPatch(typeof(UIStateEditSoldier), "StorageItems")]
	internal static class Patch_UIStateEditSoldier_StorageItems
	{
		private static bool Prefix(UIStateEditSoldier __instance, ref ItemStorage __result)
		{
			LocationContext ctx = ChaosInventoryPatchUtl.ResolveLocationContext(__instance);
			if (ctx == null)
			{
				return true;
			}
			__result = ctx.Storage;
			return false;
		}
	}

	// Replaces vanilla's RefreshStorage entirely (Prefix, skip original) whenever the soldier is
	// tracked (crewing an aircraft, or stationed at a base): builds a merged display covering every
	// reachable location (this aircraft/base, docked base/aircraft, and the faction-wide global pool,
	// which - per explicit user request - is always fully accessible everywhere until an item is
	// deliberately moved into a specific base/aircraft) PLUS every other, currently-unreachable
	// location, all in the same list. Unreachable items are shown but disabled
	// (UIInventorySlot.EventHandlersEnabled = false blocks all click/drag/drop AND drives the slot's
	// own existing "Incompatible" visual state - a real, already-used treatment, not invented here)
	// and tagged with their owning location via ChaosPerSlotAircraftLabel. Falls through to vanilla
	// for a soldier not tracked by this mod at all (stationed nowhere this mod knows about).
	[HarmonyPatch(typeof(UIStateEditSoldier), "RefreshStorage")]
	internal static class Patch_UIStateEditSoldier_RefreshStorage
	{
		private static bool Prefix(UIStateEditSoldier __instance)
		{
			return !ChaosInventoryPatchUtl.TryCustomRefreshStorage(__instance);
		}
	}

	// Replaces vanilla's UpdateStorage entirely (Prefix, skip original) for the same tracked-soldier
	// case: vanilla's own version diffs the Storage list against a single ItemStorage, which would be
	// wrong now that the list can show items from many different real storages at once. See
	// ChaosInventoryPatchUtl.TryCustomUpdateStorage for the reconciliation logic and its documented
	// limitations.
	[HarmonyPatch(typeof(UIStateEditSoldier), "UpdateStorage")]
	internal static class Patch_UIStateEditSoldier_UpdateStorage
	{
		private static bool Prefix(UIStateEditSoldier __instance)
		{
			return !ChaosInventoryPatchUtl.TryCustomUpdateStorage(__instance);
		}
	}

	// Also re-applies the per-slot disabled state every frame (see ReapplySlotTags):
	// UIInventorySlot.OnEnable() unconditionally resets EventHandlersEnabled = true whenever a
	// slot's GameObject is (re-)enabled, which happens on its own as the Storage list scrolls
	// (pooled/virtualized rows get disabled when scrolled out of view, re-enabled when scrolled
	// back in) - completely independent of any refresh/commit this mod triggers. Without this,
	// scrolling could silently undo the disabled state on an unreachable item while its red label
	// (a separate GameObject this mod controls directly) kept showing correctly, letting the item
	// be dragged/equipped again despite still displaying as blocked.
	[HarmonyPatch(typeof(UIStateEditSoldier), "UpdateState")]
	internal static class Patch_UIStateEditSoldier_UpdateState
	{
		private static void Postfix(UIStateEditSoldier __instance)
		{
			// Resolved once and shared, rather than each of the two callers below separately calling
			// ResolveLocationContext(__instance) - it's recomputed fresh every frame by design (see
			// ResolveLocationContextLive's own comment on why: docking state must never go stale
			// while this screen is open), so doing that resolution twice per frame was pure waste,
			// not a correctness need. See ChaosInventoryPatchUtl's FPS notes.
			LocationContext ctx = ChaosInventoryPatchUtl.ResolveLocationContext(__instance);
			ChaosInventoryPatchUtl.OverrideStorageIndicator(__instance, ctx);
			ChaosInventoryPatchUtl.ReapplySlotTags(__instance, ctx);
			ChaosInventoryPatchUtl.HideThirdPartyLoadoutButtons(__instance);
		}
	}

	// Vanilla's "Cancel" input action is bound to both right-click and Escape; OnInputEvent falls
	// through to base.SwitchToPreviousState() (back to Geoscape) whenever nothing else consumes it.
	// Right-clicking over the storage/inventory lists now does something useful instead: if the
	// clicked slot holds a reachable Storage item, a context menu offers to move it to any other
	// reachable location (the current base/aircraft's own docked neighbors, or the global pool).
	// Right-clicking anywhere else on this screen (the 3D character viewport, roster tabs, empty
	// space) is completely untouched and still exits normally via Escape or right-click, same as
	// vanilla.
	[HarmonyPatch(typeof(UIStateEditSoldier), "OnInputEvent")]
	internal static class Patch_UIStateEditSoldier_OnInputEvent_SuppressRightClickCancel
	{
		private static bool Prefix(UIStateEditSoldier __instance, InputEvent ev, ref bool __result)
		{
			// v90 parity fix: also swallow "VehicleRightMouseClickSelection" over the inventory
			// area, same as the UIStateEditVehicle patch below - see its own comment for the full
			// mechanism (UIStateVehicleSelected, still active deeper in the state stack whenever a
			// GeoVehicle sits selected underneath this screen, independently reacts to this SEPARATE
			// right-click-bound event and exits regardless of the "Cancel" handling below). Not
			// separately confirmed via Player.log for THIS screen the way it was for the vehicle one
			// - added defensively for the case a soldier is ever edited while a vehicle is selected
			// underneath (e.g. reached from the Air Force roster rather than a base roster), since
			// the check is a no-op whenever that event never actually arrives here.
			if (ev.Type != InputEventType.Pressed || !ChaosInventoryPatchUtl.IsRightMouseButtonDown()
				|| (ev.Name != "Cancel" && ev.Name != "VehicleRightMouseClickSelection"))
			{
				return true;
			}

			List<RaycastResult> hits = ChaosInventoryPatchUtl.RaycastPointer();
			if (!ChaosInventoryPatchUtl.IsOverInventoryArea(__instance, hits))
			{
				return true;
			}

			if (ev.Name == "VehicleRightMouseClickSelection")
			{
				Log.Debug("OnInputEvent: swallowed right-click VehicleRightMouseClickSelection over the inventory area.");
				__result = true;
				return false;
			}

			ChaosInventoryPatchUtl.TryShowStorageItemContextMenu(__instance, hits);
			Log.Debug("OnInputEvent: swallowed right-click Cancel over the inventory area.");
			__result = true;
			return false;
		}
	}

	// Mirrors every Patch_UIStateEditSoldier_* patch above, targeting UIStateEditVehicle - the
	// ground-vehicle-unit (tank/crawler) equip screen, confirmed via source to share
	// the exact same private method names/shapes and the same underlying UIModuleSoldierEquip
	// instance as UIStateEditSoldier. Ground-vehicle crew members are ordinary GeoCharacter
	// entries in GeoVehicle.Units just like human soldiers, so ChaosInventoryPatchUtl's
	// reconciliation logic (already generalized to GeoscapeViewState) applies unchanged - a
	// vehicle unit crewing an aircraft now gets the exact same per-aircraft isolation, merged
	// view, and right-click "Move to..." menu a soldier would. Before this, this screen was
	// entirely unpatched and could freely assign/take any item from the whole faction's storage.
	[HarmonyPatch(typeof(UIStateEditVehicle), "EnterState")]
	internal static class Patch_UIStateEditVehicle_EnterState
	{
		private static void Postfix()
		{
			ChaosContextMenu.Close();
			ChaosItemNameTooltip.Close();
		}
	}

	[HarmonyPatch(typeof(UIStateEditVehicle), "ExitState")]
	internal static class Patch_UIStateEditVehicle_ExitState
	{
		// v92 note: this method briefly carried a Prefix capturing Environment.StackTrace on every
		// call, added specifically to find the real mechanism behind "right-click on the vehicle
		// equip screen exits instead of showing the menu" (see Patch_UIStateEditVehicle_
		// OnInputEvent_SuppressRightClickCancel's own v92 notes for the confirmed root cause and
		// fix) - removed now that the investigation is closed and confirmed fixed, since a full
		// stack trace on every screen exit is the heaviest log line this mod ever produced.
		private static void Postfix(UIStateEditVehicle __instance)
		{
			ChaosContextMenu.Close();
			ChaosItemNameTooltip.Close();
			ChaosInventoryPatchUtl.ClearLocationState(__instance);
		}
	}

	[HarmonyPatch(typeof(UIStateEditVehicle), "CharacterChangedHandler")]
	internal static class Patch_UIStateEditVehicle_CharacterChangedHandler
	{
		// MUST be a Prefix, not a Postfix - same v39 lesson as
		// Patch_UIStateEditSoldier_CharacterChangedHandler: UIStateEditVehicle's own
		// CharacterChangedHandler also calls this.RefreshStorage() synchronously near its own end
		// (confirmed in the source), so a Postfix here would wipe out the tracking that
		// call had just correctly populated for the new character.
		private static void Prefix(UIStateEditVehicle __instance)
		{
			ChaosContextMenu.Close();
			ChaosItemNameTooltip.Close();
			ChaosInventoryPatchUtl.ClearLocationState(__instance);
		}
	}

	[HarmonyPatch(typeof(UIStateEditVehicle), "StorageItems")]
	internal static class Patch_UIStateEditVehicle_StorageItems
	{
		private static bool Prefix(UIStateEditVehicle __instance, ref ItemStorage __result)
		{
			LocationContext ctx = ChaosInventoryPatchUtl.ResolveLocationContext(__instance);
			if (ctx == null)
			{
				return true;
			}
			__result = ctx.Storage;
			return false;
		}
	}

	[HarmonyPatch(typeof(UIStateEditVehicle), "RefreshStorage")]
	internal static class Patch_UIStateEditVehicle_RefreshStorage
	{
		private static bool Prefix(UIStateEditVehicle __instance)
		{
			return !ChaosInventoryPatchUtl.TryCustomRefreshStorage(__instance);
		}
	}

	[HarmonyPatch(typeof(UIStateEditVehicle), "UpdateStorage")]
	internal static class Patch_UIStateEditVehicle_UpdateStorage
	{
		private static bool Prefix(UIStateEditVehicle __instance)
		{
			return !ChaosInventoryPatchUtl.TryCustomUpdateStorage(__instance);
		}
	}

	[HarmonyPatch(typeof(UIStateEditVehicle), "UpdateState")]
	internal static class Patch_UIStateEditVehicle_UpdateState
	{
		private static void Postfix(UIStateEditVehicle __instance)
		{
			// See the identical comment on Patch_UIStateEditSoldier_UpdateState's own Postfix.
			LocationContext ctx = ChaosInventoryPatchUtl.ResolveLocationContext(__instance);
			ChaosInventoryPatchUtl.OverrideStorageIndicator(__instance, ctx);
			ChaosInventoryPatchUtl.ReapplySlotTags(__instance, ctx);
			ChaosInventoryPatchUtl.HideThirdPartyLoadoutButtons(__instance);
		}
	}

	// v90 fix - ROOT CAUSE FOUND, finally, for the "vehicle screen right-click exits instead of
	// showing the menu" mystery this mod's own history (v44/v45) chased and left unresolved.
	// Player.log (with the v45-era raycast-hit diagnostic still in place) showed the menu being
	// built successfully (or the red hint shown for an unreachable item) AND the screen exiting
	// (`Entering Geoscape UI state: UIStateInitial` then `UIStateVehicleSelected`) on the SAME
	// tick, every single time, regardless of what this mod's own Prefix did - meaning this Prefix
	// blocking vanilla's "Cancel" InputEvent was never the whole story. Confirmed directly in the
	// UIStateVehicleSelected source: a ground vehicle's own crew-equip screen
	// (UIStateEditVehicle) is PUSHED on top of UIStateVehicleSelected, not a replacement of it -
	// and UIStateVehicleSelected's OnInputEvent independently handles a SEPARATE, differently-
	// named input event also bound to the right mouse button: `"VehicleRightMouseClickSelection"`
	// -> `OnRightClickSelection()`, a real geoscape-WORLD raycast/reselect
	// (`Context.View.SelectSiteAtCursor()`), entirely bypassing this screen's own "Cancel"
	// handling above (a completely different InputEvent, never touched by this mod before now).
	// Vanilla's own UIStateEditVehicle.OnInputEvent never handles this event name itself (falls
	// through to `_actorCycleModule.OnInputEvent`, which doesn't handle it either), so it keeps
	// propagating down the state stack to UIStateVehicleSelected underneath - exiting the screen
	// regardless of this mod's own "Cancel" swallow, since that only ever touched the OTHER event.
	// (The soldier equip screen has never shown this symptom because it isn't normally entered
	// while a GeoVehicle sits selected underneath it the way a ground-vehicle unit's own equip
	// screen always is.)
	[HarmonyPatch(typeof(UIStateEditVehicle), "OnInputEvent")]
	internal static class Patch_UIStateEditVehicle_OnInputEvent_SuppressRightClickCancel
	{
		// v92 fix - the ACTUAL root cause, finally confirmed via a captured Environment.StackTrace
		// (added as a v92 diagnostic on UIStateEditVehicle.ExitState), after TWO previous rounds
		// (v90 fifth/eighth fixes, both left in place below, both harmless dead ends) guessed wrong.
		// The stack trace named the real chain directly: GeoscapeViewState.Update() ->
		// SwitchToPreviousState() -> StateStack.SwitchToState() -> ... -> UIStateEditVehicle.ExitState
		// - i.e. the exit was never coming from any InputEvent/click handler AT ALL, it was coming
		// from the state's own per-frame Update() tick.
		//
		// Confirmed directly in the GeoscapeViewState.Update(): `if
		// (this.ProcessBackToPreviousState && this._cancelEventPending) { ...; SwitchToPreviousState();
		// return; }` - and `_cancelEventPending` is set UNCONDITIONALLY by the base class's own
		// OnInputEventInternal (`this._cancelEventPending = ev.Type == Pressed && ev.Name == "Cancel"`)
		// - which runs BEFORE the virtual OnInputEvent (this Prefix's own target) is even called, and
		// is completely unaffected by whatever OnInputEvent returns. So no matter how correctly this
		// Prefix "handles" the click (builds the menu, returns __result=true), the flag was already
		// latched true a moment earlier, and the very next Update() tick pops the state anyway -
		// exactly matching every previous round's observation ("menu built successfully, screen still
		// exits on the same tick"). This is NOT a vanilla bug: UIStateEditSoldier's own
		// source shows it overrides `ProcessBackToPreviousState => false` specifically to disable this
		// entire base-class auto-pop mechanism, relying purely on its own OnInputEvent - confirmed the
		// exact, deliberate reason the soldier screen never had this symptom. UIStateEditVehicle has
		// no such override (inherits the base default `true`), so vanilla's OWN OnInputEvent normally
		// gets away with it only because its own Cancel branch calls `base.SwitchToPreviousState()`
		// itself, synchronously, before Update() ever gets a chance to redundantly pop it again (the
		// state is already gone by then) - this mod's own Prefix breaks that coincidence by SKIPPING
		// vanilla's method body outright whenever it swallows the click, so the redundant, stale
		// auto-pop is the only thing left to fire.
		//
		// Fixed by clearing the private `_cancelEventPending` field directly (reflection - the field
		// is declared on the base GeoscapeViewState, not UIStateEditVehicle itself) whenever this
		// Prefix swallows a click - neutralizing the exact condition Update()'s own auto-pop check
		// reads, with no effect on anything else (UIStateEditVehicle doesn't override OnCancel(), the
		// only other thing that flag drives, so clearing it early has no side effect there either).
		private static readonly FieldInfo CancelEventPendingField =
			typeof(GeoscapeViewState).GetField("_cancelEventPending", BindingFlags.NonPublic | BindingFlags.Instance);

		private static bool Prefix(UIStateEditVehicle __instance, InputEvent ev, ref bool __result)
		{
			if (ev.Type != InputEventType.Pressed || !ChaosInventoryPatchUtl.IsRightMouseButtonDown()
				|| (ev.Name != "Cancel" && ev.Name != "VehicleRightMouseClickSelection"))
			{
				return true;
			}

			List<RaycastResult> hits = ChaosInventoryPatchUtl.RaycastPointer();
			if (!ChaosInventoryPatchUtl.IsOverInventoryArea(__instance, hits))
			{
				return true;
			}

			if (ev.Name == "VehicleRightMouseClickSelection")
			{
				// Swallow the separate event too, over the inventory area - nothing further to do
				// here (the "Cancel" branch below already shows the menu/hint for the same physical
				// click), just prevent it from reaching UIStateVehicleSelected.OnRightClickSelection().
				Log.Debug("OnInputEvent (vehicle): swallowed right-click VehicleRightMouseClickSelection over the inventory area.");
				__result = true;
				return false;
			}

			ChaosInventoryPatchUtl.TryShowStorageItemContextMenu(__instance, hits);
			CancelEventPendingField?.SetValue(__instance, false);
			Log.Debug("OnInputEvent (vehicle): swallowed right-click Cancel over the inventory area (and cleared the pending-cancel flag so Update()'s own auto-pop check doesn't fire on the next tick).");
			__result = true;
			return false;
		}
	}

	// v90 CORRECTION, same round: the "VehicleRightMouseClickSelection" theory above was WRONG - a
	// real retest's Player.log showed the screen still exiting exactly as before (menu built
	// successfully, then immediately "Entering Geoscape UI state: UIStateInitial" ->
	// "UIStateVehicleSelected"), while the new unconditional diagnostic logging for that exact event
	// name NEVER fired even once. Since that logging is unconditional on any right-click reaching
	// UIStateEditVehicle.OnInputEvent, its total absence proves UIStateVehicleSelected's own
	// OnInputEvent (and therefore OnRightClickSelection) is NOT reached via a top-down chain through
	// the state stacked above it - each active state is evidently dispatched the same physical input
	// independently, so nothing done inside UIStateEditVehicle's own OnInputEvent, no matter which
	// event names it intercepts, can ever prevent a DIFFERENT, deeper state from independently acting
	// on that same click. The previous patch above is left in place (harmless - it simply never
	// triggers) rather than removed, since this is proven by absence of evidence, not a positive
	// test of the alternate hypothesis - flagged here so a future reader doesn't trust its own
	// reasoning either without checking Player.log first.
	//
	// The only reliable fix is to patch the actual method causing the exit directly, rather than
	// anything upstream of it: UIStateVehicleSelected.OnRightClickSelection() (private, confirmed in
	// the source to call Context.View.SelectSiteAtCursor() unconditionally on every
	// right-click). This Prefix raycasts the cursor and skips the reselection entirely whenever the
	// click actually lands on a real UIInventorySlot - deliberately NOT scoped to any specific state
	// type (UIStateEditVehicle, UIStateEditSoldier, or anything else), since the actual problem is
	// "an equip screen's own inventory is open and handling this click," not "which particular state
	// happens to own it" - this also means it transparently protects the soldier equip screen too, in
	// the (currently unconfirmed) case it's ever opened while a vehicle sits selected underneath it.
	[HarmonyPatch(typeof(UIStateVehicleSelected), "OnRightClickSelection")]
	internal static class Patch_UIStateVehicleSelected_OnRightClickSelection_SuppressOverInventory
	{
		private static bool Prefix()
		{
			if (!ChaosInventoryPatchUtl.IsRightMouseButtonDown())
			{
				return true;
			}
			List<RaycastResult> hits = ChaosInventoryPatchUtl.RaycastPointer();
			if (hits.Any(h => h.gameObject.GetComponentInParent<UIInventorySlot>() != null))
			{
				Log.Debug("OnRightClickSelection: suppressed - right-click landed on an inventory slot (an equip screen is open on top of vehicle-selected).");
				return false;
			}
			return true;
		}
	}

	internal static class ChaosInventoryPatchUtl
	{
		// UIStateEditSoldier (human soldier equip) and UIStateEditVehicle (ground-vehicle-unit
		// equip, e.g. tanks/crawlers riding along on an aircraft) share the same base class
		// (GeoscapeViewState) and, confirmed via source, the exact same private method
		// shapes (StorageItems/RefreshStorage/UpdateStorage/EnterState/ExitState/
		// CharacterChangedHandler/UpdateState/OnInputEvent) and the same private
		// _soldierEquipModule/_currentCharacter/_faction/_refreshStorage members - but since none
		// of this is inherited (each class declares its own), every reflection accessor needs one
		// cached member per concrete type, dispatched on the runtime type of `state`. This lets
		// every method below take the common `GeoscapeViewState` base and work for either screen
		// through the exact same reconciliation logic, rather than duplicating it.
		private static readonly PropertyInfo SoldierEquipModuleProp_Soldier =
			typeof(UIStateEditSoldier).GetProperty("_soldierEquipModule", BindingFlags.NonPublic | BindingFlags.Instance);
		private static readonly PropertyInfo SoldierEquipModuleProp_Vehicle =
			typeof(UIStateEditVehicle).GetProperty("_soldierEquipModule", BindingFlags.NonPublic | BindingFlags.Instance);

		// GeoscapeViewState._geoscapeModules is declared ONCE on the shared base class (confirmed
		// in the source: "protected GeoscapeModulesData _geoscapeModules => this.
		// Context.View.GeoscapeModules;") - every state subclass's own private
		// _soldierEquipModule/_vehicleEquipModule property is just a thin wrapper reading a
		// different field off this SAME shared object. One accessor here works for ANY
		// GeoscapeViewState subclass, letting the Air Force (Vehicle Roster) screen borrow
		// UIModuleSoldierEquip.StorageList.ItemSlotPrefab for read-only rendering even though
		// UIStateVehicleRoster never uses that module itself.
		private static readonly PropertyInfo GeoscapeModulesProp =
			typeof(GeoscapeViewState).GetProperty("_geoscapeModules", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly PropertyInfo VehicleEquipModuleProp_Roster =
			typeof(UIStateVehicleRoster).GetProperty("_vehicleEquipModule", BindingFlags.NonPublic | BindingFlags.Instance);

		private static UIModuleSoldierEquip GetSoldierEquipModuleFromAnyState(GeoscapeViewState state)
		{
			GeoscapeModulesData modules = GeoscapeModulesProp?.GetValue(state) as GeoscapeModulesData;
			return modules?.SoldierEquipModule;
		}

		private static readonly FieldInfo CurrentCharacterField_Soldier =
			typeof(UIStateEditSoldier).GetField("_currentCharacter", BindingFlags.NonPublic | BindingFlags.Instance);
		private static readonly FieldInfo CurrentCharacterField_Vehicle =
			typeof(UIStateEditVehicle).GetField("_currentCharacter", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly FieldInfo FactionField_Soldier =
			typeof(UIStateEditSoldier).GetField("_faction", BindingFlags.NonPublic | BindingFlags.Instance);
		private static readonly FieldInfo FactionField_Vehicle =
			typeof(UIStateEditVehicle).GetField("_faction", BindingFlags.NonPublic | BindingFlags.Instance);

		// Input is in UnityEngine.InputLegacyModule which isn't in the ModSDK references.
		// Cache members via reflection for compile-time-clean calls (same trick as ChaosOverwatch).
		private static readonly Type InputType =
			Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule");

		private static readonly MethodInfo GetMouseButtonDownMethod =
			InputType?.GetMethod("GetMouseButtonDown", new[] { typeof(int) });

		private static readonly PropertyInfo MousePositionProp =
			InputType?.GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static);

		// Direct ItemStorage.RemoveItem(GeoItem)/AddItem(GeoItem) calls threw MissingMethodException at
		// runtime - even wrapped in try/catch, since Mono can throw this at JIT-compile time of the
		// METHOD CONTAINING the call (a verification failure on an unresolvable method token), not as a
		// normal exception during execution - a try/catch around the call cannot help, since the
		// containing method fails to JIT at all before the catch block's protection ever applies. So
		// this mod never emits a direct call to those two methods anywhere in its IL. Instead it reaches
		// into ItemStorage's private _storageItems dictionary via reflection to build temporary
		// single-item ItemStorage instances, then moves items using RemoveItems(ItemStorage)/
		// AddItems(ItemStorage) - the bulk overloads vanilla's own UpdateStorage() depends on, and the
		// only ItemStorage methods this mod calls without a reported crash.
		private static readonly FieldInfo StorageItemsField =
			typeof(ItemStorage).GetField("_storageItems", BindingFlags.NonPublic | BindingFlags.Instance);

		// internal (not private): also called by Patch_ItemStorage_TrimStorageToCapacity_ProtectVehicleClaims.
		// Logs the ACTUAL real stack's Count/CurrentCharges/TotalAvailableCharges for one ItemDef in
		// one real ItemStorage, right now - the ground truth, independent of whatever any tracked
		// representative/display clone claims. Used to bracket every real AddToStorage/
		// RemoveFromStorage call so the exact point a total unexpectedly changes is directly visible
		// in Player.log, instead of only being inferable from the end-state numbers the UI shows.
		private static void LogRealStackState(string label, ItemStorage storage, ItemDef itemDef)
		{
			GeoItem real;
			if (storage != null && storage.Items.TryGetValue(itemDef, out real))
			{
				int total = itemDef.ChargesMax > 0
					? itemDef.ChargesMax * (real.CommonItemData.Count - 1) + real.CommonItemData.CurrentCharges
					: 0;
				Log.Debug($"{label}: '{itemDef.name}' real stack Count={real.CommonItemData.Count}, CurrentCharges={real.CommonItemData.CurrentCharges}, TotalAvailableCharges={total}.");
			}
			else
			{
				Log.Debug($"{label}: '{itemDef.name}' not present in real storage.");
			}
		}

		internal static bool AddToStorage(ItemStorage storage, GeoItem item)
		{
			if (StorageItemsField == null)
			{
				Log.Debug("AddToStorage: couldn't find ItemStorage's internal _storageItems field via reflection - nothing changed.");
				return false;
			}
			ItemStorage temp = new ItemStorage();
			Dictionary<ItemDef, GeoItem> tempDict = StorageItemsField.GetValue(temp) as Dictionary<ItemDef, GeoItem>;
			if (tempDict == null)
			{
				Log.Debug("AddToStorage: _storageItems wasn't the expected Dictionary<ItemDef, GeoItem> type - nothing changed.");
				return false;
			}
			tempDict[item.ItemDef] = item;
			Log.Debug($"AddToStorage: adding '{item.CommonItemData.GetDisplayName()}' x{item.CommonItemData.Count} charges={item.CommonItemData.CurrentCharges} to '{storage}'.");
			LogRealStackState("AddToStorage.before", storage, item.ItemDef);
			storage.AddItems(temp);
			LogRealStackState("AddToStorage.after", storage, item.ItemDef);
			return true;
		}

		// Explicitly splits any magazine(s) currently loaded in `item`'s AmmoManager out into their
		// own standalone GeoItems, clearing them from `item` as a side effect (matching reality: the
		// ammo is being taken out of the weapon and stored as its own separate item). Done
		// deterministically by this mod itself, at the exact moment an item is about to be committed
		// to real storage, INSTEAD OF relying on ItemStorage.AddItem's own vanilla auto-unload-and-
		// recurse behavior (gated on the destination's private _isFactionStorage flag - true by
		// default for both GeoFaction.ItemStorage and GeoSite.ItemStorage). That vanilla side effect
		// is real and not itself wrong (a stored weapon and its magazine are always separate stack
		// entries in vanilla's own data model - confirmed via CommonItemData.SetOwnerItem/Clone()
		// always building a fresh AmmoManager for a stored item regardless of this flag, so there is
		// no vanilla insertion path where a STORED weapon can stay visibly "loaded" at all) - but this
		// mod's aggregated net-physical-delta write path (see the v38/v69 notes) had no reliable way
		// to attribute that side-effect magazine back to the correct vehicle claim once more than one
		// item was involved in the same commit, and confirmed to occasionally mis-attribute it to
		// unclaimed Global Inventory instead. Extracting it explicitly, up front, means the magazine
		// is registered through the exact same tracked (storage, representative item, claim vehicle)
		// path as any other item this mod adds - no separate event-based side channel needed at all.
		// Safe to call on an item with no ammo at all (Ammo == null) or nothing currently loaded -
		// returns an empty list.
		private static List<GeoItem> ExtractLoadedAmmo(GeoItem item)
		{
			if (item == null)
			{
				return new List<GeoItem>();
			}
			AmmoManager ammo = item.CommonItemData?.Ammo;
			Log.Debug($"ExtractLoadedAmmo: '{item.CommonItemData.GetDisplayName()}' (charges={item.CommonItemData.CurrentCharges}) - hasAmmoManager={ammo != null}, loadedMagazines={(ammo?.LoadedMagazines.Count ?? 0)}.");
			if (ammo == null || ammo.LoadedMagazines.Count == 0)
			{
				return new List<GeoItem>();
			}
			List<GeoItem> magazines = ammo.UnloadMagazines().OfType<GeoItem>().ToList();
			Log.Debug($"ExtractLoadedAmmo: '{item.CommonItemData.GetDisplayName()}' - extracted {magazines.Count} magazine(s): [{string.Join(", ", magazines.Select(m => $"{m.CommonItemData.GetDisplayName()} x{m.CommonItemData.Count} charges={m.CommonItemData.CurrentCharges}"))}].");
			return magazines;
		}

		// internal (not private): also called by Patch_ItemStorage_TrimStorageToCapacity_ProtectVehicleClaims.
		internal static bool RemoveFromStorage(ItemStorage storage, GeoItem item)
		{
			if (StorageItemsField == null)
			{
				Log.Debug("RemoveFromStorage: couldn't find ItemStorage's internal _storageItems field via reflection - nothing changed.");
				return false;
			}
			ItemStorage temp = new ItemStorage();
			Dictionary<ItemDef, GeoItem> tempDict = StorageItemsField.GetValue(temp) as Dictionary<ItemDef, GeoItem>;
			if (tempDict == null)
			{
				Log.Debug("RemoveFromStorage: _storageItems wasn't the expected Dictionary<ItemDef, GeoItem> type - nothing changed.");
				return false;
			}
			tempDict[item.ItemDef] = item;
			Log.Debug($"RemoveFromStorage: removing '{item.CommonItemData.GetDisplayName()}' x{item.CommonItemData.Count} charges={item.CommonItemData.CurrentCharges} from '{storage}'.");
			LogRealStackState("RemoveFromStorage.before", storage, item.ItemDef);
			storage.RemoveItems(temp);
			LogRealStackState("RemoveFromStorage.after", storage, item.ItemDef);
			return true;
		}

		// A direct call to ItemDef.IsInstant's getter threw the exact same class of
		// MissingMethodException documented above for ItemStorage.RemoveItem/AddItem - confirmed via
		// a real Player.log stack trace naming get_IsInstant(). The reflection-based fallback tried
		// next (GetProperty("IsInstant", ...) off the item's runtime type) was ALSO confirmed
		// failing via Player.log - it returned null (property not found) for every single item
		// tested (WeaponDef, TacticalItemDef, EquipmentDef alike), not an exception - meaning the
		// live game's actual ItemDef class genuinely has no property by that name at all, not just a
		// token-resolution mismatch. Since IsInstant is only ever a one-line wrapper around the
		// ManufacturePointsCost FIELD (confirmed in the source: "return
		// this.CostInManufacturePoints == 0f;" where CostInManufacturePoints just returns
		// ManufacturePointsCost), this reads that underlying public field directly instead - fields
		// resolve through entirely different IL (ldfld, not a callvirt to a compiler-generated
		// getter method), so this doesn't share IsInstant's specific failure mode, and a field is
		// also generally a much more stable, less likely to be renamed/removed piece of surface area
		// across game patches than a computed property wrapping it. If even the field can't be found,
		// dumps every public field/property name actually present on the live type to Player.log,
		// so identifying the real member name next time doesn't require another guess.
		private static readonly FieldInfo ManufacturePointsCostField =
			typeof(ItemDef).GetField("ManufacturePointsCost", BindingFlags.Public | BindingFlags.Instance);

		private static bool IsInstantManufacture(ItemDef itemDef)
		{
			if (itemDef == null)
			{
				return false;
			}
			try
			{
				FieldInfo field = ManufacturePointsCostField ?? itemDef.GetType().GetField("ManufacturePointsCost", BindingFlags.Public | BindingFlags.Instance);
				if (field == null)
				{
					Type type = itemDef.GetType();
					string fields = string.Join(", ", type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name));
					string props = string.Join(", ", type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name));
					Log.Debug($"IsInstantManufacture: '{itemDef.name}' - ManufacturePointsCost field not found on {type.FullName}. Public fields: [{fields}]. Public properties: [{props}].");
					return false;
				}
				float cost = (float)field.GetValue(itemDef);
				return cost <= 0f;
			}
			catch (Exception ex)
			{
				Log.Debug($"IsInstantManufacture: '{itemDef.name}' - reading ManufacturePointsCost threw {ex.GetType().Name}: {ex.Message} - defaulting to false.");
				return false;
			}
		}

		public static bool IsRightMouseButtonDown()
		{
			return GetMouseButtonDownMethod != null && (bool)GetMouseButtonDownMethod.Invoke(null, new object[] { 1 });
		}

		public static Vector2 GetMousePosition()
		{
			if (MousePositionProp == null)
			{
				return Vector2.zero;
			}
			Vector3 pos = (Vector3)MousePositionProp.GetValue(null, null);
			return new Vector2(pos.x, pos.y);
		}

		public static List<RaycastResult> RaycastPointer()
		{
			List<RaycastResult> results = new List<RaycastResult>();
			if (EventSystem.current == null)
			{
				return results;
			}
			PointerEventData pointerData = new PointerEventData(EventSystem.current) { position = GetMousePosition() };
			EventSystem.current.RaycastAll(pointerData, results);
			return results;
		}

		public static bool IsOverInventoryArea(GeoscapeViewState state, List<RaycastResult> hits)
		{
			UIModuleSoldierEquip module = GetModule(state);
			if (module == null)
			{
				return false;
			}
			foreach (RaycastResult result in hits)
			{
				Transform hit = result.gameObject.transform;
				if (IsUnderList(hit, module.StorageList) || IsUnderList(hit, module.InventoryList)
					|| IsUnderList(hit, module.ReadyList) || IsUnderList(hit, module.ArmorList))
				{
					return true;
				}
			}
			return false;
		}

		private static bool IsUnderList(Transform hit, UIInventoryList list)
		{
			return list != null && hit.IsChildOf(list.transform);
		}

		public static GeoCharacter GetCurrentCharacter(GeoscapeViewState state)
		{
			if (state is UIStateEditSoldier)
			{
				return CurrentCharacterField_Soldier?.GetValue(state) as GeoCharacter;
			}
			if (state is UIStateEditVehicle)
			{
				return CurrentCharacterField_Vehicle?.GetValue(state) as GeoCharacter;
			}
			return null;
		}

		public static GeoFaction GetFaction(GeoscapeViewState state)
		{
			if (state is UIStateEditSoldier)
			{
				return FactionField_Soldier?.GetValue(state) as GeoFaction;
			}
			if (state is UIStateEditVehicle)
			{
				return FactionField_Vehicle?.GetValue(state) as GeoFaction;
			}
			return null;
		}

		/// Finds the aircraft (if any) that currently carries the soldier/vehicle-unit being
		/// displayed, by scanning the viewer faction's own vehicles - there's no back-reference
		/// from GeoCharacter to its container vehicle in vanilla, so this is a forward search each
		/// time. Works identically for a ground-vehicle-unit character (GeoVehicle.Units is a
		/// plain GeoCharacter collection shared by human soldiers and vehicle units alike -
		/// confirmed via source).
		public static GeoVehicle GetVehicleForCurrentCharacter(GeoscapeViewState state)
		{
			GeoCharacter character = GetCurrentCharacter(state);
			GeoFaction faction = GetFaction(state);
			if (character == null || faction == null)
			{
				return null;
			}
			return faction.Vehicles.FirstOrDefault(v => v.Units.Contains(character));
		}

		/// A site only counts as a valid storage location if it's an actual player-owned Phoenix
		/// base - GeoFaction.Sites (SitesByOwner) turned out to be broader than that and also
		/// includes Havens, which are not the player's own storage and must never be assignable.
		/// Checked two ways, either one sufficing: site.GetComponent&lt;GeoPhoenixBase&gt;() != null
		/// (the idiom vanilla itself uses throughout the source - GeoFactionReward,
		/// UIStateVehicleSelected, UIModuleActorCycle, etc.) OR the site being listed in the
		/// faction's own GeoPhoenixFaction.Bases. Deliberately an OR, not just the component check
		/// alone: a version relying on the component check only caused a real regression (docked
		/// base<->aircraft transfer stopped offering the base at all), so this errs toward still
		/// recognizing a legitimate base if either signal confirms it, while a pure Haven still
		/// fails both.
		private static bool IsPlayerBase(GeoFaction faction, GeoSite site)
		{
			if (site == null)
			{
				return false;
			}
			if (site.GetComponent<GeoPhoenixBase>() != null)
			{
				return true;
			}
			GeoPhoenixFaction phoenixFaction = faction as GeoPhoenixFaction;
			return phoenixFaction != null && phoenixFaction.Bases.Any(b => b.Site == site);
		}

		/// Finds the base (if any) the soldier is stationed at, mirroring GetVehicleForCurrentCharacter
		/// exactly: GeoSite implements the same IGeoCharacterContainer interface GeoVehicle does (Units/
		/// AddCharacter/GetAllCharacters), so the same crew-membership scan applies. Restricted to
		/// actual player Phoenix bases (see IsPlayerBase) so a soldier can never be "stationed at" a
		/// Haven from this mod's point of view.
		public static GeoSite GetSiteForCurrentCharacter(GeoscapeViewState state)
		{
			GeoCharacter character = GetCurrentCharacter(state);
			GeoFaction faction = GetFaction(state);
			if (character == null || faction == null)
			{
				return null;
			}
			return faction.Sites.Where(s => IsPlayerBase(faction, s)).FirstOrDefault(s => s.Units.Contains(character));
		}

		// Caches the last successfully-resolved LocationContext per open Edit Soldier screen.
		// Critical safety net: RefreshStorage/UpdateStorage/ReapplySlotTags/the context menu all
		// call ResolveLocationContext independently, and if a live resolution were to fail (return
		// null) on ONE of those calls but not the others for the SAME screen instance, the failing
		// call's Harmony Prefix would return true and let VANILLA'S OWN UpdateStorage()/
		// StorageItems() run instead - which diffs the Storage list (still full of items pulled
		// from this mod's own per-vehicle/per-base storages) against a single vanilla ItemStorage
		// (typically the global pool) that has no idea those items exist elsewhere. Vanilla's diff
		// would then see every shown item as "newly added" and ADD it to that single storage
		// WITHOUT ever removing it from its real source - silent item duplication. Reusing the last
		// good context instead of falling through keeps every call for a given screen instance
		// consistent, closing that gap regardless of why a single resolution attempt came up empty.
		private static readonly ConditionalWeakTable<GeoscapeViewState, LocationContext> _lastGoodCtx =
			new ConditionalWeakTable<GeoscapeViewState, LocationContext>();

		/// Resolves the full location context (own storage + label + reachable destinations) for the
		/// currently displayed character: an aircraft if they're crewing one, otherwise the base
		/// they're stationed at, otherwise null (character not tracked by this mod at all - vanilla
		/// handles it). Falls back to the last successfully-resolved context for this exact screen
		/// instance if the live lookup comes up empty (see _lastGoodCtx above) - only truly returns
		/// null when this screen has never resolved a context at all.
		public static LocationContext ResolveLocationContext(GeoscapeViewState state)
		{
			LocationContext ctx = ResolveLocationContextLive(state);
			if (ctx != null)
			{
				_lastGoodCtx.Remove(state);
				_lastGoodCtx.Add(state, ctx);
				return ctx;
			}

			LocationContext cached;
			if (_lastGoodCtx.TryGetValue(state, out cached))
			{
				Log.Debug($"ResolveLocationContext: live resolution failed, reusing last-known-good context '{cached.Label}' instead of falling through to vanilla.");
				return cached;
			}
			return null;
		}

		private static LocationContext ResolveLocationContextLive(GeoscapeViewState state)
		{
			GeoFaction faction = GetFaction(state);
			if (faction == null)
			{
				return null;
			}

			GeoVehicle vehicle = GetVehicleForCurrentCharacter(state);
			if (vehicle != null)
			{
				List<StorageOption> all = GetDockedStorageOptions(vehicle);
				return new LocationContext
				{
					Label = vehicle.Name,
					Storage = faction.ItemStorage,
					IsBase = false,
					Destinations = all.Skip(1).ToList(),
					Faction = faction,
					Vehicle = vehicle,
					Key = LocationKey.ForVehicle(vehicle)
				};
			}

			GeoSite site = GetSiteForCurrentCharacter(state);
			if (site != null)
			{
				List<StorageOption> all = GetDockedStorageOptionsForSite(site, faction);
				return new LocationContext
				{
					Label = site.SiteName.Localize(null),
					Storage = site.ItemStorage,
					IsBase = true,
					Destinations = all.Skip(1).ToList(),
					Faction = faction,
					Key = LocationKey.ForBase(site)
				};
			}

			return null;
		}

		/// Builds the ordered list of storages associated with this aircraft right now: itself
		/// first, then - only while actually docked - the base and every other aircraft docked there
		/// too. Recomputed fresh on every call (never cached) so an aircraft arriving/leaving the
		/// base while this screen is open is picked up immediately.
		public static List<StorageOption> GetDockedStorageOptions(GeoVehicle vehicle)
		{
			// A vehicle's "own storage" is now a claim overlay physically backed by its faction's
			// real ItemStorage (see ChaosVehicleAssignment) - Storage here is that real backing
			// object (needed by callers that still enumerate/mutate it directly), while Key is the
			// actual location identity used for comparisons everywhere else.
			List<StorageOption> options = new List<StorageOption>
			{
				new StorageOption { Label = vehicle.Name, Storage = vehicle.Owner?.ItemStorage, IsBase = false, Vehicle = vehicle, Key = LocationKey.ForVehicle(vehicle) }
			};

			bool docked = vehicle.CurrentSite != null && !vehicle.Travelling;
			if (!docked)
			{
				return options;
			}

			GeoSite site = vehicle.CurrentSite;
			// Only offer the base itself as a destination if it's an actual player Phoenix base -
			// never a Haven (see IsPlayerBase).
			bool isPlayerBase = IsPlayerBase(vehicle.Owner, site);
			if (isPlayerBase)
			{
				options.Add(new StorageOption { Label = site.SiteName.Localize(null), Storage = site.ItemStorage, IsBase = true, Key = LocationKey.ForBase(site) });
			}

			foreach (GeoVehicle other in site.Vehicles)
			{
				// site.Vehicles enumerates every vehicle docked there regardless of owner - restrict
				// to the same faction's own aircraft. This filter was removed in v17 on suspicion of
				// being the cause of the "menu missing entirely" regression, but the real cause
				// (confirmed later, v18) was the unrelated ItemDef.IsInstant MissingMethodException
				// crashing the whole Prefix - re-added now that the actual crash is fixed.
				if (other == vehicle || other.Owner != vehicle.Owner)
				{
					continue;
				}
				options.Add(new StorageOption { Label = other.Name, Storage = other.Owner?.ItemStorage, IsBase = false, Vehicle = other, Key = LocationKey.ForVehicle(other) });
			}

			return options;
		}

		/// Mirrors GetDockedStorageOptions but starting from a base: itself first, then every aircraft
		/// currently docked there (restricted to the same faction, since site.Vehicles enumerates every
		/// vehicle at the site regardless of owner).
		public static List<StorageOption> GetDockedStorageOptionsForSite(GeoSite site, GeoFaction faction)
		{
			List<StorageOption> options = new List<StorageOption>
			{
				new StorageOption { Label = site.SiteName.Localize(null), Storage = site.ItemStorage, IsBase = true, Key = LocationKey.ForBase(site) }
			};
			foreach (GeoVehicle v in site.Vehicles)
			{
				if (v.Owner != faction)
				{
					continue;
				}
				options.Add(new StorageOption { Label = v.Name, Storage = v.Owner?.ItemStorage, IsBase = false, Vehicle = v, Key = LocationKey.ForVehicle(v) });
			}
			return options;
		}

		/// Every location reachable right now: the current context's own storage, its docked
		/// destinations, and the faction-wide global pool - which, per explicit user request, is
		/// always fully accessible from anywhere until an item is deliberately moved into a specific
		/// base/aircraft.
		private static List<StorageOption> GetReachableLocations(LocationContext ctx)
		{
			List<StorageOption> list = new List<StorageOption>
			{
				new StorageOption { Label = ctx.Label, Storage = ctx.Storage, IsBase = ctx.IsBase, Vehicle = ctx.Vehicle, Key = ctx.Key }
			};
			list.AddRange(ctx.Destinations);
			if (ctx.Faction?.ItemStorage != null)
			{
				list.Add(new StorageOption { Label = ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_GLOBAL_INVENTORY"), Storage = ctx.Faction.ItemStorage, IsBase = false, Key = LocationKey.ForGlobalUntagged(ctx.Faction) });
			}
			return list;
		}

		// GeoItem.Clone() and a bare `new GeoItem(itemDef, count, -1, null, -100)` both construct a
		// BRAND NEW, empty AmmoManager for any item whose ItemDef takes ammunition
		// (CommonItemData.SetOwnerItem always starts a fresh one - confirmed in the
		// source) - silently discarding whatever magazine was actually loaded, even though Count/
		// Charges/Malfunction are otherwise preserved correctly. This is why a soldier's weapon,
		// unequipped onto an aircraft/base with ammo loaded, showed as empty ammo the very next full
		// refresh (closing and reopening the screen): the real GeoItem sitting in
		// faction.ItemStorage/site.ItemStorage never lost its loaded magazine, but every display
		// clone built from it was a fresh, unrelated object with none - and the same, only-just-
		// synthesized clone is what a subsequent equip/drag would actually hand to the soldier.
		// Immediate same-session equip/unequip never showed the bug because that path keeps
		// reusing the ORIGINAL real reference (no refresh happens in between); it only ever
		// surfaced across a full refresh (EnterState/CharacterChangedHandler), which is exactly
		// when GetDisplayItems below runs.
		//
		// Fixed by reusing vanilla's own full item-state round-trip (ToItemData()/new
		// GeoItem(ItemData)) - the same mechanism vanilla itself uses to persist/restore a GeoItem
		// across a save/load, so it's guaranteed to carry Charges/Malfunction/loaded-Ammo
		// correctly - then restoring the desired display Count via reflection onto CommonItemData's
		// private _count field, since the ItemData constructor always resets Count to 1 (ItemData
		// represents a single item's persisted state, not a whole stack's size).
		private static readonly FieldInfo CommonItemDataCountField =
			typeof(CommonItemData).GetField("_count", BindingFlags.NonPublic | BindingFlags.Instance);

		// CommonItemData.LoadFromData (vanilla, confirmed in the source) has a landmine
		// for a non-ammo-manager item (Ammo == null): `else if (data.Charges == 0 &&
		// this.ItemDef.ChargesMax > 0) { this._charges = this.ItemDef.ChargesMax; this._count = 1; }`
		// - i.e. a round-tripped charge value of exactly 0 is treated as "never saved, default to
		// full" instead of "genuinely empty." A simple charge-based stack (e.g. a plain ammo/magazine
		// ItemDef with no CompatibleAmmunition of its own) legitimately reaches 0 through ordinary
		// play, and BuildDisplayItem's ToItemData()/new GeoItem(ItemData) round-trip would silently
		// refill it back to ChargesMax via this exact path. Patched back down via reflection whenever
		// this triggers - see BuildDisplayItem below.
		private static readonly FieldInfo CommonItemDataChargesField =
			typeof(CommonItemData).GetField("_charges", BindingFlags.NonPublic | BindingFlags.Instance);

		// `chargesOverride`: when a real stack's Count is being split across more than one display
		// bucket (a vehicle's claim, another vehicle's claim, and/or the Global untagged remainder -
		// see ChargesOverrideFor below), every bucket independently cloning the SAME shared
		// CurrentCharges value would be misleading - e.g. a stack of "1 full magazine + 1 at 9/12"
		// split into two 1-unit buckets would show "9/12" on BOTH sides instead of one reading 12/12
		// and the other 9/12 (the total was always correct - see the v72/v73 notes - only the
		// display was ambiguous). When set, this forces the display's charges to this value
		// (unconditionally ChargesMax for a vehicle claim - see ChargesOverrideFor's own comment for
		// why that's always safely true, not a guess) instead of cloning the real stack's shared
		// value as-is.
		private static GeoItem BuildDisplayItem(GeoItem real, int count, int? chargesOverride = null)
		{
			if (real == null || count <= 0)
			{
				return null;
			}
			int realCharges = real.CommonItemData.CurrentCharges;
			bool realHadAmmo = real.CommonItemData.Ammo != null;
			GeoItem display = new GeoItem(real.ToItemData());
			if (CommonItemDataCountField != null)
			{
				CommonItemDataCountField.SetValue(display.CommonItemData, count);
			}
			else
			{
				Log.Debug("BuildDisplayItem: couldn't find CommonItemData's internal _count field via reflection - display count may be wrong.");
			}
			if (!realHadAmmo && realCharges == 0 && display.CommonItemData.CurrentCharges != 0)
			{
				if (CommonItemDataChargesField != null)
				{
					Log.Debug($"BuildDisplayItem: '{real.CommonItemData.GetDisplayName()}' hit the LoadFromData charges==0 landmine (refilled to {display.CommonItemData.CurrentCharges}) - patching back down to 0.");
					CommonItemDataChargesField.SetValue(display.CommonItemData, 0);
				}
				else
				{
					Log.Debug("BuildDisplayItem: couldn't find CommonItemData's internal _charges field via reflection - the charges==0 landmine could not be patched.");
				}
			}
			if (chargesOverride.HasValue && display.CommonItemData.CurrentCharges != chargesOverride.Value)
			{
				if (CommonItemDataChargesField != null)
				{
					CommonItemDataChargesField.SetValue(display.CommonItemData, chargesOverride.Value);
				}
				else
				{
					Log.Debug("BuildDisplayItem: couldn't find CommonItemData's internal _charges field via reflection - chargesOverride could not be applied.");
				}
			}
			Log.Debug($"BuildDisplayItem: '{real.CommonItemData.GetDisplayName()}' realCharges={realCharges} realHadAmmo={realHadAmmo} realCount={real.CommonItemData.Count} chargesOverride={chargesOverride} -> displayCharges={display.CommonItemData.CurrentCharges} displayCount={display.CommonItemData.Count} requestedCount={count}.");
			return display;
		}

		/// Whether itemDef is a "pooled charge" item - ammo, magazines, batteries, anything where
		/// multiple units combine into ONE shared partial-charge value (ChargesMax>0 &&
		/// CombineWhenStacking, the exact vanilla test for "this stack pools a shared charge").
		private static bool IsPooledChargeItem(ItemDef itemDef)
		{
			return itemDef != null && itemDef.ChargesMax > 0 && itemDef.CombineWhenStacking;
		}

		// v90 fix history: this used to be disabled (always returned null) after a confirmed real
		// corruption bug - fabricating a GUESSED charge value baked it into a real, interactive
		// GeoItem clone, and vanilla's own blind auto-reload scan could pick the wrong (guessed)
		// bucket and "spend" fake charges against it, corrupting real data once this mod's own
		// reconciliation trusted that guess as ground truth. A later attempt tracked which vehicle
		// genuinely received a partial charge (a recorded fact instead of a guess) - correct, but
		// needed its own persisted map and separate bookkeeping.
		//
		// SIMPLIFIED per explicit user request: instead of tracking or guessing which bucket holds
		// the true partial charge, the claim system itself now refuses to let a vehicle claim GROW
		// at all while the real pool for a pooled-charge item currently has any partial remainder
		// (see ChaosVehicleAssignment.Adjust's own guard - the single choke point every claim-growth
		// call site already funnels through). That makes "a vehicle's claim of a pooled-charge item
		// is always genuinely full-charge" a GUARANTEED invariant, not an assumption - so showing
		// ChargesMax for any vehicle claim is never a guess anymore, just a restated fact, and no
		// map/tracking is needed to know which one to trust. Global Inventory is never overridden -
		// it's the only place a partial unit can ever legitimately end up, so its real value is
		// always already correct as shown. Bases are never overridden either - always real, separate
		// storage, never sharing a pooled value with anything else.
		// `real` is the actual matching real GeoItem this display entry is being built from (null if
		// none exists yet) - needed to check whether the "always full" assumption below still
		// actually holds, rather than applying it blindly.
		private static int? ChargesOverrideFor(LocationKey key, ItemDef itemDef, GeoItem real)
		{
			if (key.Kind != LocationKind.Vehicle || !IsPooledChargeItem(itemDef))
			{
				return null;
			}
			// The "always show ChargesMax" assumption above only holds while a genuine UNTAGGED
			// remainder still exists in the real pool for the true partial charge to be attributed
			// to (see this method's own comment above) - when every real unit is already claimed by
			// some vehicle, there's no other bucket left for a real partial charge to hide in, so
			// forcing ChargesMax here would just fabricate charges that don't physically exist.
			// Concretely confirmed via Player.log: this mod's own "+Ammo" reload sync
			// (TryCustomUpdateStorageCore's in-place "charges changed, count unchanged" branch) can
			// reduce an ALREADY-claimed unit's real charges below ChargesMax without ever touching
			// the claim count itself (Adjust's own growth-time reservation guard never runs, since
			// nothing is growing) - real storage correctly dropped to 1/5, but this override still
			// unconditionally forced the display back up to 5/5. Falling through to the real value
			// here fixes that, while leaving the ordinary multi-claimant case (an untagged remainder
			// genuinely exists) exactly as before.
			if (real == null || real.CommonItemData.Count <= 0)
			{
				return (int?)itemDef.ChargesMax;
			}
			int tagged = ChaosVehicleAssignment.GetTotalAssignedAcrossVehicles(key.Faction, itemDef);
			return tagged < real.CommonItemData.Count ? (int?)itemDef.ChargesMax : null;
		}

		/// Whether at least 1 more genuinely-full unit of itemDef is available to move OUT of Global
		/// Inventory right now, to ANY destination - mirrors the same reserved-unit cap
		/// ChaosVehicleAssignment.Adjust/TransferBetweenLocations themselves enforce (see their own
		/// comments), used here purely to decide whether to offer a "Move to..." menu option at all
		/// instead of offering one that's guaranteed to either no-op or move the wrong unit.
		private static bool HasFullUnitHeadroom(GeoFaction faction, ItemDef itemDef)
		{
			GeoItem real;
			if (faction?.ItemStorage == null || itemDef == null || !faction.ItemStorage.Items.TryGetValue(itemDef, out real)
				|| real.CommonItemData.Count <= 0)
			{
				return true;
			}
			if (!IsPooledChargeItem(itemDef) || real.CommonItemData.CurrentCharges == itemDef.ChargesMax)
			{
				return true;
			}
			int reservedCap = real.CommonItemData.Count - 1;
			int tagged = ChaosVehicleAssignment.GetTotalAssignedAcrossVehicles(faction, itemDef);
			return tagged < reservedCap;
		}

		/// Every real GeoItem this location currently displays - the single place that knows how to
		/// translate a LocationKey into actual on-screen contents. Base: read the real, separate
		/// GeoSite.ItemStorage directly (unchanged from before this redesign). Vehicle: synthesize
		/// one display clone per (ItemDef, claimed count) from the pure-metadata overlay - nothing
		/// here is a real GeoItem living anywhere except the faction's own real ItemStorage. Global
		/// untagged: the faction's real ItemStorage contents, MINUS whatever's currently claimed by
		/// any vehicle - so the same physical unit is never shown under two locations at once. Every
		/// branch now goes through BuildDisplayItem so the real item's charges/loaded-ammo state
		/// survives into the display clone (see BuildDisplayItem's own comment above).
		private static List<GeoItem> GetDisplayItems(LocationKey key)
		{
			switch (key.Kind)
			{
				case LocationKind.Base:
					return (key.Site?.ItemStorage?.Items.Values ?? Enumerable.Empty<GeoItem>())
						.Select(i => BuildDisplayItem(i, i.CommonItemData.Count))
						.Where(i => i != null)
						.ToList();

				case LocationKind.Vehicle:
				{
					GeoVehicle vehicle = key.Faction?.Vehicles.FirstOrDefault(v => v.VehicleID == key.VehicleId);
					if (vehicle == null)
					{
						return new List<GeoItem>();
					}
					ItemStorage realStorage = key.Faction?.ItemStorage;
					List<GeoItem> result = new List<GeoItem>();
					foreach (KeyValuePair<ItemDef, int> claim in ChaosVehicleAssignment.GetAll(vehicle))
					{
						GeoItem real = null;
						realStorage?.Items.TryGetValue(claim.Key, out real);
						if (real == null)
						{
							Log.Debug($"GetDisplayItems(Vehicle #{vehicle.VehicleID}): claim '{claim.Key.name}' x{claim.Value} has NO matching real item in faction storage - falling back to a blank synthesis.");
						}
						GeoItem display = real != null
							? BuildDisplayItem(real, claim.Value, ChargesOverrideFor(key, claim.Key, real))
							: new GeoItem(claim.Key, claim.Value, -1, null, -100);
						if (display != null)
						{
							result.Add(display);
						}
					}
					return result;
				}

				case LocationKind.GlobalUntagged:
				{
					ItemStorage storage = key.Faction?.ItemStorage;
					if (storage == null)
					{
						return new List<GeoItem>();
					}
					List<GeoItem> result = new List<GeoItem>();
					foreach (GeoItem real in storage.Items.Values)
					{
						int tagged = ChaosVehicleAssignment.GetTotalAssignedAcrossVehicles(key.Faction, real.ItemDef);
						int remainder = real.CommonItemData.Count - tagged;
						if (remainder > 0)
						{
							GeoItem display = BuildDisplayItem(real, remainder, ChargesOverrideFor(key, real.ItemDef, real));
							if (display != null)
							{
								result.Add(display);
							}
						}
					}
					return result;
				}

				default:
					return new List<GeoItem>();
			}
		}

		private static ItemStorage PhysicalStorageOf(LocationKey key)
		{
			switch (key.Kind)
			{
				case LocationKind.Base: return key.Site?.ItemStorage;
				case LocationKind.Vehicle:
				case LocationKind.GlobalUntagged:
					return key.Faction?.ItemStorage;
				default: return null;
			}
		}

		private static void AdjustVehicleAssignment(GeoFaction faction, int vehicleId, ItemDef itemDef, int delta)
		{
			GeoVehicle vehicle = faction?.Vehicles.FirstOrDefault(v => v.VehicleID == vehicleId);
			if (vehicle != null)
			{
				ChaosVehicleAssignment.Adjust(vehicle, itemDef, delta);
			}
		}

		/// Transfers count units of itemDef from one location to another. If both locations resolve
		/// to the SAME real backing ItemStorage (any combination of Vehicle/GlobalUntagged within one
		/// faction - they all physically share the faction's real ItemStorage), this is pure integer
		/// bookkeeping on the claim overlay with NO physical AddItems/RemoveItems call at all - the
		/// real item never needs to move because it was already sitting in the one place both
		/// locations reference. Only a transfer crossing a genuine physical boundary (either side is
		/// a Base) performs the real reflection-based remove+add.
		private static void TransferBetweenLocations(GeoFaction faction, LocationKey from, LocationKey to, ItemDef itemDef, int count)
		{
			if (count <= 0)
			{
				return;
			}

			// Per explicit user report/request: Global Inventory's display bundles genuinely-full
			// units together with the pool's one true partial-charge remainder into a single row (see
			// GetDisplayItems' GlobalUntagged case) - moving a unit "out" of that row without this
			// guard could pick the true partial one by pure chance. Concretely confirmed via user
			// report: 1 full + 1 at 6/36 in Global, "move to base" moved the 6/36 out and left a full
			// 36/36 behind - the OPPOSITE of what's wanted. That happens because vanilla's own
			// CommonItemData.Subtract/AddItem (see ItemStorage.RemoveItem/AddItem) treat whatever
			// CurrentCharges value the moved representative carries as the literal amount spent from
			// the real pool - so a representative cloned with the real (unmodified) partial charge
			// value always attributes the move to the truly-partial unit specifically. Clamping the
			// transferable count down to the guaranteed-full headroom (reserving exactly 1 unit for
			// the pool's real partial remainder, mirroring ChaosVehicleAssignment.Adjust's own
			// invariant) and forcing the moved representative's charges to ChargesMax makes vanilla's
			// own math always attribute the move to a full unit instead, leaving the true partial
			// fraction behind - the one partial unit therefore can never move to ANY destination, base
			// or aircraft alike, only genuinely full copies from the same stack ever can.
			bool forceFullCharges = false;
			if (from.Kind == LocationKind.GlobalUntagged && IsPooledChargeItem(itemDef))
			{
				GeoItem realGlobal;
				if (faction?.ItemStorage != null && faction.ItemStorage.Items.TryGetValue(itemDef, out realGlobal)
					&& realGlobal.CommonItemData.Count > 0 && realGlobal.CommonItemData.CurrentCharges != itemDef.ChargesMax)
				{
					int tagged = ChaosVehicleAssignment.GetTotalAssignedAcrossVehicles(faction, itemDef);
					int remainder = realGlobal.CommonItemData.Count - tagged;
					int availableFull = Math.Max(0, remainder - 1);
					if (count > availableFull)
					{
						Log.Debug($"TransferBetweenLocations: '{itemDef.name}' move from Global clamped {count} -> {availableFull} to keep its one genuine partial-charge unit ({realGlobal.CommonItemData.CurrentCharges}/{itemDef.ChargesMax}) behind.");
						count = availableFull;
					}
					forceFullCharges = true;
				}
			}
			if (count <= 0)
			{
				Log.Debug($"TransferBetweenLocations: '{itemDef.name}' move from '{from.Kind}' refused - no genuinely full unit available (only the partial-charge remainder is left).");
				return;
			}

			ItemStorage physFrom = PhysicalStorageOf(from);
			ItemStorage physTo = PhysicalStorageOf(to);
			if (physFrom != physTo)
			{
				// Look up the real item first so its actual charges/malfunction state (see
				// BuildDisplayItem's own comment) survives the move instead of being replaced by a
				// blank (ChargesMax) synthesis, and explicitly split any loaded magazine out into its
				// own separate transfer (see ExtractLoadedAmmo's own comment for why this mod does the
				// weapon/magazine split itself instead of relying on ItemStorage.AddItem's own
				// auto-unload side effect).
				GeoItem real = null;
				physFrom?.Items.TryGetValue(itemDef, out real);
				List<GeoItem> magazines = real != null ? ExtractLoadedAmmo(real) : new List<GeoItem>();
				// Built from the SAME real item for both sides (a blank ChargesMax-assumed removal
				// item would silently destroy real charges for a CombineWhenStacking stack - see
				// removeRepresentative's comment in TryCustomUpdateStorageCore for the concrete,
				// user-confirmed mechanism). chargesOverride forces ChargesMax only when count was
				// already proven to exclude the pool's one true partial unit (see forceFullCharges
				// above) - never a guess.
				int? chargesOverride = forceFullCharges ? (int?)itemDef.ChargesMax : null;
				GeoItem toRemove = real != null ? BuildDisplayItem(real, count, chargesOverride) : new GeoItem(itemDef, count, -1, null, -100);
				GeoItem toAdd = real != null ? BuildDisplayItem(real, count, chargesOverride) : new GeoItem(itemDef, count, -1, null, -100);
				RemoveFromStorage(physFrom, toRemove);
				AddToStorage(physTo, toAdd);

				GeoVehicle claimVehicle = to.Kind == LocationKind.Vehicle
					? faction?.Vehicles.FirstOrDefault(v => v.VehicleID == to.VehicleId)
					: null;
				foreach (GeoItem magazine in magazines)
				{
					Log.Debug($"TransferBetweenLocations: extracted loaded '{magazine.CommonItemData.GetDisplayName()}' x{magazine.CommonItemData.Count} out of '{itemDef.name}' - moving it too.");
					AddToStorage(physTo, magazine);
					if (claimVehicle != null)
					{
						ChaosVehicleAssignment.Adjust(claimVehicle, magazine.ItemDef, magazine.CommonItemData.Count);
					}
				}
			}
			if (from.Kind == LocationKind.Vehicle)
			{
				AdjustVehicleAssignment(faction, from.VehicleId, itemDef, -count);
			}
			if (to.Kind == LocationKind.Vehicle)
			{
				AdjustVehicleAssignment(faction, to.VehicleId, itemDef, count);
			}
		}

		private static UIModuleSoldierEquip GetModule(GeoscapeViewState state)
		{
			if (state is UIStateEditSoldier)
			{
				return SoldierEquipModuleProp_Soldier?.GetValue(state) as UIModuleSoldierEquip;
			}
			if (state is UIStateEditVehicle)
			{
				return SoldierEquipModuleProp_Vehicle?.GetValue(state) as UIModuleSoldierEquip;
			}
			return null;
		}

		// Hides "EQUIP LOADOUT"/"SAVE LOADOUT" (and any other exact-matching label) on the Edit
		// Soldier screen at the user's request. These buttons belong to some other installed mod,
		// not vanilla Assembly-CSharp (confirmed absent from ModSDK/Assembly-CSharp.dll's own
		// identifier strings) and not something this mod has compile-time access to, so they can't
		// be Harmony-patched by type/method the normal way - instead this finds them at runtime by
		// their exact displayed label text and disables their GameObject. Searched once (cached by
		// GameObject reference) and re-searched only if a cached reference goes null (e.g. the
		// screen's UI was torn down and rebuilt), then re-applied every frame from the UpdateState
		// Postfix in case whatever mod owns them re-enables them on its own update tick.
		private static readonly List<GameObject> _hiddenThirdPartyButtons = new List<GameObject>();
		private static readonly string[] ThirdPartyButtonLabelsToHide = { "EQUIP LOADOUT", "SAVE LOADOUT" };

		// All 4 buttons visible in the same screenshot the user originally described (only 2 of
		// which should be hidden) - used as boundary markers below, not as hide targets themselves.
		private static readonly string[] AllFourButtonLabels = { "UNEQUIP ALL GEAR", "EQUIP LOADOUT", "SAVE LOADOUT", "SHOW FACE" };

		public static void HideThirdPartyLoadoutButtons(GeoscapeViewState state)
		{
			UIModuleSoldierEquip module = GetModule(state);
			Transform root = module?.StorageList?.transform?.root;
			if (root == null)
			{
				return;
			}

			bool needsSearch = _hiddenThirdPartyButtons.Count == 0 || _hiddenThirdPartyButtons.Any(go => go == null);
			if (needsSearch)
			{
				_hiddenThirdPartyButtons.Clear();

				// Two prior attempts both failed with concrete Player.log evidence: hiding the
				// label's immediate parent only hid the text (the button icon lives further up),
				// and proximity search against Selectable/PhoenixGeneralButton kept picking a
				// completely unrelated button (a "MutationAnimArtButton", nowhere near these) -
				// meaning transform.position isn't a reliable stand-in for true screen position
				// here, or these buttons use neither component type at all. Neither
				// hierarchy-depth-guessing nor position-guessing worked, so this instead climbs
				// the label's ancestor chain based on CONTENT: keep going up as long as that
				// ancestor's subtree doesn't also contain one of the OTHER 3 buttons' labels (see
				// FindWidestExclusiveAncestor) - the widest container still exclusive to this one
				// button should include its icon as well as its label, without ever reaching into
				// a sibling button's territory, regardless of how deep the icon actually sits.
				foreach (Text text in root.GetComponentsInChildren<Text>(true))
				{
					if (text == null || string.IsNullOrEmpty(text.text))
					{
						continue;
					}
					string trimmed = text.text.Trim();
					if (ThirdPartyButtonLabelsToHide.Any(label => string.Equals(trimmed, label, StringComparison.OrdinalIgnoreCase)))
					{
						GameObject target = FindWidestExclusiveAncestor(text, trimmed);
						_hiddenThirdPartyButtons.Add(target);
						Log.Debug($"HideThirdPartyLoadoutButtons: found label '{trimmed}' - hiding widest exclusive ancestor '{target.name}'.");
					}
				}
			}

			foreach (GameObject go in _hiddenThirdPartyButtons)
			{
				if (go != null && go.activeSelf)
				{
					go.SetActive(false);
				}
			}
		}

		private static GameObject FindWidestExclusiveAncestor(Text text, string ownLabel)
		{
			Transform best = text.transform;
			Transform current = text.transform.parent;
			int safety = 0;
			while (current != null && safety < 12)
			{
				safety++;
				bool containsOtherButtonLabel = current.GetComponentsInChildren<Text>(true).Any(t =>
					t != text && !string.IsNullOrEmpty(t.text)
					&& AllFourButtonLabels.Any(label => !string.Equals(label, ownLabel, StringComparison.OrdinalIgnoreCase)
						&& string.Equals(t.text.Trim(), label, StringComparison.OrdinalIgnoreCase)));
				if (containsOtherButtonLabel)
				{
					break;
				}
				best = current;
				current = current.parent;
			}
			return best.gameObject;
		}

		// --- Per-screen-instance bookkeeping for the merged view ---------------------------------
		// Tracks, for each currently-open Edit Soldier screen, which real StorageEntry backs each
		// GeoItem reference last fed into the Storage list - needed so the next commit
		// (TryCustomUpdateStorage) knows which real storage each shown item actually belongs to, and
		// so partial-quantity changes can be reconciled by count delta rather than assuming a whole
		// stack moved.
		private static readonly ConditionalWeakTable<GeoscapeViewState, Dictionary<GeoItem, StorageEntry>> _lastEntries =
			new ConditionalWeakTable<GeoscapeViewState, Dictionary<GeoItem, StorageEntry>>();

		private static Dictionary<GeoItem, StorageEntry> GetLastEntries(GeoscapeViewState state)
		{
			Dictionary<GeoItem, StorageEntry> dict;
			return _lastEntries.TryGetValue(state, out dict) ? dict : new Dictionary<GeoItem, StorageEntry>(GeoItemReferenceComparer.Instance);
		}

		private static void SetLastEntries(GeoscapeViewState state, Dictionary<GeoItem, StorageEntry> entries)
		{
			_lastEntries.Remove(state);
			if (entries != null)
			{
				_lastEntries.Add(state, entries);
			}
		}

		public static void ClearLocationState(GeoscapeViewState state)
		{
			_lastEntries.Remove(state);
			_lastGoodCtx.Remove(state);
			UIModuleSoldierEquip module = GetModule(state);
			if (module?.StorageList != null)
			{
				ChaosPerSlotAircraftLabel.HideAll(module.StorageList);
			}
		}

		/// Builds one StorageEntry per item across every location: reachable ones (current context,
		/// its destinations, global pool) first, then everything else across every base/aircraft the
		/// faction has, so nothing is hidden even if it can't currently be touched.
		private static List<StorageEntry> BuildAllEntries(LocationContext ctx)
		{
			List<StorageEntry> entries = new List<StorageEntry>();
			List<StorageOption> reachableOptions = GetReachableLocations(ctx);
			HashSet<LocationKey> reachableKeys = new HashSet<LocationKey>(reachableOptions.Select(o => o.Key));

			// Global Inventory items are only unconditionally equipable if they can be manufactured
			// instantly (ItemDef.IsInstant) - everything else additionally requires the current
			// context to actually be at, or an aircraft docked to, a player base. This mirrors the
			// existing rule already enforced when MOVING an item out of Global Inventory via the
			// right-click menu (see TryShowStorageItemContextMenu) - applied here to whether the item
			// can be equipped directly while still sitting in Global Inventory. ctx.Destinations only
			// ever contains a base entry when actually docked to a player base (GetDockedStorageOptions
			// only adds it then), so reusing it here avoids recomputing the docked check separately.
			bool atOrDockedToBase = ctx.IsBase || ctx.Destinations.Any(d => d.IsBase);

			foreach (StorageOption option in reachableOptions)
			{
				if (option.Key.Kind == LocationKind.GlobalUntagged)
				{
					AddEntriesFromLocation(entries, option.Key, option.Label, item => IsInstantManufacture(item.ItemDef) || atOrDockedToBase);
				}
				else
				{
					AddEntriesFromLocation(entries, option.Key, option.Label, item => true);
				}
			}

			foreach (GeoSite site in ctx.Faction.Sites.Where(s => IsPlayerBase(ctx.Faction, s)))
			{
				LocationKey key = LocationKey.ForBase(site);
				if (site.ItemStorage != null && !reachableKeys.Contains(key))
				{
					AddEntriesFromLocation(entries, key, site.SiteName.Localize(null), item => false);
				}
			}
			foreach (GeoVehicle v in ctx.Faction.Vehicles)
			{
				LocationKey key = LocationKey.ForVehicle(v);
				if (!reachableKeys.Contains(key))
				{
					AddEntriesFromLocation(entries, key, v.Name, item => false);
				}
			}

			// Per explicit user request: within each of the three tiers ApplySlotTags already colors
			// (white/own-location, light-blue/reachable-elsewhere, red/unreachable), sort most
			// valuable first - same ManufacturePrice-based metric already used to order the Air Force
			// tab's cargo cards (ChaosAircraftItemsDisplay.GetItemValue). OrderBy/ThenByDescending are
			// stable, so entries of equal tier+value keep their original (location-grouped) order.
			return entries
				.OrderBy(e => GetSortTier(e, ctx.Key))
				.ThenByDescending(e => ChaosAircraftItemsDisplay.GetItemValue(e.Item.ItemDef))
				.ToList();
		}

		/// 0 = white (belongs to the currently selected base/aircraft), 1 = light-blue (equipable but
		/// sourced elsewhere), 2 = red (unreachable) - mirrors ApplySlotTags's own color decision
		/// exactly, so the sort tiers and the displayed colors can never disagree.
		private static int GetSortTier(StorageEntry entry, LocationKey ownLocation)
		{
			if (!entry.Reachable)
			{
				return 2;
			}
			return entry.Location.Equals(ownLocation) ? 0 : 1;
		}

		private static void AddEntriesFromLocation(List<StorageEntry> entries, LocationKey key, string label, Func<GeoItem, bool> reachablePredicate)
		{
			ItemStorage physical = PhysicalStorageOf(key);
			foreach (GeoItem item in GetDisplayItems(key))
			{
				entries.Add(new StorageEntry
				{
					Item = item,
					LocationLabel = label,
					Reachable = reachablePredicate(item),
					SourceStorage = physical,
					Location = key,
					OriginalCount = item.CommonItemData.Count,
					OriginalCharges = item.CommonItemData.CurrentCharges
				});
			}
		}

		/// <summary>
		/// Full replacement for UIStateEditSoldier.RefreshStorage() when the soldier is tracked by
		/// this mod. Feeds the merged multi-location item list into the Storage list with stacking
		/// disabled (UIInventoryList.AllowStacking gates both StackItems() and
		/// HandleAddingToSlotStacking's cross-item merge-by-ItemDef, confirmed in the
		/// source - with it off, items from different locations that happen to share an ItemDef land
		/// in separate slots instead of merging into one indistinguishable stack), then tags each
		/// populated slot with its owning location and reachable/unreachable state. Returns false
		/// (meaning "let vanilla handle it") when the character isn't tracked by this mod at all.
		/// </summary>
		// Guarded by the same shared _reconciling flag as TryCustomUpdateStorage (see its comment for
		// why): module.UpdateData(...) below was confirmed via Player.log to synchronously trigger a
		// nested UpdateStorage() commit before this method ever reaches its own SetLastEntries call,
		// which made every currently-shown item look brand-new to that nested commit and duplicately
		// added all of them into real storage. Wrapping this whole method in the guard means that
		// nested commit is now a no-op instead.
		public static bool TryCustomRefreshStorage(GeoscapeViewState state)
		{
			if (_reconciling)
			{
				Log.Debug("TryCustomRefreshStorage: re-entrant call detected while a refresh/commit was already in progress - skipping to avoid double-processing/duplication.");
				return true;
			}
			_reconciling = true;
			try
			{
				return TryCustomRefreshStorageCore(state);
			}
			finally
			{
				_reconciling = false;
			}
		}

		private static bool TryCustomRefreshStorageCore(GeoscapeViewState state)
		{
			UIModuleSoldierEquip module = GetModule(state);
			LocationContext ctx = ResolveLocationContext(state);
			if (module == null || module.StorageList == null || ctx == null)
			{
				return false;
			}

			module.StorageList.AllowStacking = false;
			// UIInventoryList.ApplyFilter() re-sorts FilteredItems by ItemDef.ViewElementDef.
			// DisplayPriority whenever OrderList is true (confirmed in the source),
			// which silently discarded the location-based order BuildAllEntries constructs below
			// (current location's own items first, then other reachable locations, then
			// unreachable ones) - disabling it here preserves that order through to the slots.
			module.StorageList.OrderList = false;
			// UIInventoryList.SetItems() - called by UpdateData below - silently strips any
			// partially-charged stack item (a "half loaded" magazine/ammo) out of UnfilteredItems
			// and into a SEPARATE, untracked StorageList.PartialMagazines list whenever
			// ShouldHidePartialMagazines is true (set by vanilla's own EnterState). Confirmed in the
			// HidePartialMagazines(): for a stack with Count > 1, it SUBTRACTS the
			// partial portion directly from the very GeoItem clone this mod's reconciliation is
			// tracking as a StorageEntry, silently changing its Count out from under
			// TryCustomUpdateStorage - which then reads the mutated Count as if the player had
			// manually removed that many, and "corrects" the real storage by that same amount even
			// though nothing was actually moved. This is the likely cause of the reported
			// stacked-ammo item loss. Disabling it here means partial-charge items stay normal,
			// whole slot entries (still visually showing their partial charge via the slot's own
			// existing ammo-bar rendering) instead of being silently split into an untracked bucket
			// this mod's reconciliation has no idea exists.
			module.StorageList.ShouldHidePartialMagazines = false;

			// UIInventorySlot.CanAcceptItem gates on Compatible, which folds in
			// "!EventHandlersEnabled" (see Incompatible's getter) - so any slot ApplySlotTags left
			// disabled for a red/unreachable item on the PREVIOUS refresh is still disabled right now,
			// before this refresh's own ApplySlotTags call ever gets a chance to fix it. UpdateData
			// below (-> UIInventoryList.Init -> ApplyFilter -> UpdateList) places every item via
			// GetFirstAvailableSlot, which skips any slot that fails CanAcceptItem entirely, for ANY
			// item - a stale-disabled slot therefore isn't just wrong-colored, it silently refuses to
			// receive ANY item this pass, and once every OTHER slot is claimed by an earlier item in
			// the list, whichever items are left with nowhere to go are just dropped from display
			// (still really in storage, just not shown) - exactly the "randomly empty slots" symptom.
			// Resetting every slot to enabled here, before placement runs, means the only slots that
			// end up disabled after this refresh are the ones ApplySlotTags deliberately disables for
			// THIS refresh's own red items - never a leftover from the last one.
			foreach (UIInventorySlot staleSlot in module.StorageList.GetComponentsInChildren<UIInventorySlot>(true))
			{
				staleSlot.EventHandlersEnabled = true;
			}

			List<StorageEntry> entries = BuildAllEntries(ctx);
			List<ICommonItem> feed = entries.Select(e => (ICommonItem)e.Item).ToList();
			module.UpdateData(null, null, null, feed, 0);

			Dictionary<GeoItem, StorageEntry> byItem = new Dictionary<GeoItem, StorageEntry>(GeoItemReferenceComparer.Instance);
			foreach (StorageEntry e in entries)
			{
				byItem[e.Item] = e;
			}

			object font = ChaosContextMenu.GetFont(module.StorageLabelTextComp);
			ApplySlotTags(state, module, byItem, ctx.Key, font);
			// Restored to vanilla's own display logic (UIStateEditSoldier itself calls
			// SetStorageIndicatorUI(GetUsedStorage(), _faction.GetTotalAvailableStorage()) at every
			// refresh) instead of the hardcoded int.MaxValue this mod previously substituted - purely
			// cosmetic, per explicit user request. Aircraft storage staying genuinely uncapped is
			// enforced separately, at the ItemStorage level (Patch_ItemStorage_GetStorageUsed_
			// ExcludeVehicleClaims / Patch_ItemStorage_TrimStorageToCapacity_ProtectVehicleClaims), so
			// showing the faction's real total here doesn't reintroduce any actual limit.
			module.SetStorageIndicatorUI(GetLocationStorageUsed(ctx), ctx.Faction.GetTotalAvailableStorage());
			SetLastEntries(state, byItem);
			return true;
		}

		/// "Used storage" for the indicator bar - for a vehicle context this is the vehicle's own
		/// claimed total (never the whole faction's real usage, which is what ctx.Storage now
		/// physically represents for Vehicle/GlobalUntagged contexts), for a base context it's still
		/// that base's own real, separate ItemStorage usage, unchanged.
		private static int GetLocationStorageUsed(LocationContext ctx)
		{
			return ctx.Key.Kind == LocationKind.Vehicle && ctx.Vehicle != null
				? ChaosVehicleAssignment.GetVehicleStorageUsed(ctx.Vehicle)
				: ctx.Storage.GetStorageUsed();
		}

		/// Fallback hover text shown for a red/unreachable Storage slot on the soldier/vehicle equip
		/// screens, used only when the item's real location couldn't be resolved (see
		/// BuildUnreachableTooltipText below for the normal, location-aware wording). A method
		/// rather than a const string (as it was before localization) since it needs to re-resolve
		/// through I2 Loc every call, not bake in a fixed English value at compile time.
		private static string UnreachableTooltipText()
		{
			return ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_DOCK_TO_ANY_BASE");
		}

		/// <summary>
		/// Per explicit user request (cosmetic wording fix, v90, extended per follow-up request): the
		/// generic "Dock to any base to equip" hint is only actually accurate for an item sitting in
		/// the untagged Global remainder - it was ALSO shown, misleadingly, for an item sitting at a
		/// SPECIFIC base or in a DIFFERENT aircraft's own claimed cargo, when the real fix is docking
		/// at that one specific base/aircraft, not "any base". Names the specific base or aircraft in
		/// both of those cases now; only the true Global case still falls through to the generic text.
		/// </summary>
		private static string BuildUnreachableTooltipText(LocationKey location, string locationLabel)
		{
			return (location.Kind == LocationKind.Vehicle || location.Kind == LocationKind.Base)
				? ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_DOCK_TO_LOCATION", locationLabel)
				: UnreachableTooltipText();
		}

		/// <summary>
		/// Resolves the live GeoActor (GeoSite for a base, GeoVehicle for an aircraft) a given
		/// LocationKey refers to, for camera/selection navigation - see NavigateToLocation. Returns
		/// null for LocationKind.GlobalUntagged (nothing specific to navigate to) or if the vehicle no
		/// longer exists (e.g. lost/sold since the entry was captured).
		/// </summary>
		private static GeoActor ResolveActorForLocation(LocationKey location)
		{
			switch (location.Kind)
			{
				case LocationKind.Base:
					return location.Site;
				case LocationKind.Vehicle:
					return location.Faction?.Vehicles.FirstOrDefault(v => v.VehicleID == location.VehicleId);
				default:
					return null;
			}
		}

		// GeoscapeViewState.Context is declared "private protected GeoscapeViewContext Context
		// { protected get; private set; }" on the shared base class (confirmed in
		// source) - not accessible directly from this mod's own static utility class, same reasoning
		// as GeoscapeModulesProp above. GeoscapeViewContext.View itself is a public field, so no
		// further reflection is needed once Context is read.
		private static readonly PropertyInfo ContextProp =
			typeof(GeoscapeViewState).GetProperty("Context", BindingFlags.NonPublic | BindingFlags.Instance);

		/// <summary>
		/// Per explicit user request: clicking the "Dock to X..." hint row (see ShowUnreachableHintMenu)
		/// should navigate the Geoscape to that specific base/aircraft, not just describe it. Reuses
		/// GeoscapeView.SelectActorAndVehicle - the same public API vanilla's own vehicle-cycle/roster
		/// selection already calls - which both clears back out of the current equip screen's state
		/// stack (ClearStackAndPush) and chase-pans the camera to the target actor, so this doubles as
		/// "close this screen and take me there" in one call. Does nothing (silently) for a
		/// GlobalUntagged location (ResolveActorForLocation returns null - there's no single specific
		/// place to navigate to for the untagged remainder).
		/// </summary>
		private static void NavigateToLocation(GeoscapeViewState state, LocationKey location)
		{
			GeoActor actor = ResolveActorForLocation(location);
			if (actor == null)
			{
				Log.Debug($"NavigateToLocation: nothing to navigate to for location.Kind={location.Kind}.");
				return;
			}
			GeoscapeViewContext context = ContextProp?.GetValue(state) as GeoscapeViewContext;
			GeoscapeView view = context?.View;
			if (view == null)
			{
				Log.Debug("NavigateToLocation: couldn't resolve GeoscapeView from state.");
				return;
			}
			Log.Debug($"NavigateToLocation: navigating to '{actor}'.");
			view.SelectActorAndVehicle(actor, true);
		}

		/// <summary>
		/// Shared "red/unreachable item" hint menu, used identically by both the right-click path
		/// (TryShowStorageItemContextMenu's own EventHandlersEnabled=false branch) and the left-click
		/// path (SetUnreachableTooltip's ChaosClickHandler) - per explicit user request for both
		/// buttons to behave exactly the same way. Replaces the old, separately-buggy
		/// ChaosItemNameTooltip box with the same ChaosContextMenu the working blue/white "Move to..."
		/// menu already uses. The single option row shows the location-aware hint text
		/// (BuildUnreachableTooltipText) and, whenever the source location resolves to an actual
		/// specific base or aircraft, is clickable to navigate the Geoscape straight there.
		/// </summary>
		private static void ShowUnreachableHintMenu(GeoscapeViewState state, Transform parentTransform, object font, LocationKey? location, string locationLabel)
		{
			string hintText = location.HasValue
				? BuildUnreachableTooltipText(location.Value, locationLabel)
				: UnreachableTooltipText();
			// Global Inventory has no single specific place to navigate to, so the hint row stays a
			// plain non-clickable line in that case (OnClick left null) - same as it already is when
			// the source location couldn't be resolved at all.
			Action navigate = location.HasValue && location.Value.Kind != LocationKind.GlobalUntagged
				? (Action)(() => NavigateToLocation(state, location.Value))
				: null;
			List<ChaosContextMenu.Option> hintOptions = new List<ChaosContextMenu.Option>
			{
				new ChaosContextMenu.Option { Label = hintText, TextColor = ChaosPerSlotAircraftLabel.UnreachableColor, OnClick = navigate }
			};
			// Per explicit user follow-up request: the gray title row (the aircraft/base name)
			// navigates there too, same as clicking the red hint text below it.
			ChaosContextMenu.Show(parentTransform, GetMousePosition(), locationLabel, font, hintOptions, navigate);
		}

		/// <summary>
		/// (Re-)applies the per-slot disabled state and location label for whatever item currently
		/// occupies each Storage slot. Needs to run after every commit as well as every refresh - not
		/// just at EnterState/CharacterChangedHandler - because drag-and-drop rearranges which item
		/// sits in which slot on every interaction, but only triggers a commit (TryCustomUpdateStorage),
		/// not a full refresh. Without this, a slot's tag would keep showing whatever item used to be
		/// there before the drag, since ConditionalWeakTable-based labels are keyed by slot identity,
		/// not by item identity.
		/// </summary>
		private static void ApplySlotTags(GeoscapeViewState state, UIModuleSoldierEquip module, Dictionary<GeoItem, StorageEntry> byItem, LocationKey ownLocation, object font)
		{
			Transform tooltipParent = module.StorageList.transform.root;
			foreach (UIInventorySlot slot in module.StorageList.Slots)
			{
				GeoItem item = slot.Item as GeoItem;
				StorageEntry entry;
				if (item != null && byItem.TryGetValue(item, out entry))
				{
					slot.EventHandlersEnabled = entry.Reachable;
					// White: belongs to the currently selected base/aircraft. Light blue: equipable
					// but actually sourced from a different (docked/global) location. Red: unreachable.
					Color color = !entry.Reachable
						? ChaosPerSlotAircraftLabel.UnreachableColor
						: (entry.Location.Equals(ownLocation)
							? ChaosPerSlotAircraftLabel.OwnLocationColor
							: ChaosPerSlotAircraftLabel.OtherReachableColor);
					ChaosPerSlotAircraftLabel.SetLabel(slot, entry.LocationLabel, color, font);
					SetUnreachableTooltip(slot, !entry.Reachable, tooltipParent, font, state, entry.Reachable ? (LocationKey?)null : entry.Location, entry.LocationLabel);
				}
				else
				{
					slot.EventHandlersEnabled = true;
					ChaosPerSlotAircraftLabel.SetLabel(slot, null, ChaosPerSlotAircraftLabel.OwnLocationColor, font);
					SetUnreachableTooltip(slot, false, tooltipParent, font, state, null, null);
				}
			}
		}

		/// <summary>
		/// Attaches (once per slot GameObject - reused across the repeated per-frame/per-commit
		/// ApplySlotTags calls, same reuse pattern as ChaosPerSlotAircraftLabel's ConditionalWeakTable)
		/// two independent behaviors for a red/unreachable Storage slot, per explicit user follow-up
		/// request:
		///
		/// 1. Left-click shows UnreachableTooltipText in red near the cursor (via ChaosClickHandler -
		///    a separate component alongside the real UIInventorySlot, unaffected by
		///    EventHandlersEnabled=false, same reasoning already proven for the Air Force tab's
		///    cargo-card double-click/hover handlers). Right-click is handled separately, in
		///    TryShowStorageItemContextMenu's own early-return branch for an unreachable slot - right
		///    click on this screen never reaches Unity's normal IPointerClickHandler dispatch at all,
		///    it's intercepted at the InputEvent "Cancel" level before that (see
		///    Patch_UIStateEditSoldier_OnInputEvent_SuppressRightClickCancel).
		///
		/// 2. Mouse-over restores the ORIGINAL, unmodified item-info tooltip (UIInventoryTooltip's
		///    stats panel, UIInventoryRollover's cursor-follow icon, etc.) instead of showing nothing.
		///    Every vanilla-registered OnPointerEnteredHandlers/OnPointerExitedHandlers callback gates
		///    on slot.Incompatible (== "_incompatible || !EventHandlersEnabled"), so as long as
		///    EventHandlersEnabled is false those callbacks always bail without this mod doing
		///    anything. Rather than reimplement that whole vanilla tooltip pipeline, ChaosHoverTooltipHandler
		///    briefly flips EventHandlersEnabled to true only for the synchronous duration of one
		///    manual slot.OnPointerEnter/OnPointerExit call - the same "flip a flag, let vanilla's own
		///    already-correct method run, flip it back" technique already trusted in this mod (the
		///    AllowStacking Harmony __state flip on UIInventoryList.RemoveItem). A genuine equip click
		///    is always a separate, later event, by which point the flag is already back to false, so
		///    this can never let an unreachable item actually be equipped.
		/// </summary>
		// `locked`: true for both genuinely unreachable (red) and blue-but-locked (pooled-charge/
		// ammo-type sourced elsewhere - see ApplySlotTags) items - drives the hover tooltip-restore,
		// harmless/beneficial either way. `showHintOnClick`: only true for genuinely unreachable
		// items - a blue-locked item is already reachable, so the "Dock to X to equip" hint would be
		// actively wrong; it keeps its real right-click "Move to..." menu instead (see
		// TryShowStorageItemContextMenu), just no left-click hint.
		// Tracks the reachability/location signature last actually wired onto each slot, so
		// SetUnreachableTooltip (called every frame - see ReapplySlotTags) can skip re-wiring when
		// nothing has changed since last frame. Fixes a real per-frame GC-churn source found during
		// an FPS investigation: every call below used to unconditionally reassign hover.OnHoverEnter/
		// OnHoverExit/click.OnClicked to freshly allocated closures, for every visible Storage slot,
		// every single frame this screen is open - even though a slot's reachability/location almost
		// never changes between two consecutive frames. Keyed the same reuse-across-frames way as
		// ChaosPerSlotAircraftLabel's own ConditionalWeakTable.
		private sealed class SlotTagWiring
		{
			public bool Applied;
			public bool Unreachable;
			public LocationKey? Location;
			public string LocationLabel;
		}

		private static readonly ConditionalWeakTable<UIInventorySlot, SlotTagWiring> _slotTagWiring =
			new ConditionalWeakTable<UIInventorySlot, SlotTagWiring>();

		private static void SetUnreachableTooltip(UIInventorySlot slot, bool unreachable, Transform tooltipParent, object font, GeoscapeViewState state, LocationKey? location, string locationLabel)
		{
			SlotTagWiring wiring;
			if (!_slotTagWiring.TryGetValue(slot, out wiring))
			{
				wiring = new SlotTagWiring();
				_slotTagWiring.Add(slot, wiring);
			}
			if (wiring.Applied && wiring.Unreachable == unreachable
				&& Equals(wiring.Location, location) && wiring.LocationLabel == locationLabel)
			{
				return;
			}
			wiring.Applied = true;
			wiring.Unreachable = unreachable;
			wiring.Location = location;
			wiring.LocationLabel = locationLabel;

			ChaosHoverTooltipHandler hover = slot.GetComponent<ChaosHoverTooltipHandler>();
			if (hover == null)
			{
				hover = slot.gameObject.AddComponent<ChaosHoverTooltipHandler>();
			}
			if (unreachable)
			{
				UIInventorySlot capturedSlot = slot;
				hover.OnHoverEnter = () =>
				{
					capturedSlot.EventHandlersEnabled = true;
					capturedSlot.OnPointerEnter(new PointerEventData(EventSystem.current));
					capturedSlot.EventHandlersEnabled = false;
				};
				hover.OnHoverExit = () =>
				{
					capturedSlot.EventHandlersEnabled = true;
					capturedSlot.OnPointerExit(new PointerEventData(EventSystem.current));
					capturedSlot.EventHandlersEnabled = false;
					// Deliberately does NOT also close ChaosContextMenu here (previously did, for
					// ChaosItemNameTooltip - carried over when the hint switched to ChaosContextMenu,
					// which turned out to be a real bug, not just a stale comment): the moment the
					// hint menu opens, its own backdrop becomes the topmost raycast target over this
					// very slot, which makes Unity's EventSystem immediately fire a real OnPointerExit
					// on this same ChaosHoverTooltipHandler - closing the menu that had JUST been
					// created, on the same frame, before the player could ever see or click it. This
					// was the actual cause of the "opens and immediately closes" bug. ChaosContextMenu
					// already closes itself correctly on its own (its full-screen backdrop closes on
					// any outside click, same as the working blue/white "Move to..." menu, which has
					// no hover-exit wiring at all and doesn't have this problem), so no extra close
					// call is needed - or safe - here.
				};
			}
			else
			{
				hover.OnHoverEnter = null;
				hover.OnHoverExit = null;
			}

			ChaosClickHandler click = slot.GetComponent<ChaosClickHandler>();
			if (click == null)
			{
				click = slot.gameObject.AddComponent<ChaosClickHandler>();
			}
			// Left-click now shows the exact same ChaosContextMenu hint menu as a right-click
			// (ShowUnreachableHintMenu, shared with TryShowStorageItemContextMenu) - per explicit
			// user request for both buttons to behave identically, replacing the old separate
			// ChaosItemNameTooltip box.
			click.OnClicked = unreachable
				? (Action)(() => ShowUnreachableHintMenu(state, tooltipParent, font, location, locationLabel))
				: null;
		}

		/// <summary>
		/// Re-applies the last-known per-slot tags every frame (see the UpdateState Postfix that
		/// calls this) to counter UIInventorySlot.OnEnable() silently resetting
		/// EventHandlersEnabled = true whenever a slot is (re-)enabled - notably when the Storage
		/// list's pooled/virtualized rows scroll back into view, entirely independent of any
		/// refresh/commit this mod triggers. Cheap enough to run unconditionally every frame (a
		/// property set and a label update per slot, mirroring the existing per-frame storage-
		/// indicator override).
		/// </summary>
		public static void ReapplySlotTags(GeoscapeViewState state)
		{
			ReapplySlotTags(state, ResolveLocationContext(state));
		}

		// Overload for callers (the per-frame UpdateState postfixes) that already resolved the
		// context this frame for another purpose (OverrideStorageIndicator) - avoids resolving it,
		// and everything GetDockedStorageOptions/GetDockedStorageOptionsForSite freshly allocate,
		// a second time on the same frame. Still resolved fresh every frame by whichever caller
		// asks first, same as always - only the redundant duplicate resolve is removed.
		public static void ReapplySlotTags(GeoscapeViewState state, LocationContext ctx)
		{
			UIModuleSoldierEquip module = GetModule(state);
			if (module == null || module.StorageList == null || ctx == null)
			{
				return;
			}
			object font = ChaosContextMenu.GetFont(module.StorageLabelTextComp);
			ApplySlotTags(state, module, GetLastEntries(state), ctx.Key, font);

			// v90 follow-up, per direct user report: the click-time fix above (Patch_
			// UIInventorySlotSideButton_OnSideButtonPressed_RespectReachability) corrects a stale
			// "+Ammo"/"+Item" side button's cached state at the moment it's clicked, but the HOVER
			// tooltip text is set inside RefreshState() itself and only reflects whatever the LAST
			// call to RefreshState() computed - so before the first correcting click (or after
			// reachability quietly changes for some other reason, e.g. a docked neighbor's storage
			// changing while this screen stays open), the tooltip could still read "Equip"
			// (MoveFromStorageTooltip, the ActionNeededFree wording) instead of the correct
			// manufacture-related wording, even though a click would already behave correctly.
			// Reuses the exact same "every frame" cadence already established for ApplySlotTags
			// above (which exists to counter an equivalent staleness problem for slot colors) rather
			// than only reactively fixing it at click time - RefreshSideButtons() is cheap (a
			// handful of buttons: LoadAmmo/AddAmmo/AddItem/Repair per equipped slot) and re-running
			// GetState() every frame is exactly what keeps its own Postfix's correction
			// (Patch_UIInventorySlotSideButton_GetState_RespectReachability) applied continuously
			// instead of only at the one moment a click happens to force it.
			module.RefreshSideButtons();
		}

		/// <summary>
		/// Full replacement for UIStateEditSoldier.UpdateStorage() when the soldier is tracked by this
		/// mod. Vanilla's own version diffs the Storage list's contents against a single ItemStorage -
		/// wrong here, since the list can show items from many different real storages simultaneously.
		/// Reconciles by comparing what's currently shown against the entries snapshot from the last
		/// refresh (or the last commit, since this rebuilds that snapshot every time too): items that
		/// vanished entirely (equipped onto the soldier) are removed from whichever real storage they
		/// actually came from; items whose remaining count dropped or rose (a partial stack was taken/
		/// returned) apply just that delta to their real source, using a freshly constructed GeoItem
		/// (the public GeoItem(ItemDef, count, ...) constructor, not Clone()+Subtract, to avoid any
		/// further undocumented method-signature risk) rather than the original reference; brand-new
		/// item references (freshly unequipped by the player) land in the CURRENT location, matching
		/// the pre-existing "unequip lands in current storage" behavior. This is the single riskiest
		/// piece of this mod - a wrong reconciliation could silently duplicate or lose items - and has
		/// not been verified in-game beyond careful reasoning about the source.
		/// </summary>
		// Re-entrancy guard, widened after a second, more severe manifestation of the same class of
		// bug was confirmed directly in Player.log (v37): right after "TryCustomRefreshStorage: ...
		// 39 item entries" logged, the very next line was "TryCustomUpdateStorage: ... lastEntries=0,
		// currentlyShown=39" - immediately followed by 39 "new item -> 'HELIOS 3'" lines, one for
		// EVERY currently-shown item across every location (Global Inventory, docked base, etc.), not
		// just genuinely new ones. Since TryCustomRefreshStorage only calls SetLastEntries at the very
		// end of its own body (well after this), lastEntries=0 immediately after a refresh that just
		// displayed 39 items is only possible if module.UpdateData(...) (called earlier in
		// TryCustomRefreshStorage, to feed the merged list into the real UIInventoryList) is itself
		// synchronously triggering a NESTED call into UpdateStorage() before the outer refresh ever
		// reaches its own SetLastEntries - so the nested commit sees an empty tracking dictionary and
		// treats every shown item, regardless of true source, as brand new - duplicately adding ALL of
		// them into whichever storage the current screen resolves to. This is a real, confirmed
		// duplication of real ItemStorage contents, not just a display glitch.
		//
		// The v36 guard below only protected against TryCustomUpdateStorage calling itself
		// (AddToStorage/RemoveFromStorage fire ItemAdded/StorageChanged events with subscribers this
		// mod doesn't control, which could call back into UpdateStorage() - that direction is real
		// too, per the v36 reasoning kept below) - it did not cover a nested UpdateStorage call
		// triggered from INSIDE TryCustomRefreshStorage. Both directions are now guarded by the same
		// shared flag, covering the whole "a merged-view rebuild or reconciliation is in progress"
		// window: any UpdateStorage commit that fires anywhere inside that window is a no-op (skip
		// vanilla too), since there is nothing meaningful to commit while the list is still being
		// rebuilt from truth.
		//
		// v36 reasoning (still valid for the update-calls-update direction): several split fragments
		// showed up as "known" in _lastEntries on the very first commit that ever saw them, without
		// ever passing through the "new item" branch below (the only place that calls AddToStorage) -
		// meaning they were tracked but never actually written to real storage. Since C#/Unity's game
		// loop is single-threaded, this is only possible via a nested call completing first and
		// calling SetLastEntries before the outer call's loop reaches that item.
		private static bool _reconciling;

		public static bool TryCustomUpdateStorage(GeoscapeViewState state)
		{
			if (_reconciling)
			{
				Log.Debug("TryCustomUpdateStorage: re-entrant call detected while an outer commit or refresh was already in progress - skipping to avoid double-processing/duplication.");
				return true;
			}
			_reconciling = true;
			try
			{
				return TryCustomUpdateStorageCore(state);
			}
			finally
			{
				_reconciling = false;
			}
		}

		private static bool TryCustomUpdateStorageCore(GeoscapeViewState state)
		{
			UIModuleSoldierEquip module = GetModule(state);
			LocationContext ctx = ResolveLocationContext(state);
			if (module == null || module.StorageList == null || ctx == null)
			{
				Log.Debug($"TryCustomUpdateStorage: falling through to VANILLA update - module={module != null}, storageList={module?.StorageList != null}, ctx={ctx != null}. If this soldier was shown via the custom merged view a moment ago, vanilla's own diff-based commit can duplicate items into whatever StorageItems() resolves to.");
				return false;
			}

			Dictionary<GeoItem, StorageEntry> lastEntries = GetLastEntries(state);
			// See v34 notes in this mod's CLAUDE.md for why this reads UnfilteredItems (the full
			// backing data) as the primary source, with Slots unioned in read-only just to also
			// catch a split-off stack fragment - never Slots alone (only reflects the visible/
			// instantiated window of a pooled/virtualized list).
			List<GeoItem> currentlyShown = module.StorageList.UnfilteredItems.OfType<GeoItem>()
				.Concat(module.StorageList.Slots.Select(s => s?.Item as GeoItem).Where(i => i != null))
				.Distinct(GeoItemReferenceComparer.Instance)
				.ToList();
			HashSet<GeoItem> stillShown = new HashSet<GeoItem>(currentlyShown, GeoItemReferenceComparer.Instance);

			// Net physical delta per (real backing ItemStorage, ItemDef), aggregated rather than
			// applied per-entry. This is the key structural improvement over the old design: a
			// shrink recorded against one location and a same-commit appearance at another that
			// share the SAME real backing (any combination of two vehicles, or a vehicle and the
			// untagged Global remainder, of the same faction) cancel out to a genuine no-op instead
			// of a redundant - and riskier - remove-then-add round trip against the real storage.
			// Only a change that nets to nonzero for a given real storage ever calls into the
			// reflection-based AddToStorage/RemoveFromStorage helpers at all.
			Dictionary<ItemStorage, Dictionary<ItemDef, int>> netPhysicalDelta = new Dictionary<ItemStorage, Dictionary<ItemDef, int>>();
			void AddPhysicalDelta(ItemStorage storage, ItemDef itemDef, int delta)
			{
				if (storage == null || delta == 0)
				{
					return;
				}
				Dictionary<ItemDef, int> perDef;
				if (!netPhysicalDelta.TryGetValue(storage, out perDef))
				{
					perDef = new Dictionary<ItemDef, int>();
					netPhysicalDelta[storage] = perDef;
				}
				int current;
				perDef.TryGetValue(itemDef, out current);
				perDef[itemDef] = current + delta;
			}

			// Tracks, per (real backing storage, ItemDef) with a net POSITIVE delta this commit, one
			// actual GeoItem reference whose real charges/malfunction state should be preserved when
			// the aggregated add below actually runs. Without this, the apply loop below only ever
			// had a bare (ItemDef, netCount) to work with and had to synthesize a brand-new, blank
			// GeoItem (ChargesMax charges) for the real write - discarding whatever the player's
			// actual dragged item had, which was the concrete cause of a loaded weapon's ammo
			// vanishing the moment it was unequipped into storage. Last-write-wins if more than one
			// distinct item nets to the same (storage, ItemDef) in a single commit (a rare case
			// vanilla's own single-GeoItem-per-ItemDef storage model can't fully represent either
			// way) - preserving ONE real item's state is still strictly better than none.
			// Vehicle-claim adjustments are collected here and only actually applied AFTER the
			// aggregated real AddToStorage/RemoveFromStorage calls below (see the loop right
			// before ApplySlotTags). ChaosVehicleAssignment.Adjust clamps a claim to whatever is
			// CURRENTLY sitting in real storage - so calling it for a brand-new item (e.g. a
			// soldier unequipping armor straight onto a vehicle) before that item's own real
			// AddToStorage call had actually happened would see a real total of 0 and immediately
			// clamp the fresh claim back down to 0, silently demoting the item to untagged Global
			// Inventory instead of the vehicle it was actually dropped into - exactly the bug
			// confirmed via Player.log (every "new item -> 'HELIOS 3'" line was followed by a
			// "clamping to real total 0" line for that same item).
			List<Action> pendingClaimAdjustments = new List<Action>();

			// Declared here (before RegisterAdd, which needs to write into it directly) rather than
			// at its historical spot right before the main currentlyShown loop - see RegisterAdd's
			// own comment for why it now needs early access to this.
			Dictionary<GeoItem, StorageEntry> updatedEntries = new Dictionary<GeoItem, StorageEntry>(GeoItemReferenceComparer.Instance);

			// Set by RegisterAdd whenever an item was extracted this commit (see its own comment for
			// the full history of why hand-patching the UI list for this specific case kept causing
			// fresh bugs). When true, this whole commit's incremental UI bookkeeping below is
			// superseded by a full TryCustomRefreshStorageCore call at the very end - the real-storage
			// writes and claim adjustments computed below still need to run first (RegisterAdd/
			// AddPhysicalDelta/pendingClaimAdjustments), but the DISPLAY gets rebuilt from truth
			// instead of patched by hand.
			//
			// Deliberately NOT "always full refresh" (a round of this mod's history briefly tried
			// that, to keep every other bucket's charge display in sync after any commit) - confirmed
			// via Player.log to cause a real re-entrant double-refresh: TWO complete BuildAllEntries/
			// BuildDisplayItem passes for the same ~40 items landed at the identical timestamp, with
			// "ResolveLocationContext: live resolution failed, reusing last-known-good context"
			// sitting right at the boundary between them - module.UpdateData's own internal cascade
			// re-entering this commit path before the first pass had even finished, exactly the class
			// of bug _reconciling exists to prevent, just reached through a different door (calling
			// TryCustomRefreshStorageCore unconditionally, every commit, rather than only when truly
			// needed). Confirmed by direct user report as the cause of a real weapon-duplication bug.
			// No longer needed for charge-correctness either: ChargesOverrideFor is now an
			// unconditional, always-true fact baked in at BuildDisplayItem construction time (see its
			// own comment), not something that needs re-evaluating after every unrelated commit.
			bool anyExtractionHappened = false;

			// True summed TotalAvailableCharges (ChargesMax*(Count-1)+Charges) across EVERY fragment
			// added to the same (storage, ItemDef) this commit - not just whichever one happened to be
			// remembered last as the "representative" below. Needed because a single weapon unequip can
			// extract MULTIPLE separately-partial loaded magazines of the SAME ItemDef in one commit
			// (e.g. a spare magazine at 1/5 plus another at 4/5 both loaded into the same weapon) - see
			// the apply loop's own comment for the concrete bug this fixes (a real "free extra magazine"
			// materializing from nowhere, confirmed via Player.log: 1+4=5 true charges recombined with
			// the OLD stack's own 3 into a naive Count=3/charges=2 - TotalAvailableCharges=12 - instead
			// of the correct Count=2/charges=3 - TotalAvailableCharges=8).
			Dictionary<ItemStorage, Dictionary<ItemDef, int>> addTrueChargesSum = new Dictionary<ItemStorage, Dictionary<ItemDef, int>>();
			void AddTrueCharges(ItemStorage storage, ItemDef itemDef, int trueCharges)
			{
				if (storage == null || itemDef == null)
				{
					return;
				}
				Dictionary<ItemDef, int> perDef;
				if (!addTrueChargesSum.TryGetValue(storage, out perDef))
				{
					perDef = new Dictionary<ItemDef, int>();
					addTrueChargesSum[storage] = perDef;
				}
				int current;
				perDef.TryGetValue(itemDef, out current);
				perDef[itemDef] = current + trueCharges;
			}

			Dictionary<ItemStorage, Dictionary<ItemDef, GeoItem>> addRepresentative = new Dictionary<ItemStorage, Dictionary<ItemDef, GeoItem>>();
			void RememberAddSource(ItemStorage storage, GeoItem representative)
			{
				if (storage == null || representative == null)
				{
					return;
				}
				Dictionary<ItemDef, GeoItem> perDefItem;
				if (!addRepresentative.TryGetValue(storage, out perDefItem))
				{
					perDefItem = new Dictionary<ItemDef, GeoItem>();
					addRepresentative[storage] = perDefItem;
				}
				perDefItem[representative.ItemDef] = representative;

				ItemDef itemDef = representative.ItemDef;
				if (itemDef.ChargesMax > 0 && itemDef.CombineWhenStacking)
				{
					int trueCharges = itemDef.ChargesMax * (representative.CommonItemData.Count - 1) + representative.CommonItemData.CurrentCharges;
					AddTrueCharges(storage, itemDef, trueCharges);
				}
			}

			// Same idea as addRepresentative, but for the REMOVAL side. Without this, every real
			// RemoveFromStorage call below used a blank `new GeoItem(itemDef, count, -1, null, -100)`
			// - and that -1 charges sentinel resolves to ChargesMax (see CommonItemData's ctor) - i.e.
			// removal always assumed a FULLY CHARGED unit was being taken out, regardless of what was
			// actually shown/removed. For a CombineWhenStacking item (e.g. a magazine stack, where
			// vanilla only tracks ONE shared partial-charge value for the whole stack - all other
			// units implicitly assumed full, per CommonItemData.TotalAvailableCharges), removing a
			// unit that was actually only partially charged while CommonItemData.Subtract's own
			// borrow arithmetic assumes a full one silently destroys the difference in total real
			// charges - confirmed via a concrete user-reported trace (21 total charges -> 18 -> 12
			// across two claim-splitting equip/unequip round trips of a partially-used magazine
			// stack, matching this exact mismatch arithmetically).
			//
			// Takes charges as a plain int, NOT a live GeoItem reference to clone from later - this
			// matters. Confirmed via Player.log: vanilla's own UIInventoryList.TryLoadItemWithItem
			// (the auto-reload-on-equip mechanic - equipping a weapon auto-loads compatible ammo out
			// of the Storage list) calls `ammoItem.GetSingleItem().Clone()` then
			// `ammoItem.CommonItemData.Subtract(thatClone)` DIRECTLY on a live, UI-tracked GeoItem
			// clone - and when that subtraction lands exactly on Count=0, CommonItemData.Subtract's
			// own ChargesMax-borrow arithmetic leaves a stale, meaningless _charges value behind (e.g.
			// a tracked clone last known at Count=1/charges=9 was found at Count=0/charges=12 by the
			// time this mod's own removal logic read it moments later - the live object had been
			// mutated by vanilla in between, entirely outside this mod's own reconciliation). Building
			// an isolated, disconnected GeoItem snapshot immediately (via the plain constructor, not a
			// stored reference to anything that could still be mutated later) avoids this class of bug
			// entirely - see its use at the "disappeared" removal branch, which now sources charges
			// from StorageEntry.OriginalCharges (captured before any such mutation could happen) rather
			// than the live tracked object.
			// Mirrors addTrueChargesSum above, for the removal side - the same multi-fragment
			// mismatch can in principle occur here too (e.g. two distinct partially-charged items of
			// the same ItemDef both shrinking/disappearing in one commit).
			Dictionary<ItemStorage, Dictionary<ItemDef, int>> removeTrueChargesSum = new Dictionary<ItemStorage, Dictionary<ItemDef, int>>();
			void AddRemoveTrueCharges(ItemStorage storage, ItemDef itemDef, int trueCharges)
			{
				if (storage == null || itemDef == null)
				{
					return;
				}
				Dictionary<ItemDef, int> perDef;
				if (!removeTrueChargesSum.TryGetValue(storage, out perDef))
				{
					perDef = new Dictionary<ItemDef, int>();
					removeTrueChargesSum[storage] = perDef;
				}
				int current;
				perDef.TryGetValue(itemDef, out current);
				perDef[itemDef] = current + trueCharges;
			}

			Dictionary<ItemStorage, Dictionary<ItemDef, GeoItem>> removeRepresentative = new Dictionary<ItemStorage, Dictionary<ItemDef, GeoItem>>();
			void RememberRemoveSource(ItemStorage storage, ItemDef itemDef, int count, int charges)
			{
				if (storage == null || itemDef == null || count <= 0)
				{
					return;
				}
				GeoItem representative = new GeoItem(itemDef, count, charges, null, -100);
				Dictionary<ItemDef, GeoItem> perDefItem;
				if (!removeRepresentative.TryGetValue(storage, out perDefItem))
				{
					perDefItem = new Dictionary<ItemDef, GeoItem>();
					removeRepresentative[storage] = perDefItem;
				}
				perDefItem[representative.ItemDef] = representative;

				if (itemDef.ChargesMax > 0 && itemDef.CombineWhenStacking)
				{
					int trueCharges = itemDef.ChargesMax * (count - 1) + charges;
					AddRemoveTrueCharges(storage, itemDef, trueCharges);
				}
			}

			// Registers a real item as being newly added to `storage` (physical delta + preserved
			// representative + deferred claim adjustment if this location is a vehicle), then
			// explicitly extracts any loaded magazine out of it and registers that as ITS OWN add too
			// - see ExtractLoadedAmmo's own comment for why this mod does the weapon/magazine split
			// itself rather than relying on ItemStorage.AddItem's own auto-unload side effect.
			//
			// SIMPLIFIED in v83, per direct user request ("is it possible to make it simpler"), after
			// v76-v82 each patched a fresh symptom of the same root cause: this method used to also
			// hand-maintain the VISIBLE UI list in place (inserting the extracted item directly,
			// merging it into a pre-existing displayed item to avoid duplicates, reassigning claim
			// ownership on merge, keeping Slots/UnfilteredItems in sync...) so the extracted item
			// would show up immediately without waiting for a full refresh. Every one of those was a
			// real, separately-confirmed bug (invisible until refresh / stale duplicate ghosts /
			// ownership silently staying split) - but they were all facets of one underlying problem:
			// hand-patching the live UI list can never fully keep pace with everything real storage
			// and the claim overlay are doing. `GetDisplayItems`/`BuildAllEntries` (used by every full
			// refresh) already computes the CORRECT merged/split view from truth every time - this
			// method no longer tries to replicate any part of that. It now does ONLY the real
			// bookkeeping (physical delta, representative for charge preservation, claim adjustment)
			// and sets `anyExtractionHappened`, which triggers a full TryCustomRefreshStorageCore call
			// at the very end of this commit (see below) - the exact same, already-proven-correct code
			// path a screen close/reopen uses - instead of this method trying to patch the UI by hand.
			void RegisterAdd(ItemStorage storage, LocationKey location, string label, GeoItem sourceItem, int count)
			{
				if (storage == null || sourceItem == null || count <= 0)
				{
					return;
				}
				GeoVehicle vehicle = location.Kind == LocationKind.Vehicle
					? location.Faction?.Vehicles.FirstOrDefault(v => v.VehicleID == location.VehicleId)
					: null;
				Log.Debug($"RegisterAdd: '{sourceItem.CommonItemData.GetDisplayName()}' x{count} charges={sourceItem.CommonItemData.CurrentCharges} -> '{label}'.");
				AddPhysicalDelta(storage, sourceItem.ItemDef, count);
				RememberAddSource(storage, sourceItem);
				if (vehicle != null)
				{
					// Deliberately claims ONLY the newly-extracted `count` - not whatever else of this
					// ItemDef might already be sitting elsewhere (e.g. unclaimed Global Inventory), even
					// though the real-storage write above may merge with it via ItemStorage.AddItem's own
					// count/charge-combining logic. v82/v84 once absorbed that pre-existing portion into
					// this vehicle's claim too, but per explicit user request ("I don't want to move from
					// blue, unload ammo only to existing inventory") that reached further than wanted -
					// unequipping ammo should only ever grow THIS vehicle's own existing claim by what was
					// actually just unequipped, never reach out and annex separate, already-untagged stock.
					ItemDef itemDef = sourceItem.ItemDef;
					pendingClaimAdjustments.Add(() => ChaosVehicleAssignment.Adjust(vehicle, itemDef, count));
				}
				anyExtractionHappened = true;

				foreach (GeoItem magazine in ExtractLoadedAmmo(sourceItem))
				{
					Log.Debug($"TryCustomUpdateStorage: extracted loaded '{magazine.CommonItemData.GetDisplayName()}' x{magazine.CommonItemData.Count} out of '{sourceItem.CommonItemData.GetDisplayName()}' into '{storage}' as its own item.");
					RegisterAdd(storage, location, label, magazine, magazine.CommonItemData.Count);
				}
			}

			// Items that disappeared entirely from the Storage list (equipped onto the soldier) -
			// always a real physical removal (the soldier's equipment is never itself an
			// ItemStorage), plus a claim decrement if the source was a vehicle.
			foreach (KeyValuePair<GeoItem, StorageEntry> kv in lastEntries)
			{
				if (!stillShown.Contains(kv.Key))
				{
					Log.Debug($"TryCustomUpdateStorage: '{kv.Key.CommonItemData.GetDisplayName()}' x{kv.Value.OriginalCount} disappeared from '{kv.Value.LocationLabel}' - equipped, removing full count from source (snapshotted charges={kv.Value.OriginalCharges}, live charges={kv.Key.CommonItemData.CurrentCharges}).");
					AddPhysicalDelta(kv.Value.SourceStorage, kv.Key.ItemDef, -kv.Value.OriginalCount);
					RememberRemoveSource(kv.Value.SourceStorage, kv.Key.ItemDef, kv.Value.OriginalCount, kv.Value.OriginalCharges);
					if (kv.Value.Location.Kind == LocationKind.Vehicle)
					{
						int vehicleId = kv.Value.Location.VehicleId;
						ItemDef itemDef = kv.Key.ItemDef;
						int originalCount = kv.Value.OriginalCount;
						pendingClaimAdjustments.Add(() => AdjustVehicleAssignment(ctx.Faction, vehicleId, itemDef, -originalCount));
					}
				}
			}

			foreach (GeoItem item in currentlyShown)
			{
				StorageEntry entry;
				bool known = lastEntries.TryGetValue(item, out entry);
				ItemStorage source = known ? entry.SourceStorage : ctx.Storage;
				LocationKey location = known ? entry.Location : ctx.Key;
				string label = known ? entry.LocationLabel : ctx.Label;
				bool reachable = known ? entry.Reachable : true;

				if (known)
				{
					int delta = entry.OriginalCount - item.CommonItemData.Count;
					if (delta != 0)
					{
						// Still shown, but count changed - by the same convention as the "new item"
						// branch below, the difference is assumed to have moved to the CURRENT
						// context: dragging part of a stack elsewhere within this merged Storage
						// list is this mod's only affordance for "assign N units of an item here."
						Log.Debug($"TryCustomUpdateStorage: '{item.CommonItemData.GetDisplayName()}' count changed {entry.OriginalCount}->{item.CommonItemData.Count} in '{label}'.");
						// Count DECREASED (delta > 0) used to ALWAYS assume "peeled off `delta` whole
						// FULL-charge units, the shared partial charge untouched" (a blank `-1 charges
						// -> ChargesMax` representative per removed unit). That's correct for vanilla's
						// OWN reload button (Subtract with a Count=1/charges=ChargesMax token cleanly
						// peels a full unit) - but confirmed WRONG for a second, real mechanism: vanilla's
						// own CommonItemData.ModifyCharges (called directly by TFTV's laser-ammo-share
						// reload logic, but not TFTV-specific - it's a real vanilla method) reduces
						// CurrentCharges by an arbitrary amount and, if that drops to <=0, borrows a
						// single WHOLE unit's worth back (`_count--; _charges+=ChargesMax;`) to stay
						// canonical - so a SMALL partial consumption (e.g. -2 out of a 2/6+1-full stack)
						// can ALSO decrement Count by 1, even though nowhere near a full ChargesMax was
						// actually consumed. The blank-ChargesMax assumption then over-removed a full
						// unit's worth from real storage for what was actually a small partial draw -
						// confirmed via Player.log/user report ("2/6 + 1 full" i.e. Count=2/Charges=2,
						// TotalAvailableCharges=8, becoming "4/6" i.e. Count=1/Charges=4 after one
						// equip/unequip cycle - only 2 charges should ever have been consumed by that
						// weapon's own reload cost, not the 6 this mod's old blind assumption removed).
						//
						// Fixed by computing the TRUE amount consumed via TotalAvailableCharges
						// conservation (ChargesMax*(Count-1)+CurrentCharges) instead of assuming a fixed
						// ChargesMax per removed unit - this is a strict generalization, not a special
						// case: when charges genuinely are unchanged (the original, already-validated
						// reload-button scenario), this formula still resolves to exactly
						// `delta * ChargesMax`, identical to the old blank-synthesis token - verified by
						// hand for both scenarios before writing this.
						int chargesMax = item.ItemDef.ChargesMax;
						if (delta > 0 && chargesMax > 0)
						{
							int originalTotal = chargesMax * (entry.OriginalCount - 1) + entry.OriginalCharges;
							int currentTotal = chargesMax * (item.CommonItemData.Count - 1) + item.CommonItemData.CurrentCharges;
							int trueConsumed = originalTotal - currentTotal;
							int representativeCharges = Math.Max(0, trueConsumed - (delta - 1) * chargesMax);
							Log.Debug($"TryCustomUpdateStorage: '{item.CommonItemData.GetDisplayName()}' true charges consumed = {trueConsumed} (not the blind {delta * chargesMax} assumption) - removing x{delta} at charges={representativeCharges} from real storage.");
							RememberRemoveSource(source, item.ItemDef, delta, representativeCharges);
						}
						AddPhysicalDelta(source, item.ItemDef, -delta);
						if (delta < 0)
						{
							// Count INCREASED (more units of this real item just arrived here) - the
							// live `item` reference is exactly what should be preserved into real
							// storage, so hand it to the apply loop below instead of a blank synthesis,
							// and split any loaded ammo it carries out into its own tracked add too.
							RememberAddSource(source, item);
							foreach (GeoItem magazine in ExtractLoadedAmmo(item))
							{
								Log.Debug($"TryCustomUpdateStorage: extracted loaded '{magazine.CommonItemData.GetDisplayName()}' x{magazine.CommonItemData.Count} out of '{item.CommonItemData.GetDisplayName()}' into '{label}' as its own item.");
								RegisterAdd(source, location, label, magazine, magazine.CommonItemData.Count);
							}
						}
						if (location.Kind == LocationKind.Vehicle)
						{
							int vehicleId = location.VehicleId;
							ItemDef itemDef = item.ItemDef;
							int capturedDelta = delta;
							pendingClaimAdjustments.Add(() => AdjustVehicleAssignment(ctx.Faction, vehicleId, itemDef, -capturedDelta));
						}
					}
					else
					{
						// Count UNCHANGED, but charges may have drifted - vanilla's own TryLoadItemWithItem
						// (the auto-reload-on-equip mechanism, only reachable from this mod's Storage list
						// since the AllowStacking fix above) mutates a tracked Storage item's
						// CommonItemData IN PLACE to fill the weapon being equipped, consuming charges
						// without ever changing Count. Every earlier branch in this loop keys off Count
						// alone, so this exact, real, in-place consumption was completely invisible to it -
						// confirmed via Player.log against "Deimos AR-L" (a TFTV weapon) drawing from a
						// single "LASER BATTERY PACK" item: each equip consumed real charges from it (e.g.
						// 4->2), but the real backing ItemStorage was never told, so the next unequip's
						// ExtractLoadedAmmo/RegisterAdd re-added the extracted remainder ON TOP of the
						// stale, still-full real charges instead of the truly-reduced total - inflating the
						// real charge pool by a fixed amount every single equip/unequip cycle with nothing
						// lost anywhere to explain it.
						//
						// Fixed by syncing the real backing item to match whenever this exact case is
						// detected: a Count=1 representative carrying just the consumed/restored charge
						// delta, fed through CommonItemData's own Subtract/AddItem - the same convention
						// already used everywhere else in this file for a single-unit charge adjustment
						// (matching GetSingleItem()'s own Count=1 contract). Deliberately NOT a Count=0
						// representative - CommonItemData.Subtract's own borrow arithmetic assumes exactly
						// one unit is being touched (an unconditional `_count++` at the end that only nets
						// to zero when `other.Count == 1`), so a Count=0 delta would silently corrupt the
						// real Count instead of leaving it alone.
						int chargesDelta = entry.OriginalCharges - item.CommonItemData.CurrentCharges;
						if (chargesDelta != 0 && source != null)
						{
							GeoItem real;
							if (source.Items.TryGetValue(item.ItemDef, out real) && real.CommonItemData.Count >= 1)
							{
								Log.Debug($"TryCustomUpdateStorage: '{item.CommonItemData.GetDisplayName()}' charges changed {entry.OriginalCharges}->{item.CommonItemData.CurrentCharges} in-place (count unchanged) in '{label}' - syncing real storage to match.");
								if (chargesDelta > 0)
								{
									real.CommonItemData.Subtract(new GeoItem(item.ItemDef, 1, chargesDelta, null, -100));

									// A charges DECREASE with Count unchanged means one of THIS vehicle's own
									// already-claimed units just became genuinely partial (e.g. "+Ammo" draining
									// a claimed magazine pack in place to refill a weapon) - per explicit user
									// direction/this class's own core invariant (ChaosVehicleAssignment: "any
									// vehicle claim of a pooled-charge item is always genuinely full-charge; the
									// partial unit always stays untagged Global Inventory instead"), that
									// invariant must be actively RESTORED here, not just assumed - release
									// exactly 1 unit's worth of this vehicle's claim (mirroring Adjust's own
									// growth-time reservation) so the now-partial unit falls back to being
									// untagged and shows correctly under Global Inventory, rather than staying
									// claimed-but-partial under this vehicle. Confirmed via Player.log/user
									// report: without this, the vehicle kept the claim, showing a duplicate/
									// wrong-charge card instead of the real item quietly reappearing in Global.
									if (location.Kind == LocationKind.Vehicle)
									{
										int vehicleId = location.VehicleId;
										ItemDef itemDef = item.ItemDef;
										pendingClaimAdjustments.Add(() => AdjustVehicleAssignment(ctx.Faction, vehicleId, itemDef, -1));

										// The claim release above changes which LOCATION this exact item
										// belongs to (vehicle -> untagged Global) - this commit's own
										// incremental updatedEntries[item] a few lines below still carries the
										// STALE pre-release location/label/tag, since only a full rebuild from
										// truth (GetDisplayItems/BuildAllEntries) actually re-derives that.
										// Forcing the same already-proven full-refresh path this file already
										// uses for every other "ownership just changed mid-commit" case (see
										// anyExtractionHappened's own comment) avoids hand-patching this one
										// entry's tag/label here and risking yet another fresh half-updated-
										// display bug, and is also what actually confirmed-fixes the reported
										// stray duplicate card (a real full rebuild re-derives the Storage list
										// from the CURRENT, just-updated claim - so the vehicle no longer
										// contributes a claimed entry for this item at all, only Global does).
										anyExtractionHappened = true;
									}
								}
								else
								{
									real.CommonItemData.AddItem(new GeoItem(item.ItemDef, 1, -chargesDelta, null, -100));
								}
							}
						}
					}
				}
				else
				{
					// Brand new reference not seen at the last refresh/commit - either a freshly
					// unequipped item, or the other half of an in-list split (see above) - lands in
					// the current location either way, matching pre-existing behavior.
					Log.Debug($"TryCustomUpdateStorage: new item '{item.CommonItemData.GetDisplayName()}' x{item.CommonItemData.Count} -> '{ctx.Label}'.");
					AddPhysicalDelta(ctx.Storage, item.ItemDef, item.CommonItemData.Count);
					RememberAddSource(ctx.Storage, item);
					foreach (GeoItem magazine in ExtractLoadedAmmo(item))
					{
						Log.Debug($"TryCustomUpdateStorage: extracted loaded '{magazine.CommonItemData.GetDisplayName()}' x{magazine.CommonItemData.Count} out of '{item.CommonItemData.GetDisplayName()}' into '{ctx.Label}' as its own item.");
						RegisterAdd(ctx.Storage, ctx.Key, ctx.Label, magazine, magazine.CommonItemData.Count);
					}
					if (ctx.Key.Kind == LocationKind.Vehicle && ctx.Vehicle != null)
					{
						GeoVehicle vehicle = ctx.Vehicle;
						ItemDef itemDef = item.ItemDef;
						int count = item.CommonItemData.Count;
						pendingClaimAdjustments.Add(() => ChaosVehicleAssignment.Adjust(vehicle, itemDef, count));
					}
				}

				// A tracked Storage item can be drained to Count=0 IN PLACE (never actually removed
				// from UnfilteredItems) by vanilla's own TryLoadAmmo/TryLoadItemWithItem reload
				// mechanism: it calls ammoItem.CommonItemData.Subtract(wholeUnitClone) directly on
				// this same tracked reference, then hands the item to UIInventoryList.RemoveItem -
				// but with AllowStacking temporarily true (see Patch_UIInventoryList_TryLoadAmmo_
				// RespectReachability) and slot.Item == removedItem, RemoveItem's own branch just
				// does `slot.Item = null` and returns - it never calls UnfilteredItems.Remove the way
				// its slot==null branch does. Confirmed by direct user report: pressing "+Ammo" fully
				// consumed a claimed magazine into the weapon (correct), but the now-empty donor
				// reference survived in UnfilteredItems and got carried forward into updatedEntries/
				// _lastEntries as a permanent phantom "known" item - showing as a stray duplicate
				// card in the Storage list, and confusing the NEXT commit's own delta math (which
				// assumes a tracked item still has real Count>=1). The real-storage/claim bookkeeping
				// above already accounted for this item correctly (delta caught the Count 1->0 drop
				// like any other shrink) - this only purges the leftover dead reference itself,
				// exactly as if it had disappeared outright.
				if (item.CommonItemData.Count <= 0)
				{
					Log.Debug($"TryCustomUpdateStorage: '{item.ItemDef.name}' drained to Count=0 in place (not removed from UnfilteredItems by vanilla) - purging the dead reference instead of tracking it forward.");
					module.StorageList.UnfilteredItems.Remove(item);
					UIInventorySlot staleSlot = module.StorageList.Slots.FirstOrDefault(sl => ReferenceEquals(sl?.Item, item));
					if (staleSlot != null)
					{
						staleSlot.Item = null;
					}
					continue;
				}

				updatedEntries[item] = new StorageEntry
				{
					Item = item,
					LocationLabel = label,
					Reachable = reachable,
					SourceStorage = source,
					Location = location,
					OriginalCount = item.CommonItemData.Count,
					OriginalCharges = item.CommonItemData.CurrentCharges
				};
			}

			// Apply the aggregated net changes - one real operation per (storage, ItemDef) with
			// nonzero net delta, instead of a naive per-entry remove-then-add that could otherwise
			// touch the same real storage twice in one commit for what's really a zero-sum move.
			// Back-solves the single shared charges value BuildDisplayItem/CommonItemData's own pooling
			// convention (TotalAvailableCharges = ChargesMax*(Count-1)+Charges) needs to reproduce a
			// KNOWN true total across `count` units - the inverse of that same formula. Returns null
			// when there's nothing to back-solve from (not a pooled-charge item, or no true-sum was
			// ever recorded for it), so callers fall back to the plain representative-charges behavior
			// unchanged for every case this wasn't written for.
			int? BackSolveCharges(Dictionary<ItemStorage, Dictionary<ItemDef, int>> trueSums, ItemStorage storage, ItemDef itemDef, int count)
			{
				if (itemDef.ChargesMax <= 0 || !itemDef.CombineWhenStacking || count <= 0)
				{
					return null;
				}
				Dictionary<ItemDef, int> perDef;
				int trueTotal;
				if (!trueSums.TryGetValue(storage, out perDef) || !perDef.TryGetValue(itemDef, out trueTotal))
				{
					return null;
				}
				int backSolved = trueTotal - itemDef.ChargesMax * (count - 1);
				return Math.Max(0, Math.Min(itemDef.ChargesMax, backSolved));
			}

			foreach (KeyValuePair<ItemStorage, Dictionary<ItemDef, int>> storageEntry in netPhysicalDelta)
			{
				Dictionary<ItemDef, GeoItem> perDefAddRepresentative;
				addRepresentative.TryGetValue(storageEntry.Key, out perDefAddRepresentative);
				Dictionary<ItemDef, GeoItem> perDefRemoveRepresentative;
				removeRepresentative.TryGetValue(storageEntry.Key, out perDefRemoveRepresentative);

				foreach (KeyValuePair<ItemDef, int> defDelta in storageEntry.Value)
				{
					if (defDelta.Value > 0)
					{
						GeoItem representative = null;
						perDefAddRepresentative?.TryGetValue(defDelta.Key, out representative);
						GeoItem toAdd;
						if (representative != null)
						{
							// True-sum back-solve takes priority over the representative's own raw
							// charges whenever more than one fragment of this ItemDef was added this
							// commit (see addTrueChargesSum's own comment for the concrete "free extra
							// magazine" bug this fixes) - for the ordinary single-fragment case the two
							// are mathematically identical, so this never changes already-correct results.
							int? backSolved = BackSolveCharges(addTrueChargesSum, storageEntry.Key, defDelta.Key, defDelta.Value);
							toAdd = BuildDisplayItem(representative, defDelta.Value, backSolved);
							if (backSolved.HasValue)
							{
								Log.Debug($"Apply: adding '{defDelta.Key.name}' x{defDelta.Value} to '{storageEntry.Key}' - back-solved charges={backSolved.Value} from true-sum (naive representative charges would have been {representative.CommonItemData.CurrentCharges}).");
							}
						}
						else
						{
							toAdd = new GeoItem(defDelta.Key, defDelta.Value, -1, null, -100);
						}
						AddToStorage(storageEntry.Key, toAdd);
					}
					else if (defDelta.Value < 0)
					{
						GeoItem representative = null;
						perDefRemoveRepresentative?.TryGetValue(defDelta.Key, out representative);
						GeoItem toRemove;
						if (representative != null)
						{
							int? backSolved = BackSolveCharges(removeTrueChargesSum, storageEntry.Key, defDelta.Key, -defDelta.Value);
							toRemove = BuildDisplayItem(representative, -defDelta.Value, backSolved);
							Log.Debug($"Apply: removing '{defDelta.Key.name}' x{-defDelta.Value} from '{storageEntry.Key}' using {(backSolved.HasValue ? $"back-solved charges={backSolved.Value}" : $"representative charges={representative.CommonItemData.CurrentCharges}")}.");
						}
						else
						{
							toRemove = new GeoItem(defDelta.Key, -defDelta.Value, -1, null, -100);
							Log.Debug($"Apply: removing '{defDelta.Key.name}' x{-defDelta.Value} from '{storageEntry.Key}' using blank (ChargesMax) synthesis.");
						}
						RemoveFromStorage(storageEntry.Key, toRemove);
					}
				}
			}

			// Only now that every real AddToStorage/RemoveFromStorage call above has actually run
			// do we touch the claim overlay - so ChaosVehicleAssignment.Adjust's own "clamp to real
			// total" always sees the post-move real total, never the stale pre-move one.
			foreach (Action adjust in pendingClaimAdjustments)
			{
				adjust();
			}

			// If this commit extracted any loaded ammo (RegisterAdd), rebuild the whole display from
			// truth via the same full-refresh path a screen close/reopen already uses, instead of
			// this method's own incremental per-item UI bookkeeping - see anyExtractionHappened's own
			// comment for the full reasoning (including a confirmed real re-entrant-double-refresh/
			// weapon-duplication bug from briefly trying "always full refresh" unconditionally).
			// TryCustomRefreshStorageCore re-reads real storage/claims from scratch and calls
			// SetLastEntries itself, so it fully supersedes the ApplySlotTags/SetLastEntries
			// (updatedEntries) calls below for this commit.
			if (anyExtractionHappened)
			{
				return TryCustomRefreshStorageCore(state);
			}

			ApplySlotTags(state, module, updatedEntries, ctx.Key, ChaosContextMenu.GetFont(module.StorageLabelTextComp));
			SetLastEntries(state, updatedEntries);
			return true;
		}

		/// <summary>
		/// If the right-click landed on a non-empty, reachable slot in the Storage list, shows a
		/// context menu offering every other reachable location as a destination. Does nothing if the
		/// click missed a storage item, the item is currently unreachable (EventHandlersEnabled=false
		/// already blocks normal interaction; this mirrors that for the custom right-click path too),
		/// or there's nowhere else to send it.
		/// </summary>
		public static void TryShowStorageItemContextMenu(GeoscapeViewState state, List<RaycastResult> hits)
		{
			UIModuleSoldierEquip module = GetModule(state);
			LocationContext ctx = ResolveLocationContext(state);
			if (module == null || module.StorageList == null || ctx == null)
			{
				Log.Debug($"TryShowStorageItemContextMenu: no menu - module={module != null}, storageList={module?.StorageList != null}, ctx={ctx != null}.");
				return;
			}

			UIInventorySlot slot = null;
			foreach (RaycastResult result in hits)
			{
				UIInventorySlot candidate = result.gameObject.GetComponentInParent<UIInventorySlot>();
				if (candidate != null && candidate.ParentList == module.StorageList)
				{
					slot = candidate;
					break;
				}
			}
			if (slot == null || slot.Item == null)
			{
				Log.Debug($"TryShowStorageItemContextMenu: no menu - slot={slot != null}, item={slot?.Item != null}.");
				return;
			}
			if (!slot.EventHandlersEnabled)
			{
				// Right-click on a red/unreachable item never dispatches through Unity's own
				// IPointerClickHandler on this screen (it's intercepted at the InputEvent "Cancel"
				// level before that), so this is the one place a right-click on an unreachable slot
				// can be detected. Shows the shared hint menu (see ShowUnreachableHintMenu) - same
				// treatment as the left-click path (SetUnreachableTooltip), per explicit user
				// request for both buttons to behave identically.
				GeoItem unreachableItem = slot.Item as GeoItem;
				StorageEntry unreachableEntry = default(StorageEntry);
				bool resolved = unreachableItem != null && GetLastEntries(state).TryGetValue(unreachableItem, out unreachableEntry);
				ShowUnreachableHintMenu(state, module.StorageList.transform.root,
					ChaosContextMenu.GetFont(module.StorageLabelTextComp),
					resolved ? unreachableEntry.Location : (LocationKey?)null,
					resolved ? unreachableEntry.LocationLabel : null);
				Log.Debug("TryShowStorageItemContextMenu: right-click on unreachable item - showing hint menu instead of a real destination list.");
				return;
			}
			GeoItem item = slot.Item as GeoItem;
			if (item == null)
			{
				return;
			}

			Dictionary<GeoItem, StorageEntry> lastEntries = GetLastEntries(state);
			StorageEntry entry;
			bool entryFound = lastEntries.TryGetValue(item, out entry);
			LocationKey sourceLocation = entryFound ? entry.Location : ctx.Key;
			string ownerLabel = entryFound ? entry.LocationLabel : ctx.Label;

			// Global Inventory is a one-way source, never a destination: items can move OUT of it to
			// any specific base/aircraft, but never back INTO it once assigned somewhere - per explicit
			// user correction. Excluding it unconditionally here is equivalent to "only allow moving
			// out of global": if the item's own source already IS the global pool, that option is
			// already removed by the sourceLocation filter below, so this line never actually blocks a
			// legitimate move - it only ever removes global as a target for an item that lives
			// elsewhere.
			List<StorageOption> reachable = GetReachableLocations(ctx)
				.Where(o => o.Key.Kind != LocationKind.GlobalUntagged)
				.ToList();

			// Items sourced FROM the global pool have a further restriction, per explicit user
			// request: only items that can be manufactured instantly (ItemDef.IsInstant) may move to
			// ANY base or aircraft; everything else may only move to a base, or to an aircraft that's
			// currently docked at a base (standalone, non-docked aircraft are excluded). Every entry in
			// ctx.Destinations is already inherently "docked" (GetDockedStorageOptions/
			// GetDockedStorageOptionsForSite only ever add them while actually at a site), so this only
			// ever filters out ctx's own storage when ctx itself is a standalone, non-docked aircraft.
			bool sourceIsGlobal = sourceLocation.Kind == LocationKind.GlobalUntagged;
			if (sourceIsGlobal && !IsInstantManufacture(item.ItemDef))
			{
				reachable = reachable.Where(o => o.IsBase || (o.Vehicle != null && o.Vehicle.CurrentSite != null && !o.Vehicle.Travelling)).ToList();
			}

			// Per explicit user report/request: a pooled-charge item's Global Inventory display can
			// bundle genuinely-full units together with the one real partial remainder into a single
			// row (see GetDisplayItems' GlobalUntagged case) - offering "Move to..." on that row once
			// only the partial unit is left would either silently no-op (aircraft destination - see
			// ChaosVehicleAssignment.Adjust's own reserved-unit cap) or, worse, actually move the
			// partial one itself while leaving a fabricated "full" unit behind (base destination - see
			// TransferBetweenLocations' own forceFullCharges guard, added after a confirmed report of
			// exactly that: a real 6/36 got moved to a base while Global was left showing a full
			// 36/36). The one true partial unit must never be offered a destination at all - hiding
			// every destination (base AND aircraft alike) once there's no full-unit headroom left is
			// the correct fix, not just hiding aircraft.
			if (sourceIsGlobal && IsPooledChargeItem(item.ItemDef) && !HasFullUnitHeadroom(ctx.Faction, item.ItemDef))
			{
				reachable = new List<StorageOption>();
			}

			List<StorageOption> destinations = reachable.Where(o => !o.Key.Equals(sourceLocation)).ToList();
			if (destinations.Count == 0)
			{
				Log.Debug($"TryShowStorageItemContextMenu: no menu - ctx='{ctx.Label}', reachableCount={reachable.Count}, sourceIsGlobal={sourceIsGlobal}, destinationsCount=0.");
				return;
			}

			object font = ChaosContextMenu.GetFont(module.StorageLabelTextComp);
			List<ChaosContextMenu.Option> options = destinations.Select(dest => new ChaosContextMenu.Option
			{
				Label = ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_MOVE_TO", dest.Label),
				// The destination that IS the currently-displayed base/aircraft reads as the
				// "usual" action - shown in the normal white text instead of the light-blue used
				// for every other (cross-location) destination.
				TextColor = dest.Key.Equals(ctx.Key) ? (Color?)ChaosPerSlotAircraftLabel.OwnLocationColor : null,
				OnClick = () =>
				{
					// Per explicit user request: "Move to..." moves exactly 1 unit per click, not the
					// whole stack - clicking again moves another one, same as every other move in this
					// mod is already meant to work one unit at a time.
					int count = 1;
					TransferBetweenLocations(ctx.Faction, sourceLocation, dest.Key, item.ItemDef, count);
					Log.Debug($"MoveItem '{item.CommonItemData.GetDisplayName()}' x{count} from '{ownerLabel}' -> '{dest.Label}'.");
					// REVERTED (v45): tried updating just the moved entry's tracked location in
					// place instead of a full ForceRefresh, to stop the whole Storage list visually
					// "resorting" after one move. Reverted after the user reported it broke
					// something - Player.log showed the same item being moved twice in a row (with
					// a claim-clamp warning on the second attempt), consistent with the in-place
					// update not making the move visually obvious enough, and/or per-screen
					// _lastEntries tracking going stale in ways a full rebuild wouldn't. A full
					// refresh is proven correct (always matches real state); the resort is a
					// cosmetic annoyance, not a correctness bug - safer default until a more
					// carefully verified surgical update can be designed.
					ForceRefresh(state);
				}
			}).ToList();

			Transform menuParent = module.StorageList.transform.root;
			Log.Debug($"TryShowStorageItemContextMenu: showing menu with {options.Count} destination(s) for '{item.CommonItemData.GetDisplayName()}', screen='{state.GetType().Name}', menuParent='{menuParent.name}'.");
			ChaosContextMenu.Show(menuParent, GetMousePosition(), ownerLabel, font, options);
		}

		/// Same "Move to..." context menu as TryShowStorageItemContextMenu, adapted for the Air
		/// Force tab's read-only cargo overlay (ChaosAircraftItemsDisplay) instead of a real
		/// UIStateEditSoldier/UIStateEditVehicle Storage slot - per explicit user request ("right
		/// click on our newly added filter button on any item to show same dropdown menu... with
		/// possibility to move item to other aircraft/base"). The source location is always simply
		/// "this vehicle" (a cargo card can only ever exist because THIS vehicle currently claims
		/// it - there's no separate StorageEntry/LocationKey tracking needed the way the merged
		/// soldier-screen view requires, since these cards aren't reconciled against a diffed UI
		/// list at all), so this reuses GetDockedStorageOptions/TransferBetweenLocations directly
		/// rather than needing ResolveLocationContext/StorageEntry machinery. Called from both a
		/// right-click (via the raycast-hit-search overload below) and a double left click (which
		/// already knows exactly which slot was clicked, no raycast needed).
		public static void ShowCargoItemContextMenuForSlot(GeoVehicle vehicle, UIInventorySlot slot)
		{
			GeoItem item = slot?.Item as GeoItem;
			if (vehicle == null || item == null)
			{
				Log.Debug($"ShowCargoItemContextMenuForSlot: no menu - vehicle={vehicle != null}, slot={slot != null}, item={item != null}.");
				return;
			}

			UIModuleVehicleEquip module = ChaosAircraftItemsDisplay.LastVehicleEquipModule;
			Transform menuParent = module?.StorageList?.transform?.root;
			if (menuParent == null)
			{
				Log.Debug("ShowCargoItemContextMenuForSlot: no menu - couldn't resolve a menu parent transform.");
				return;
			}

			List<StorageOption> destinations = GetDockedStorageOptions(vehicle).Skip(1).ToList();
			object font = ChaosAircraftItemsDisplay.LastFont;
			List<ChaosContextMenu.Option> options;
			if (destinations.Count == 0)
			{
				// Not docked anywhere with a destination - per explicit user request, still show
				// the menu (rather than silently doing nothing) with a single red, non-actionable
				// hint row instead of a real "Unload to..." option.
				options = new List<ChaosContextMenu.Option>
				{
					new ChaosContextMenu.Option { Label = ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_DOCK_TO_UNLOAD"), TextColor = ChaosPerSlotAircraftLabel.UnreachableColor }
				};
			}
			else
			{
				LocationKey sourceLocation = LocationKey.ForVehicle(vehicle);
				options = destinations.Select(dest => new ChaosContextMenu.Option
				{
					Label = ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_UNLOAD_TO", dest.Label),
					OnClick = () =>
					{
						int count = item.CommonItemData.Count;
						TransferBetweenLocations(vehicle.Owner, sourceLocation, dest.Key, item.ItemDef, count);
						Log.Debug($"ShowCargoItemContextMenuForSlot: unloaded '{item.CommonItemData.GetDisplayName()}' x{count} from '{vehicle.Name}' -> '{dest.Label}'.");
						ChaosAircraftItemsDisplay.Refresh();
						ChaosVehicleCrewCountSuffix.RefreshAll();
						ChaosGeoscapeVehicleBadge.RefreshCurrent();
					}
				}).ToList();
			}

			Log.Debug($"ShowCargoItemContextMenuForSlot: showing menu with {options.Count} row(s) for '{item.CommonItemData.GetDisplayName()}' on '{vehicle.Name}' (docked={destinations.Count > 0}).");
			ChaosContextMenu.Show(menuParent, GetMousePosition(), vehicle.Name, font, options);
		}

		public static void TryShowCargoItemContextMenu(GeoVehicle vehicle, List<RaycastResult> hits)
		{
			UIInventorySlot slot = null;
			foreach (RaycastResult result in hits)
			{
				UIInventorySlot candidate = result.gameObject.GetComponentInParent<UIInventorySlot>();
				if (candidate != null && ChaosAircraftItemsDisplay.IsOwnedSlot(candidate.transform))
				{
					slot = candidate;
					break;
				}
			}
			ShowCargoItemContextMenuForSlot(vehicle, slot);
		}

		/// Right-click/double-click on the "Aircraft Cargo Inventory" filter BUTTON itself (not an
		/// individual card) - per explicit user request - offers to unload EVERY currently claimed
		/// item at once to one chosen destination, or the same red "Dock to unload all items" hint if
		/// not currently docked anywhere.
		public static void ShowUnloadAllMenu(GeoVehicle vehicle)
		{
			if (vehicle == null)
			{
				Log.Debug("ShowUnloadAllMenu: no vehicle.");
				return;
			}
			List<KeyValuePair<ItemDef, int>> claims = ChaosVehicleAssignment.GetAll(vehicle);
			if (claims.Count == 0)
			{
				Log.Debug($"ShowUnloadAllMenu: '{vehicle.Name}' has no claimed items, nothing to unload.");
				return;
			}

			UIModuleVehicleEquip module = ChaosAircraftItemsDisplay.LastVehicleEquipModule;
			Transform menuParent = module?.StorageList?.transform?.root;
			if (menuParent == null)
			{
				Log.Debug("ShowUnloadAllMenu: no menu - couldn't resolve a menu parent transform.");
				return;
			}

			List<StorageOption> destinations = GetDockedStorageOptions(vehicle).Skip(1).ToList();
			object font = ChaosAircraftItemsDisplay.LastFont;
			List<ChaosContextMenu.Option> options;
			if (destinations.Count == 0)
			{
				options = new List<ChaosContextMenu.Option>
				{
					new ChaosContextMenu.Option { Label = ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_DOCK_TO_UNLOAD_ALL"), TextColor = ChaosPerSlotAircraftLabel.UnreachableColor }
				};
			}
			else
			{
				LocationKey sourceLocation = LocationKey.ForVehicle(vehicle);
				options = destinations.Select(dest => new ChaosContextMenu.Option
				{
					Label = ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_UNLOAD_ALL_TO", dest.Label),
					OnClick = () =>
					{
						List<KeyValuePair<ItemDef, int>> snapshot = ChaosVehicleAssignment.GetAll(vehicle);
						foreach (KeyValuePair<ItemDef, int> claim in snapshot)
						{
							TransferBetweenLocations(vehicle.Owner, sourceLocation, dest.Key, claim.Key, claim.Value);
						}
						Log.Debug($"ShowUnloadAllMenu: unloaded all ({snapshot.Count} item type(s)) from '{vehicle.Name}' -> '{dest.Label}'.");
						ChaosAircraftItemsDisplay.Refresh();
						ChaosVehicleCrewCountSuffix.RefreshAll();
						ChaosGeoscapeVehicleBadge.RefreshCurrent();
					}
				}).ToList();
			}

			Log.Debug($"ShowUnloadAllMenu: showing menu with {options.Count} row(s) for '{vehicle.Name}' ({claims.Count} claimed item type(s), docked={destinations.Count > 0}).");
			ChaosContextMenu.Show(menuParent, GetMousePosition(), vehicle.Name, font, options);
		}

		private static readonly FieldInfo RefreshStorageField_Soldier =
			typeof(UIStateEditSoldier).GetField("_refreshStorage", BindingFlags.NonPublic | BindingFlags.Instance);
		private static readonly FieldInfo RefreshStorageField_Vehicle =
			typeof(UIStateEditVehicle).GetField("_refreshStorage", BindingFlags.NonPublic | BindingFlags.Instance);

		private static readonly MethodInfo RefreshStorageMethod_Soldier =
			typeof(UIStateEditSoldier).GetMethod("RefreshStorage", BindingFlags.NonPublic | BindingFlags.Instance);
		private static readonly MethodInfo RefreshStorageMethod_Vehicle =
			typeof(UIStateEditVehicle).GetMethod("RefreshStorage", BindingFlags.NonPublic | BindingFlags.Instance);

		private static void ForceRefresh(GeoscapeViewState state)
		{
			if (state is UIStateEditSoldier)
			{
				RefreshStorageField_Soldier?.SetValue(state, true);
				RefreshStorageMethod_Soldier?.Invoke(state, null);
			}
			else if (state is UIStateEditVehicle)
			{
				RefreshStorageField_Vehicle?.SetValue(state, true);
				RefreshStorageMethod_Vehicle?.Invoke(state, null);
			}
		}

		private const int CargoSummaryMaxItemsPerSection = 6;
		private const int CargoSummaryMaxSections = 4;

		public static void ShowAircraftCargoSummary(UIModuleVehicleCycle cycleModule)
		{
			VehicleDisplayData current = cycleModule.CurrentVehicle;
			GeoVehicle vehicle = current?.GetBaseObject<GeoVehicle>();
			AircraftInfoController controller = cycleModule.AircraftInfoController;
			if (vehicle == null || controller == null || controller.AircraftDescription == null)
			{
				return;
			}

			List<StorageOption> options = GetDockedStorageOptions(vehicle);
			controller.AircraftDescription.text = BuildCombinedCargoSummary(options);
		}

		/// Rebuilds the read-only aircraft-cargo slot grid (ChaosAircraftItemsDisplay) for
		/// whichever vehicle is now selected on the Air Force tab. Closes it entirely if there's
		/// no vehicle selected (e.g. an empty roster) - ChaosAircraftItemsDisplay.Show already
		/// no-ops on an empty claim list, so nothing further is needed for "no claimed items".
		public static void ShowAircraftItemsDisplay(UIStateVehicleRoster state, VehicleDisplayData newVehicle)
		{
			GeoVehicle vehicle = newVehicle?.GetBaseObject<GeoVehicle>();
			if (vehicle == null)
			{
				Log.Debug("ShowAircraftItemsDisplay: newVehicle resolved to null (e.g. empty roster) - closing display.");
				ChaosAircraftItemsDisplay.Close();
				return;
			}
			UIModuleVehicleEquip vehicleEquipModule = VehicleEquipModuleProp_Roster?.GetValue(state) as UIModuleVehicleEquip;
			UIModuleSoldierEquip soldierEquipModule = GetSoldierEquipModuleFromAnyState(state);
			if (vehicleEquipModule == null || soldierEquipModule == null)
			{
				Log.Debug($"ShowAircraftItemsDisplay: '{vehicle.Name}' - reflection lookup failed (vehicleEquipModule={(vehicleEquipModule != null)}, soldierEquipModule={(soldierEquipModule != null)}) - can't show display.");
			}
			object font = ChaosContextMenu.GetFont(soldierEquipModule?.StorageLabelTextComp);
			ChaosAircraftItemsDisplay.Show(vehicleEquipModule, soldierEquipModule, vehicle, font);
		}

		/// Clones the vanilla "Modules" filter button into a 4th button ("Aircraft Cargo
		/// Inventory") in the same tab group, per explicit user request. Selecting it hides every
		/// real vehicle-equipment item from the STORAGE grid (SetFilter with an always-false
		/// predicate - GeoVehicleEquipmentUIData never represents this mod's own claimed general
		/// items, so there's nothing legitimate for this category to ever show from vanilla's own
		/// data) and refreshes ChaosAircraftItemsDisplay so it has the whole grid free to lay
		/// cargo cards into. Also wires a refresh into the 3 EXISTING vanilla buttons, since
		/// switching category changes which real cells are empty/occupied - our cards are
		/// positioned by a snapshot taken at Show()/Refresh() time, not continuously, so any
		/// filter change needs an explicit rebuild to stay visually accurate.
		public static void AddCargoFilterButton(UIModuleVehicleEquip module)
		{
			PhoenixGeneralButton source = module?.ModuleTypeFilterButton;
			if (source == null || source.TabbingControl == null || module.StorageList == null || module.FilterTextLabel == null)
			{
				Log.Debug($"AddCargoFilterButton: missing dependency (source={(source != null)}, tabbingControl={(source?.TabbingControl != null)}, storageList={(module?.StorageList != null)}, filterLabel={(module?.FilterTextLabel != null)}) - not adding the 4th filter button.");
				return;
			}

			PhoenixGeneralButton cargoButton = UnityEngine.Object.Instantiate(source, source.transform.parent, false);
			cargoButton.name = "ChaosCargoFilterButton";
			cargoButton.IsSelected = false;
			if (cargoButton.BaseButton != null)
			{
				cargoButton.BaseButton.onClick.RemoveAllListeners();
				cargoButton.BaseButton.onClick.AddListener(() =>
				{
					// FilterTextLabel's actual text (including the live item count) is set inside
					// ChaosAircraftItemsDisplay.Show/Refresh instead - SetCargoFilterActive(true)
					// below calls Refresh() synchronously, so there's no need to set a static
					// placeholder here first.
					module.StorageList.SetFilter(def => false);
					ChaosAircraftItemsDisplay.SetCargoFilterActive(true);
				});
			}
			source.TabbingControl.AddTabbingMember(cargoButton);
			AddCargoButtonBadge(cargoButton, module.FilterTextLabel);
			ChaosAircraftItemsDisplay.RegisterCargoButton(cargoButton.transform);

			// Double left click on the button itself - like double-clicking a card - offers to
			// unload EVERYTHING at once instead of one item, per explicit user request. Right
			// click on the button is handled separately, through the same OnInputEvent "Cancel"
			// suppression path used for cards (Patch_UIStateVehicleRoster_OnInputEvent_
			// SuppressRightClickCancel), since a raw right-click would otherwise still exit the
			// screen via vanilla's own Cancel handling regardless of this component.
			ChaosDoubleClickHandler cargoButtonDblClick = cargoButton.gameObject.AddComponent<ChaosDoubleClickHandler>();
			cargoButtonDblClick.OnDoubleClick = () => ChaosInventoryPatchUtl.ShowUnloadAllMenu(ChaosAircraftItemsDisplay.LastVehicle);

			// The 3 vanilla buttons only ever called DoFilter()/updated FilterTextLabel
			// themselves - none of them knew this mod's cargo overlay exists. Wiring
			// SetCargoFilterActive(false) alongside their own vanilla behavior (not replacing it)
			// turns cargo cards back off - and refreshes their positions - no matter which of the
			// 3 real categories the player switches to. Without this, cargo cards kept showing
			// under every filter (reported directly: "currently... it displayed everywhere").
			if (module.AllTypeFilterButton?.BaseButton != null)
			{
				module.AllTypeFilterButton.BaseButton.onClick.AddListener(() => ChaosAircraftItemsDisplay.SetCargoFilterActive(false));
			}
			if (module.WeaponTypeFilterButton?.BaseButton != null)
			{
				module.WeaponTypeFilterButton.BaseButton.onClick.AddListener(() => ChaosAircraftItemsDisplay.SetCargoFilterActive(false));
			}
			if (module.ModuleTypeFilterButton?.BaseButton != null)
			{
				module.ModuleTypeFilterButton.BaseButton.onClick.AddListener(() => ChaosAircraftItemsDisplay.SetCargoFilterActive(false));
			}

			Log.Debug("AddCargoFilterButton: added 'Aircraft Cargo Inventory' filter button (own badge, exclusive display, double-click unload-all) and wired all 4 filter buttons.");
		}

		/// Gives the cloned cargo button a visually distinct look from the "Modules" icon it was
		/// cloned from - now the live cargo item count itself (kept in sync by
		/// ChaosAircraftItemsDisplay.Show via RegisterCargoButtonCountLabel), per explicit user
		/// request ("instead of icon show count of items") - more informative than an arbitrary
		/// letter/color, and it was only ever a placeholder differentiator to begin with. A
		/// solid-color Image background (no sprite needed - a bare UnityEngine.UI.Image with no
		/// sprite assigned still renders as a plain filled rect via the default UI shader) is kept
		/// so the number stays legible regardless of the button's own art underneath, placed as the
		/// LAST child (drawn on top of everything else already on the button) plus a forced-topmost
		/// Canvas override, same as before.
		private static void AddCargoButtonBadge(PhoenixGeneralButton cargoButton, Text fontSource)
		{
			GameObject badgeGO = new GameObject("ChaosCargoBadge", typeof(RectTransform), typeof(Image));
			badgeGO.transform.SetParent(cargoButton.transform, false);
			badgeGO.transform.SetAsLastSibling();
			// Sibling order alone only wins within the same Canvas, and this button likely has its
			// own sub-canvas/sorting setup that a plain SetAsLastSibling can't out-rank.
			ChaosContextMenu.MakeTopmost(badgeGO, 500);
			RectTransform badgeRect = (RectTransform)badgeGO.transform;
			badgeRect.anchorMin = new Vector2(0.5f, 0.5f);
			badgeRect.anchorMax = new Vector2(0.5f, 0.5f);
			badgeRect.pivot = new Vector2(0.5f, 0.5f);
			badgeRect.anchoredPosition = Vector2.zero;
			badgeRect.sizeDelta = new Vector2(30f, 26f);

			Image badgeImage = badgeGO.GetComponent<Image>();
			badgeImage.sprite = null;
			badgeImage.color = new Color(0f, 0f, 0f, 0.75f);
			badgeImage.raycastTarget = false;

			GameObject textGO = new GameObject("Label", typeof(RectTransform), typeof(Text));
			textGO.transform.SetParent(badgeRect, false);
			RectTransform textRect = (RectTransform)textGO.transform;
			textRect.anchorMin = Vector2.zero;
			textRect.anchorMax = Vector2.one;
			textRect.offsetMin = Vector2.zero;
			textRect.offsetMax = Vector2.zero;

			Text badgeText = textGO.GetComponent<Text>();
			ChaosContextMenu.SetFont(badgeText, ChaosContextMenu.GetFont(fontSource));
			ChaosContextMenu.SetCenterAlignment(badgeText);
			badgeText.text = "0";
			badgeText.color = Color.white;
			badgeText.fontSize = 18;
			badgeText.raycastTarget = false;

			ChaosAircraftItemsDisplay.RegisterCargoButtonCountLabel(badgeText);
		}

		private static string BuildCombinedCargoSummary(List<StorageOption> options)
		{
			List<string> sections = options
				.Take(CargoSummaryMaxSections)
				.Select(option => option.Label + ": " + BuildCargoLine(option.Key))
				.ToList();

			string result = ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_CARGO_SUMMARY_PREFIX", string.Join("  |  ", sections));
			if (options.Count > CargoSummaryMaxSections)
			{
				result += ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_CARGO_MORE_LOCATIONS", options.Count - CargoSummaryMaxSections);
			}
			return result;
		}

		// Reads via GetDisplayItems (not option.Storage.Items.Values directly) since a vehicle
		// option's Storage is now the whole faction's real pool - only GetDisplayItems knows how to
		// resolve "what does THIS specific location show" from that shared physical backing.
		private static string BuildCargoLine(LocationKey key)
		{
			List<GeoItem> items = GetDisplayItems(key);
			if (items.Count == 0)
			{
				return ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_CARGO_EMPTY");
			}

			List<string> lines = items
				.Select(item => $"{item.CommonItemData.GetDisplayName()} x{item.CommonItemData.Count}")
				.ToList();

			string shown = string.Join(", ", lines.Take(CargoSummaryMaxItemsPerSection));
			if (lines.Count > CargoSummaryMaxItemsPerSection)
			{
				shown += ChaosInventoryLocalization.Get("KEY_CHAOSINVENTORY_CARGO_MORE_ITEMS", lines.Count - CargoSummaryMaxItemsPerSection);
			}
			return shown;
		}

		public static void OverrideStorageIndicator(GeoscapeViewState state)
		{
			OverrideStorageIndicator(state, ResolveLocationContext(state));
		}

		// See ReapplySlotTags's own overload above for why this exists.
		public static void OverrideStorageIndicator(GeoscapeViewState state, LocationContext ctx)
		{
			if (ctx == null)
			{
				return;
			}
			UIModuleSoldierEquip module = GetModule(state);
			if (module == null)
			{
				return;
			}
			module.SetStorageIndicatorUI(GetLocationStorageUsed(ctx), ctx.Faction.GetTotalAvailableStorage());
		}
	}
}

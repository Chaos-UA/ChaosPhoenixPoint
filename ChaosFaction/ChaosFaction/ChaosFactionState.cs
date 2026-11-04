using System.Collections.Generic;
using System.Linq;
using Base.Assets;
using Base.Core;
using Base.Serialization.General;
using Base.UI;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Missions;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.Levels.Objectives;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Tactical.Entities;
using UnityEngine;

namespace ChaosFaction
{
	internal static class Log
	{
		public static void Debug(string msg)
		{
			if (ChaosFactionMain.Cfg?.EnableDebugLog == true)
				UnityEngine.Debug.Log("[ChaosFaction] " + msg);
		}
	}

	/// <summary>
	/// Shared "how many concurrently-active mod-driven things want Geoscape time paused"
	/// coordinator — extracted from what used to be AllyRequestTracker's own private
	/// _pausedStateBeforeAnyRequest/UpdatePauseState once DebugActivityTracker needed the
	/// exact same "force-pause while active, restore whatever the player had before once
	/// the last one resolves" behavior for Pure/Forsaken debug activities too. A single
	/// shared counter (not one per tracker) means a help request and a debug activity
	/// active at once (not that both should ever coexist for the *same* site — see the
	/// mutual-exclusivity checks — but nothing stops them being active for two different
	/// sites simultaneously) still only captures/restores the pre-existing pause state
	/// once, correctly, rather than each tracker fighting over its own snapshot.
	/// </summary>
	internal static class SitePauseCoordinator
	{
		private static bool? _pausedStateBeforeAnyActivity;
		private static int _activeCount;

		public static void Increment(GeoLevelController geoLevel)
		{
			_activeCount++;
			Apply(geoLevel);
		}

		public static void Decrement(GeoLevelController geoLevel)
		{
			_activeCount = Mathf.Max(0, _activeCount - 1);
			Apply(geoLevel);
		}

		private static void Apply(GeoLevelController geoLevel)
		{
			if (geoLevel?.Timing == null)
				return;
			if (_activeCount > 0)
			{
				if (_pausedStateBeforeAnyActivity == null)
					_pausedStateBeforeAnyActivity = geoLevel.Timing.Paused;
				geoLevel.Timing.Paused = true;
			}
			else if (_pausedStateBeforeAnyActivity != null)
			{
				geoLevel.Timing.Paused = _pausedStateBeforeAnyActivity.Value;
				_pausedStateBeforeAnyActivity = null;
			}
		}
	}

	/// <summary>
	/// GeoSite.SiteName is only ever assigned for Havens/Phoenix bases/Marketplace/
	/// Archaeology sites (confirmed by grepping every `.SiteName =` assignment site in
	/// source — Pandoran bases have none), defaulting to an empty
	/// LocalizedTextBind whose Localize(null) returns null — falls back to the alien
	/// base's own tier display name (e.g. "Lair") for that case. Shared by
	/// AskForHelpGeoFactionObjective and DebugActivityGeoFactionObjective, which both
	/// need the same "what do we call this site" logic.
	/// </summary>
	internal static class GeoSiteDisplayHelper
	{
		public static string GetDisplayName(GeoSite site)
		{
			if (site == null)
				return ChaosFactionLocalization.Get("KEY_CHAOSFACTION_UNKNOWN");
			string name = site.SiteName?.Localize(null);
			if (!string.IsNullOrEmpty(name))
				return name;
			string alienBaseName = site.GetComponent<GeoAlienBase>()?.AlienBaseTypeDef?.Name?.Localize(null);
			return !string.IsNullOrEmpty(alienBaseName) ? alienBaseName : ChaosFactionLocalization.Get("KEY_CHAOSFACTION_UNKNOWN");
		}
	}

	/// <summary>
	/// Puts a small persistent marker on a site's Geoscape icon while its help request is
	/// pending — no existing GeoSiteVisualsController icon slot (InfestedIcon, LockIcon,
	/// DiplomaticMissionAvailable, etc.) means what we need, they're each tied 1:1 to a
	/// specific vanilla feature, and none of the sibling mods in this repo have ever
	/// rendered anything into the Geoscape 3D scene either (checked — no precedent to
	/// reuse). GeoSiteVisualsController.RefreshAnimations/GeoSite.RefreshSiteAnimations
	/// (the built-in "highlight glow" effect) was considered and rejected: it's the same
	/// mechanism GeoscapeView uses for mouse-hover highlighting
	/// (GeoscapeView.cs:1587,1592, `_lastHighlightedSite`), so forcing it on would get
	/// silently toggled back off the moment the player hovers away from/onto any site —
	/// fine for transient feedback, not for a marker that needs to persist.
	/// Built from scratch instead: a small sphere primitive (no facing/orientation concerns
	/// the way a TextMesh label would have, and no custom asset needed —
	/// GameObject.CreatePrimitive is a built-in Unity API), colored light blue to match the
	/// "Already Requested Help" context-menu entry.
	///
	/// v1 (not visible in-game, per player report) parented the marker under
	/// GeoSiteVisualsController.VisualsContainer with a hardcoded offset/scale — two
	/// unverified assumptions at once (that Transform's own scale/position conventions,
	/// and a guessed local offset), so there was no way to tell which one was wrong.
	/// v2 (this version) instead parents directly under SiteIconRenderer.transform — the
	/// MeshRenderer that IS the visible site icon, so whatever local-space conventions it
	/// already uses are guaranteed correct by construction — and explicitly copies its
	/// GameObject layer onto the marker (CreatePrimitive defaults to layer 0/Default,
	/// which may not be in whatever culling mask the Geoscape camera actually renders;
	/// this wasn't ruled out as a cause for v1's invisibility). Also logs the marker's
	/// resulting world position/lossyScale and the icon's own world bounds right after
	/// creation, so if this still isn't visible, there's concrete Player.log data to
	/// reason from (offset direction, relative scale) instead of guessing a third time.
	/// </summary>
	internal static class HelpRequestedMarker
	{
		private const float MarkerScale = 0.35f;
		private static readonly Vector3 MarkerLocalOffset = new Vector3(0f, 1.2f, 0f);

		/// The default friendly-blue marker color (Ask-for-Help). `internal` (not
		/// `private`) so `DebugActivityTracker.RevealIfNeeded` (ChaosFactionDebug.cs) can
		/// reuse this exact shade for a revealed allied activity's map label — explicit
		/// request: "I want the text on map to have light blue color (same as help
		/// request)" — one source of truth instead of a second, only-visually-similar
		/// constant that could drift out of sync.
		internal static readonly Color MarkerColor = new Color(0.25f, 0.65f, 1f);

		private const float LabelLocalHeight = 1.1f;
		private const float LabelCharacterSize = 0.3f;
		private const int LabelFontSize = 48;

		private static readonly Dictionary<GeoSite, GameObject> Markers = new Dictionary<GeoSite, GameObject>();

		/// <summary>
		/// expiresAtHours/onExpired drive the countdown label and auto-expiry — see
		/// HelpMarkerCountdown. The stale-entry check below (TryGetValue + null check,
		/// rather than a plain ContainsKey) matters if this is ever called again for a
		/// site whose previous marker GameObject was destroyed without Hide() being told
		/// (e.g. an in-process reload that doesn't restart the whole app and so doesn't
		/// clear this static Dictionary) — Unity's "fake null" for a destroyed object
		/// still satisfies `!= null` on a raw reference check but not on the overloaded
		/// `==`/`!=` operators Unity types provide, so comparing against null the normal
		/// way here correctly detects it and lets a fresh marker be created instead of
		/// silently doing nothing forever.
		/// </summary>
		public static void Show(GeoSite site, string factionDisplayName, double expiresAtHours, System.Action onExpired)
		{
			Show(site, factionDisplayName, expiresAtHours, onExpired, MarkerColor);
		}

		/// <summary>
		/// color overload — DebugActivityTracker uses ChaosFactionColors.HostileRed here
		/// instead of the default friendly-help blue, so a Pure/Forsaken debug activity's
		/// map marker reads as a threat rather than an ally at a glance. Everything else
		/// (pulse, billboard, countdown) is identical regardless of color.
		/// Bug fixed here: the sphere's material color and the label's initial text color
		/// were both hardcoded to the private `MarkerColor` field (the default friendly
		/// blue) instead of reading this method's own `color` parameter — found while
		/// implementing the "unknown until revealed" activity marker (explicit follow-up
		/// request), since that feature needs Show() to actually honor a passed-in gray for
		/// the unrevealed state. Every marker this mod has ever shown (including "hostile"
		/// debug activities, which pass HostileRed) was silently rendering blue on the
		/// Geoscape until this fix, regardless of what color was requested.
		/// </summary>
		public static void Show(GeoSite site, string factionDisplayName, double expiresAtHours, System.Action onExpired, Color color)
		{
			if (site == null)
				return;
			if (Markers.TryGetValue(site, out GameObject existingMarker) && existingMarker != null)
				return;
			try
			{
				GeoSiteVisualsController visuals = site.GetComponent<GeoSiteVisualsController>();
				Transform parent = visuals != null && visuals.SiteIconRenderer != null
					? visuals.SiteIconRenderer.transform
					: site.transform;

				// Not stripping the auto-added sphere collider CreatePrimitive attaches:
				// UnityEngine.Collider isn't available in this project's trimmed ModSDK
				// UnityEngine.dll reference (compile error otherwise), and Geoscape site
				// clicks don't appear to go through 3D physics raycasts against arbitrary
				// scene colliders anyway (site selection is its own system) — if it turns
				// out to intercept clicks in testing, revisit with a different primitive
				// approach (e.g. a manually-built mesh with no collider at all).
				GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
				marker.name = "ChaosFaction_HelpRequestedMarker";
				marker.layer = parent.gameObject.layer;
				marker.transform.SetParent(parent, false);
				marker.transform.localPosition = MarkerLocalOffset;
				marker.transform.localRotation = Quaternion.identity;
				marker.transform.localScale = Vector3.one * MarkerScale;
				Renderer markerRenderer = marker.GetComponent<Renderer>();
				if (markerRenderer != null)
					markerRenderer.material.color = color;

				// Blinking/pulsing feel (the "signal" look the player asked to reuse from
				// the vanilla haven-under-attack indicator) without touching that vanilla
				// system at all: GeoSiteVisualsController.RefreshAnimations/
				// GeoUpdatedableMissionVisualsController are both driven by state this mod
				// doesn't own (mission progress shaders, the same hover-highlight toggle
				// GeoscapeView silently owns) — a small MonoBehaviour we control ourselves
				// avoids fighting either of those. Pulses scale, not a shader property, so
				// it works with CreatePrimitive's default material regardless of what
				// shader it turns out to be at runtime.
				HelpMarkerPulse pulse = marker.AddComponent<HelpMarkerPulse>();
				pulse.BaseScale = marker.transform.localScale;

				// World-space label above the marker ("Synedrion Help" etc.), built via
				// reflection instead of a typed `TextMesh`/`Canvas`+`UI.Text`: neither
				// UnityEngine.UIModule nor UnityEngine.TextRenderingModule (Canvas, Font,
				// TextAnchor, FontStyle, TextMesh itself, ...) are in this project's
				// referenced ModSDK DLL set (confirmed by compile error — CS0246/CS0012 —
				// when tried directly; `UnityEngine.UI.Text` only partially resolves, since
				// its `.text`/`.color` properties are plain string/Color but `.font`/
				// `.alignment`/etc. reach into TextRenderingModule). `GameObject.
				// AddComponent(System.Type)` and `PropertyInfo.SetValue`, by contrast, only
				// need `System.Type`/`System.Reflection` — resolving "TextMesh" by its
				// runtime-qualified name works because the actual running game process has
				// that module loaded even though our own assembly never references it.
				Component textMesh = TryAddTextMeshLabel(marker, factionDisplayName, color);
				if (textMesh != null)
				{
					// Faces the Geoscape camera at any zoom/angle rather than being legible
					// only from whatever direction it happened to spawn facing.
					HelpMarkerBillboard billboard = textMesh.gameObject.AddComponent<HelpMarkerBillboard>();
					billboard.PulseTarget = pulse;
					billboard.TextMeshComponent = textMesh;

					// Ticks the label's text down toward 0h and fires onExpired once it
					// hits zero — see HelpMarkerCountdown.
					HelpMarkerCountdown countdown = textMesh.gameObject.AddComponent<HelpMarkerCountdown>();
					countdown.Initialize(site, factionDisplayName, expiresAtHours, textMesh, onExpired);
				}

				Markers[site] = marker;
				string iconBounds = visuals != null && visuals.SiteIconRenderer != null
					? visuals.SiteIconRenderer.bounds.ToString()
					: "<no icon renderer>";
				Log.Debug($"Added help-requested map marker at {site.SiteName.Localize(null)}: "
					+ $"layer={marker.layer}, worldPos={marker.transform.position}, "
					+ $"lossyScale={marker.transform.lossyScale}, iconBounds={iconBounds}, "
					+ $"iconActive={(visuals != null && visuals.SiteIconRenderer != null && visuals.SiteIconRenderer.gameObject.activeInHierarchy)}.");
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to create help-requested map marker: " + ex);
			}
		}

		public static void Hide(GeoSite site)
		{
			if (site == null || !Markers.TryGetValue(site, out GameObject marker))
				return;
			Markers.Remove(site);
			if (marker != null)
				UnityEngine.Object.Destroy(marker);
		}

		/// <summary>
		/// Updates an already-shown marker's color and label text in place — explicit
		/// request: a debug activity should first show as an unknown/neutral signal, then
		/// "reveal" (updated color + faction name) once the player investigates the site
		/// (see DebugActivityTracker.RevealIfNeeded). No-ops if no marker is currently
		/// tracked for this site (already expired/removed) — same stale-entry-safe
		/// TryGetValue + `!= null` check Show() itself uses, for the same reason.
		/// </summary>
		public static void UpdateAppearance(GeoSite site, string factionDisplayName, Color color)
		{
			if (site == null || !Markers.TryGetValue(site, out GameObject marker) || marker == null)
				return;
			try
			{
				Renderer markerRenderer = marker.GetComponent<Renderer>();
				if (markerRenderer != null)
					markerRenderer.material.color = color;

				HelpMarkerCountdown countdown = marker.GetComponentInChildren<HelpMarkerCountdown>();
				countdown?.UpdateDisplayName(factionDisplayName);

				HelpMarkerBillboard billboard = marker.GetComponentInChildren<HelpMarkerBillboard>();
				billboard?.UpdateBaseColor(color);
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to update help-requested map marker appearance: " + ex);
			}
		}

		/// Separate from Markers — a Pandoran base tracked by
		/// HavenSelfDefensePandoranSiteAttackTracker is, by construction, never also
		/// showing an ordinary help-request/activity marker at the same time (it can only
		/// be tracked while unrevealed, and a site the player can't yet see can't have had
		/// a context-menu action taken on it), but keeping this on its own dictionary
		/// avoids ever having to rely on that reasoning staying true.
		private static readonly Dictionary<GeoSite, GameObject> CounterMarkers = new Dictionary<GeoSite, GameObject>();

		/// <summary>
		/// Debug-only, no-expiry marker for HavenSelfDefensePandoranSiteAttackTracker —
		/// explicit request: "when chaos_factions_debug = 1 to show that counter on map
		/// where it is higher than 0... reuse existing logic which you used to display
		/// activity and pending help request". Reuses the same sphere + HelpMarkerPulse +
		/// reflection-built TextMesh label + HelpMarkerBillboard pieces Show() uses, but
		/// deliberately without HelpMarkerCountdown — that component is built specifically
		/// around ticking a label down toward an expiry hour and firing a callback at 0,
		/// which doesn't fit a counter that only changes on a self-defense event, not
		/// continuously. Creates the marker on first call for a site, or just updates its
		/// label/color text in place on every call after (the same
		/// create-or-refresh-in-one-call shape UpdateAppearance/Show together already
		/// provide for the other marker kind, just collapsed into one method here since
		/// there's no separate "first reveal" moment to distinguish for this one).
		/// </summary>
		public static void ShowOrUpdateCounter(GeoSite site, string labelText, Color color)
		{
			if (site == null)
				return;
			if (CounterMarkers.TryGetValue(site, out GameObject existing) && existing != null)
			{
				try
				{
					Renderer existingRenderer = existing.GetComponent<Renderer>();
					if (existingRenderer != null)
						existingRenderer.material.color = color;
					HelpMarkerBillboard existingBillboard = existing.GetComponentInChildren<HelpMarkerBillboard>();
					if (existingBillboard?.TextMeshComponent != null)
						SetProp(existingBillboard.TextMeshComponent, "text", labelText);
					existingBillboard?.UpdateBaseColor(color);
				}
				catch (System.Exception ex)
				{
					Log.Debug("Failed to update Pandoran attack-counter map marker: " + ex);
				}
				return;
			}
			try
			{
				// Deliberately NOT parented under SiteIconRenderer.transform the way
				// Show() parents the ordinary marker — reported in-game: the counter
				// marker never actually appeared for an unrevealed site. Root cause,
				// confirmed by reading GeoSiteVisualsController: an unrevealed site's
				// icon sub-hierarchy (LocationIconParent, which SiteIconRenderer sits
				// under) is kept GameObject.SetActive(false) by ReplaceSiteVisuals/OnEnable
				// whenever IsVisivble() is false — exactly the case for every site this
				// tracker targets, since it only ever tracks sites GetVisible() already
				// returned false for. Any child parented under that hierarchy inherits
				// activeInHierarchy == false and never renders, regardless of its own
				// activeSelf. Fixed by creating this marker unparented (a root-level
				// object) at an explicit world position instead — Transform.position/.up
				// are still readable off SiteIconRenderer even while its GameObject is
				// inactive, so the exact same outward-offset direction/magnitude Show()
				// already uses (MarkerLocalOffset.y along the icon's own local "up",
				// which points away from the Geoscape globe's surface at that site,
				// not a fixed world axis) is reused here — just computed as a world-space
				// vector instead of relying on parenting for it. site.transform/.gameObject
				// are used only as a fallback source for position/layer if the icon
				// renderer is ever missing entirely, never as an actual Transform parent.
				GeoSiteVisualsController visuals = site.GetComponent<GeoSiteVisualsController>();
				Transform iconTransform = visuals != null && visuals.SiteIconRenderer != null ? visuals.SiteIconRenderer.transform : null;
				Transform positionSource = iconTransform != null ? iconTransform : site.transform;
				int layer = iconTransform != null ? iconTransform.gameObject.layer : site.gameObject.layer;
				Vector3 worldPos = positionSource.position + positionSource.up * MarkerLocalOffset.y;

				// Reported in-game: once unparented (the fix above), the marker/label
				// rendered far too big. Cause: being a child of positionSource previously
				// meant its final world size was localScale (Vector3.one * MarkerScale)
				// multiplied by that parent's own lossyScale — site icons are scaled well
				// below 1 on the Geoscape, so that multiplication was quietly shrinking it
				// down to the intended size. Unparented, localScale alone *is* the world
				// size, losing that shrink entirely. Baking positionSource's lossyScale
				// into localScale directly reproduces the exact same effective world size
				// as before (the label, parented under the marker, inherits this
				// automatically — no separate fix needed for its own size).
				GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
				marker.name = "ChaosFaction_PandoranAttackCounterMarker";
				marker.layer = layer;
				marker.transform.position = worldPos;
				marker.transform.rotation = Quaternion.identity;
				marker.transform.localScale = Vector3.Scale(Vector3.one * MarkerScale, positionSource.lossyScale);
				Renderer markerRenderer = marker.GetComponent<Renderer>();
				if (markerRenderer != null)
					markerRenderer.material.color = color;

				HelpMarkerPulse pulse = marker.AddComponent<HelpMarkerPulse>();
				pulse.BaseScale = marker.transform.localScale;

				Component textMesh = TryAddTextMeshLabel(marker, labelText, color);
				if (textMesh != null)
				{
					HelpMarkerBillboard billboard = textMesh.gameObject.AddComponent<HelpMarkerBillboard>();
					billboard.PulseTarget = pulse;
					billboard.TextMeshComponent = textMesh;
				}

				CounterMarkers[site] = marker;
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to create Pandoran attack-counter map marker: " + ex);
			}
		}

		public static void HideCounter(GeoSite site)
		{
			if (site == null || !CounterMarkers.TryGetValue(site, out GameObject marker))
				return;
			CounterMarkers.Remove(site);
			if (marker != null)
				UnityEngine.Object.Destroy(marker);
		}

		/// <summary>
		/// Adds a UnityEngine.TextMesh (world-space, no Canvas needed) purely via
		/// reflection, so this project never has to name the type directly — see the Show()
		/// comment above for why. Returns null (logged, not thrown) if the type can't be
		/// resolved at runtime for some reason, so a label failure degrades to "no label"
		/// rather than losing the marker itself.
		/// </summary>
		private static Component TryAddTextMeshLabel(GameObject marker, string text, Color color)
		{
			System.Type textMeshType = System.Type.GetType("UnityEngine.TextMesh, UnityEngine.TextRenderingModule");
			if (textMeshType == null)
			{
				Log.Debug("TextMesh type not resolvable at runtime; skipping help-requested label.");
				return null;
			}

			GameObject labelGo = new GameObject("ChaosFaction_HelpRequestedLabel");
			labelGo.layer = marker.layer;
			labelGo.transform.SetParent(marker.transform, false);
			labelGo.transform.localPosition = new Vector3(0f, LabelLocalHeight, 0f);
			labelGo.transform.localRotation = Quaternion.identity;

			Component textMesh = labelGo.AddComponent(textMeshType);
			SetProp(textMesh, "text", text);
			SetProp(textMesh, "color", color);
			SetProp(textMesh, "characterSize", LabelCharacterSize);
			SetProp(textMesh, "fontSize", LabelFontSize);

			System.Type anchorType = System.Type.GetType("UnityEngine.TextAnchor, UnityEngine.TextRenderingModule");
			if (anchorType != null)
				SetProp(textMesh, "anchor", System.Enum.Parse(anchorType, "MiddleCenter"));
			System.Type alignType = System.Type.GetType("UnityEngine.TextAlignment, UnityEngine.TextRenderingModule");
			if (alignType != null)
				SetProp(textMesh, "alignment", System.Enum.Parse(alignType, "Center"));

			return textMesh;
		}

		private static void SetProp(Component comp, string propName, object value)
		{
			System.Reflection.PropertyInfo prop = comp.GetType().GetProperty(propName);
			if (prop != null)
				prop.SetValue(comp, value, null);
		}
	}

	/// <summary>
	/// Pulses a marker's scale (and, via HelpMarkerBillboard, its label's alpha in lockstep)
	/// to read as a "blinking signal" without needing shader emission support from
	/// CreatePrimitive's default material.
	/// </summary>
	internal class HelpMarkerPulse : MonoBehaviour
	{
		private const float PulseSpeed = 2.5f;
		private const float PulseAmplitude = 0.18f;

		public Vector3 BaseScale = Vector3.one;

		public float CurrentPulse01 { get; private set; } = 1f;

		private void Update()
		{
			float t = (Mathf.Sin(Time.time * PulseSpeed) + 1f) * 0.5f;
			CurrentPulse01 = t;
			transform.localScale = BaseScale * (1f + PulseAmplitude * t);
		}
	}

	/// <summary>
	/// Keeps the help-requested label facing whatever camera is rendering it (Geoscape's
	/// camera angle/zoom both change), and fades its alpha with the marker's own pulse so
	/// the two blink together.
	/// </summary>
	internal class HelpMarkerBillboard : MonoBehaviour
	{
		public HelpMarkerPulse PulseTarget;

		/// The TextMesh component (see HelpRequestedMarker.TryAddTextMeshLabel) — held as a
		/// plain Component since this project can't name the TextMesh type directly; its
		/// "color" property is read/written via cached reflection instead.
		public Component TextMeshComponent;

		private System.Reflection.PropertyInfo _colorProp;
		private Color _baseColor;
		private bool _colorInitialized;

		/// <summary>
		/// Reported in-game: a revealed activity's marker/label never actually changed
		/// color, staying stuck at the "unknown" gray. Root cause: `TryAddTextMeshLabel`'s
		/// caller (`Show`) does `AddComponent&lt;HelpMarkerBillboard&gt;()` *then* assigns
		/// `TextMeshComponent` on the result — but Unity calls `Awake()` synchronously,
		/// during `AddComponent` itself, before that assignment ever runs. The original
		/// `Awake()` resolved `_colorProp` there, so it always saw `TextMeshComponent ==
		/// null`, hit its own early-return guard, and left `_colorProp` permanently null —
		/// `Update()`'s `if (_colorProp != null ...)` guard then always failed, so the
		/// entire per-frame color/pulse-alpha application (and, by extension,
		/// `UpdateBaseColor`, which only ever updates a field that code path would have
		/// read) never ran at all. The label was frozen at whatever `TryAddTextMeshLabel`'s
		/// own direct `SetProp(textMesh, "color", color)` set at creation time, forever.
		/// Fix: resolve `_colorProp`/`_baseColor` lazily, on first `Update()`, by which
		/// point `Show()` has long since finished assigning `TextMeshComponent` (`Show()`
		/// runs entirely synchronously within one frame, well before Unity's next `Update`
		/// pass) — no `Awake()` needed at all.
		/// </summary>
		private void TryInitializeColor()
		{
			_colorInitialized = true;
			if (TextMeshComponent == null)
				return;
			_colorProp = TextMeshComponent.GetType().GetProperty("color");
			if (_colorProp != null)
				_baseColor = (Color)_colorProp.GetValue(TextMeshComponent, null);
		}

		private void Update()
		{
			Camera cam = Camera.main;
			if (cam != null)
				transform.rotation = cam.transform.rotation;

			if (!_colorInitialized)
				TryInitializeColor();

			if (_colorProp != null && PulseTarget != null)
			{
				float alpha = Mathf.Lerp(0.55f, 1f, PulseTarget.CurrentPulse01);
				_colorProp.SetValue(TextMeshComponent, new Color(_baseColor.r, _baseColor.g, _baseColor.b, alpha), null);
			}
		}

		/// <summary>Changes which color the label pulses toward — Update() already re-applies
		/// _baseColor (blended with the current pulse alpha) every frame, so just updating
		/// the field here is enough; no need to push the color onto TextMeshComponent
		/// directly.</summary>
		public void UpdateBaseColor(Color color)
		{
			_baseColor = color;
		}
	}

	/// <summary>
	/// Ticks the marker label's text down toward "0.0h" (checked once a second, not every
	/// frame — cheap enough either way with at most a handful of markers alive at once, but
	/// no reason to recompute in-game time every frame) and invokes onExpired once the
	/// request's time is up, so AllyRequestTracker can unmark it (no refund) — "if the
	/// player didn't come to attack, the marker disappears" was the explicit request. Reads
	/// GeoSite.GeoLevel.Timing directly rather than going through GameUtl.CurrentLevel(),
	/// since the site reference is already on hand and this avoids one extra lookup.
	/// </summary>
	internal class HelpMarkerCountdown : MonoBehaviour
	{
		private const float UpdateIntervalSeconds = 1f;

		private GeoSite _site;
		private string _factionDisplayName;
		private double _expiresAtHours;
		private Component _textMesh;
		private System.Reflection.PropertyInfo _textProp;
		private System.Action _onExpired;
		private float _timeSinceUpdate;
		private bool _expired;

		public void Initialize(GeoSite site, string factionDisplayName, double expiresAtHours, Component textMesh, System.Action onExpired)
		{
			_site = site;
			_factionDisplayName = factionDisplayName;
			_expiresAtHours = expiresAtHours;
			_textMesh = textMesh;
			_textProp = textMesh?.GetType().GetProperty("text");
			_onExpired = onExpired;
			RefreshLabel();
		}

		/// <summary>Swaps in a new display name (e.g. once a debug activity is revealed —
		/// see DebugActivityTracker.RevealIfNeeded) and immediately redraws the label so the
		/// change doesn't wait for the next 1-second tick.</summary>
		public void UpdateDisplayName(string factionDisplayName)
		{
			_factionDisplayName = factionDisplayName;
			RefreshLabel();
		}

		private void Update()
		{
			if (_expired)
				return;
			_timeSinceUpdate += Time.deltaTime;
			if (_timeSinceUpdate < UpdateIntervalSeconds)
				return;
			_timeSinceUpdate = 0f;
			RefreshLabel();
		}

		private void RefreshLabel()
		{
			if (_site == null || _site.GeoLevel == null)
				return;
			double remaining = _expiresAtHours - AllyRequestTracker.CurrentHours(_site.GeoLevel);
			if (remaining <= 0)
			{
				// Marked before invoking the callback: ExpireRequest ends up calling
				// HelpRequestedMarker.Hide, which destroys this component's own GameObject
				// — safe to do from within our own Update (Unity just defers the actual
				// destruction to end of frame), but guard against this same countdown
				// somehow ticking again in the meantime regardless.
				_expired = true;
				_onExpired?.Invoke();
				return;
			}
			if (_textProp != null)
				_textProp.SetValue(_textMesh, $"{_factionDisplayName} ({remaining:0.0}h)", null);
		}
	}

	/// <summary>
	/// One faction's current "price" for helping: which resource it wants (instead of
	/// always Materials), how much of it, and how much relationship it'd cost instead.
	/// See FactionHelpOfferTracker for how/when these get (re)rolled.
	/// </summary>
	internal class FactionHelpOffer
	{
		public ResourceType ResourceType;
		public int ResourceAmount;
		public int RelationshipAmount;
	}

	/// <summary>
	/// Rolls and tracks each of the three helper factions' current help-request "price" —
	/// independent per faction, not shared. Two things get rolled together as one unit per
	/// faction: which resource it wants (a permutation of Materials/Supplies("food")/Tech
	/// across the three factions, so no two factions ever want the same resource at once)
	/// and how much of it (plus how much relationship, as the alternative payment). Both
	/// amounts are `ChaosFactionConfig.HelpResourcesCost`/`HelpRelationshipCost` (the base
	/// values) times an independent roll each, within ±`HelpCostVariancePercent` (config,
	/// default 25 = ±25%) — "independent" meaning a faction's resource-amount roll and
	/// relationship-amount roll don't share one multiplier, and different factions' rolls
	/// don't share one either.
	/// Tech is priced at 1/5 the rolled value (`TechCostDivisor`) since it's treated as
	/// worth 5x a unit of Materials/Supplies — "tech is more costly" per the request that
	/// prompted this.
	///
	/// Reroll triggers, per explicit request:
	/// - A full reroll (fresh resource-type permutation *and* fresh amounts for all three
	///   factions) happens whenever EnsureRolled notices the in-game day has advanced
	///   since the last roll (`GeoLevelController.Timing`, day count via `(Now -
	///   StartTime).TimeSpan.TotalDays`) — checked every time the context menu is opened
	///   (ContextualAbilitiesPatchHelper), not on a separate timer, so no polling needed.
	/// - A single faction's request being used doesn't touch the resource-type
	///   permutation (rerolling just one slot could only ever put it back to a type
	///   another faction already has, or leave it unchanged — not meaningfully "fresh"
	///   without reshuffling all three, which is what the daily reroll already does) —
	///   RerollAfterUse only rerolls that faction's own resource/relationship *amounts*,
	///   keeping its resource type as-is, so "next time" still shows different numbers
	///   without needing to touch the other two factions' unrelated offers.
	/// </summary>
	internal static class FactionHelpOfferTracker
	{
		private const float TechCostDivisor = 5f;

		/// Phoenix Point has no resource literally called "Food" — Supplies is the closest
		/// in-game equivalent (sustenance/upkeep), so that's what "food" maps to here.
		private static readonly ResourceType[] RollableResourceTypes = { ResourceType.Supplies, ResourceType.Materials, ResourceType.Tech };

		/// <summary>
		/// Explicit follow-up request: "when you generate cost for mission it should be
		/// per faction per siteType per DifficultyThreatLevel" — a 3-way key. Base
		/// values now come from `MissionHelpSettingsResolver.Get(DifficultyThreatLevel?,
		/// ...)` (threat level, not mission type), but the *offer cache itself* still
		/// keys on mission type too, deliberately — a Nest assault and a Lair assault
		/// both at High threat share the same base value range, but still each get their
		/// own independently-rolled number, exactly the same "same base, independent
		/// roll" reasoning this tracker already used for per-mission-type pricing before
		/// threat level existed as a concept here.
		/// </summary>
		private static readonly Dictionary<(PPFactionDef, HelpMissionType, DifficultyThreatLevel), FactionHelpOffer> Offers =
			new Dictionary<(PPFactionDef, HelpMissionType, DifficultyThreatLevel), FactionHelpOffer>();
		private static int _rolledOnDay = int.MinValue;

		/// <summary>
		/// Returns null if the faction isn't one of the three tracked helper factions (or
		/// isn't present in this game — e.g. a game mode without Anu) — callers already
		/// only ask about factions they've resolved as real candidates, so this is mostly
		/// a defensive fallback, not an expected path. missionType/threatLevel null
		/// (couldn't be resolved) fall back to PhoenixBaseDefense/Low respectively, same
		/// as every other consumer of these two resolvers.
		/// </summary>
		public static FactionHelpOffer GetOffer(GeoLevelController geoLevel, GeoFaction faction, HelpMissionType? missionType, DifficultyThreatLevel? threatLevel, ChaosFactionConfig cfg)
		{
			EnsureRolled(geoLevel, cfg);
			(PPFactionDef, HelpMissionType, DifficultyThreatLevel) key = (faction.PPFactionDef, missionType ?? HelpMissionType.PhoenixBaseDefense, threatLevel ?? DifficultyThreatLevel.Low);
			return Offers.TryGetValue(key, out FactionHelpOffer offer) ? offer : null;
		}

		/// <summary>
		/// Resource-type assignment (the Materials/Supplies/Tech permutation) stays purely
		/// per-faction — reshuffling it per mission type/threat level would mean the same
		/// faction could want a different resource depending which site's menu you're
		/// looking at, which reads as inconsistent/confusing rather than meaningfully
		/// "fresh" (same reasoning RerollAfterUse's own doc comment already gives for not
		/// reshuffling resource type on a single-faction reroll). Only the rolled amounts
		/// vary per (mission type, threat level) pair. Rolls (or re-rolls, on a new day)
		/// every (faction, missionType, threatLevel) combination up front the first time
		/// *any* of them is asked for that day, rather than lazily — simpler than tracking
		/// which combinations today's roll already covered, and the cost of rolling all of
		/// them is still negligible (3 factions × 7 mission types × 4 threat levels = 84).
		/// </summary>
		private static void EnsureRolled(GeoLevelController geoLevel, ChaosFactionConfig cfg)
		{
			int today = CurrentDay(geoLevel);
			if (today == _rolledOnDay && Offers.Count > 0)
				return;

			List<GeoFaction> factions = new List<GeoFaction> { geoLevel.NewJerichoFaction, geoLevel.AnuFaction, geoLevel.SynedrionFaction }
				.Where(f => f != null)
				.ToList();
			List<ResourceType> shuffledTypes = RollableResourceTypes.OrderBy(_ => Random.value).ToList();

			Offers.Clear();
			for (int i = 0; i < factions.Count; i++)
			{
				ResourceType resourceType = shuffledTypes[i % shuffledTypes.Count];
				foreach (HelpMissionType type in AllMissionTypes)
				{
					foreach (DifficultyThreatLevel threatLevel in AllThreatLevels)
					{
						FactionHelpOffer offer = new FactionHelpOffer { ResourceType = resourceType };
						RollAmounts(offer, threatLevel, cfg);
						Offers[(factions[i].PPFactionDef, type, threatLevel)] = offer;
					}
				}
				Log.Debug($"Rolled daily help offers for {factions[i].Def.name} (resource {resourceType}) across all {AllMissionTypes.Length} mission types × {AllThreatLevels.Length} threat levels.");
			}
			_rolledOnDay = today;
		}

		private static readonly HelpMissionType[] AllMissionTypes = (HelpMissionType[])System.Enum.GetValues(typeof(HelpMissionType));
		private static readonly DifficultyThreatLevel[] AllThreatLevels = (DifficultyThreatLevel[])System.Enum.GetValues(typeof(DifficultyThreatLevel));

		/// <summary>
		/// Called once a faction's help is actually used for a specific (mission type,
		/// threat level) pair (AllyRequestTracker.MarkRequested) so its price for *that*
		/// exact pair is different "next time" — its price for every other mission
		/// type/threat level combination is untouched, same "narrowest change that
		/// satisfies the request" reasoning the original per-faction-only version already
		/// used for not touching the other two factions.
		/// </summary>
		public static void RerollAfterUse(GeoFaction faction, HelpMissionType? missionType, DifficultyThreatLevel? threatLevel, ChaosFactionConfig cfg)
		{
			HelpMissionType resolvedType = missionType ?? HelpMissionType.PhoenixBaseDefense;
			DifficultyThreatLevel resolvedThreatLevel = threatLevel ?? DifficultyThreatLevel.Low;
			if (!Offers.TryGetValue((faction.PPFactionDef, resolvedType, resolvedThreatLevel), out FactionHelpOffer offer))
				return;
			RollAmounts(offer, resolvedThreatLevel, cfg);
			Log.Debug($"Rerolled help offer for {faction.Def.name} ({resolvedType}, {resolvedThreatLevel} threat) after use: {offer.ResourceAmount} {offer.ResourceType} or -{offer.RelationshipAmount} Relationship.");
		}

		private static void RollAmounts(FactionHelpOffer offer, DifficultyThreatLevel threatLevel, ChaosFactionConfig cfg)
		{
			MissionHelpSettings settings = MissionHelpSettingsResolver.Get(threatLevel, cfg);

			// HelpCostVariancePercent (25 = ±25%, global) drives the roll range directly,
			// rather than a hardcoded 0.75-1.25 — clamped so a 0% (or negative,
			// misconfigured) value degrades to "no variance" (both bounds 1.0) instead of
			// Random.Range(1, 1) edge weirdness or an inverted/negative multiplier.
			float variance = Mathf.Max(0, cfg.HelpCostVariancePercent) / 100f;
			float varianceMin = 1f - variance;
			float varianceMax = 1f + variance;

			int baseResourceValue = Mathf.Max(1, Mathf.RoundToInt(settings.ResourcesCost * Random.Range(varianceMin, varianceMax)));
			offer.ResourceAmount = offer.ResourceType == ResourceType.Tech
				? Mathf.Max(1, Mathf.RoundToInt(baseResourceValue / TechCostDivisor))
				: baseResourceValue;
			offer.RelationshipAmount = Mathf.Max(1, Mathf.RoundToInt(settings.RelationshipCost * Random.Range(varianceMin, varianceMax)));
		}

		private static int CurrentDay(GeoLevelController geoLevel)
		{
			return (int)(geoLevel.Timing.Now - geoLevel.Timing.StartTime).TimeSpan.TotalDays;
		}
	}

	/// <summary>
	/// The seven mission types this mod supports Ask-for-Help/debug activities for — same
	/// classification `ContextualAbilitiesPatchHelper.IsEligibleMission`/
	/// `IsEligibleAlienBaseType` already used via individual `EnableFor*` config toggles,
	/// now also the key for per-mission-type squad/cost settings (explicit follow-up
	/// request: "for each supported mission type... add individual modifiers"). See
	/// `ContextualAbilitiesPatchHelper.ResolveMissionType` for how a live `GeoMission`
	/// maps to one of these, and `MissionHelpSettingsResolver.Get` for reading the
	/// matching block of `ChaosFactionConfig` fields.
	/// </summary>
	internal enum HelpMissionType
	{
		PhoenixBaseDefense,
		PandoranNestAssault,
		PandoranLairAssault,
		PandoranCitadelAssault,
		PandoranPalaceAssault,
		PhoenixBaseInfestation,
		HavenInfestation,
	}

	/// <summary>
	/// The 8 per-threat-level-configurable settings, resolved for one specific
	/// `DifficultyThreatLevel` — originally split per `HelpMissionType` (7 blocks, 56
	/// fields total), replaced with one block of 8 per threat level (4 blocks, 32 fields
	/// total) per explicit follow-up request, so e.g. an Extreme-threat mission can offer
	/// a bigger squad / cost more than a Low-threat one, regardless of which of the 7
	/// mission types either happens to be. Deliberately does *not* include
	/// `NewJerichoVehicleUnitSize`/`SynedrionVehicleUnitSize`/`MutogUnitSize` — those
	/// describe a faction's own vehicles/Mutogs (how many slots one costs), not something
	/// that varies by threat level, and were never asked to become per-threat-level.
	/// </summary>
	internal readonly struct MissionHelpSettings
	{
		public readonly int ResourcesCost;
		public readonly int RelationshipCost;
		public readonly int MinSquadSize;
		public readonly int MaxSquadSize;
		public readonly int MaxNewJerichoVehiclesInSquad;
		public readonly int MaxSynedrionVehiclesInSquad;
		public readonly int MaxAnuMutogsInSquad;
		public readonly int VehicleSpawnChancePercent;

		public MissionHelpSettings(int resourcesCost, int relationshipCost, int minSquadSize, int maxSquadSize,
			int maxNewJerichoVehiclesInSquad, int maxSynedrionVehiclesInSquad, int maxAnuMutogsInSquad, int vehicleSpawnChancePercent)
		{
			ResourcesCost = resourcesCost;
			RelationshipCost = relationshipCost;
			MinSquadSize = minSquadSize;
			MaxSquadSize = maxSquadSize;
			MaxNewJerichoVehiclesInSquad = maxNewJerichoVehiclesInSquad;
			MaxSynedrionVehiclesInSquad = maxSynedrionVehiclesInSquad;
			MaxAnuMutogsInSquad = maxAnuMutogsInSquad;
			VehicleSpawnChancePercent = vehicleSpawnChancePercent;
		}
	}

	/// <summary>
	/// One shared place mapping a `DifficultyThreatLevel` to its matching block of 8
	/// `ChaosFactionConfig` fields, so no other code needs its own copy of this 4-way
	/// switch. Replaced the original mission-type-keyed version (7 blocks) per explicit
	/// follow-up request — see `ContextualAbilitiesPatchHelper.ResolveThreatLevel` for
	/// how a live `GeoMission` resolves to one of these 4 levels (the same signal the
	/// mission-brief popup's own Threat Level row already shows). `threatLevel == null`
	/// (no live `GeoMission` to resolve from at all — a defensive-only edge case, since
	/// `ResolveThreatLevel` itself always returns a real value for any non-null mission)
	/// falls back to the `Low` block, the enum's own first/default value, rather than
	/// crashing or silently using zeroes.
	/// </summary>
	internal static class MissionHelpSettingsResolver
	{
		public static MissionHelpSettings Get(DifficultyThreatLevel? threatLevel, ChaosFactionConfig cfg)
		{
			switch (threatLevel)
			{
				case DifficultyThreatLevel.Medium:
					return new MissionHelpSettings(cfg.MediumThreatHelpResourcesCost, cfg.MediumThreatHelpRelationshipCost,
						cfg.MediumThreatHelpMinSquadSize, cfg.MediumThreatHelpMaxSquadSize,
						cfg.MediumThreatHelpMaxNewJerichoVehiclesInSquad, cfg.MediumThreatHelpMaxSynedrionVehiclesInSquad,
						cfg.MediumThreatHelpMaxAnuMutogsInSquad, cfg.MediumThreatVehicleSpawnChancePercent);
				case DifficultyThreatLevel.High:
					return new MissionHelpSettings(cfg.HighThreatHelpResourcesCost, cfg.HighThreatHelpRelationshipCost,
						cfg.HighThreatHelpMinSquadSize, cfg.HighThreatHelpMaxSquadSize,
						cfg.HighThreatHelpMaxNewJerichoVehiclesInSquad, cfg.HighThreatHelpMaxSynedrionVehiclesInSquad,
						cfg.HighThreatHelpMaxAnuMutogsInSquad, cfg.HighThreatVehicleSpawnChancePercent);
				case DifficultyThreatLevel.Extreme:
					return new MissionHelpSettings(cfg.ExtremeThreatHelpResourcesCost, cfg.ExtremeThreatHelpRelationshipCost,
						cfg.ExtremeThreatHelpMinSquadSize, cfg.ExtremeThreatHelpMaxSquadSize,
						cfg.ExtremeThreatHelpMaxNewJerichoVehiclesInSquad, cfg.ExtremeThreatHelpMaxSynedrionVehiclesInSquad,
						cfg.ExtremeThreatHelpMaxAnuMutogsInSquad, cfg.ExtremeThreatVehicleSpawnChancePercent);
				case DifficultyThreatLevel.Low:
				default:
					return new MissionHelpSettings(cfg.LowThreatHelpResourcesCost, cfg.LowThreatHelpRelationshipCost,
						cfg.LowThreatHelpMinSquadSize, cfg.LowThreatHelpMaxSquadSize,
						cfg.LowThreatHelpMaxNewJerichoVehiclesInSquad, cfg.LowThreatHelpMaxSynedrionVehiclesInSquad,
						cfg.LowThreatHelpMaxAnuMutogsInSquad, cfg.LowThreatVehicleSpawnChancePercent);
			}
		}
	}

	/// <summary>
	/// Which of the three tracked helper factions agreed to help — a closed set, small
	/// enough to serialize as a plain int rather than needing a PPFactionDef reference
	/// (Def asset references and save-data primitives don't mix well without extra
	/// plumbing this mod has no other need for) in ChaosFactionGeoscape's save data.
	/// </summary>
	internal enum HelperFactionKind
	{
		NewJericho,
		Anu,
		Synedrion,
	}

	internal static class HelperFactionKindExtensions
	{
		/// <summary>Returns null if faction isn't one of the three tracked helpers.</summary>
		public static HelperFactionKind? FromFaction(GeoLevelController geoLevel, GeoFaction faction)
		{
			if (geoLevel == null || faction == null)
				return null;
			if (faction == geoLevel.NewJerichoFaction)
				return HelperFactionKind.NewJericho;
			if (faction == geoLevel.AnuFaction)
				return HelperFactionKind.Anu;
			if (faction == geoLevel.SynedrionFaction)
				return HelperFactionKind.Synedrion;
			return null;
		}

		/// <summary>Returns null if this game/DLC config doesn't have that faction at all.</summary>
		public static GeoFaction ToFaction(this HelperFactionKind kind, GeoLevelController geoLevel)
		{
			if (geoLevel == null)
				return null;
			switch (kind)
			{
				case HelperFactionKind.NewJericho: return geoLevel.NewJerichoFaction;
				case HelperFactionKind.Anu: return geoLevel.AnuFaction;
				case HelperFactionKind.Synedrion: return geoLevel.SynedrionFaction;
				default: return null;
			}
		}

		/// <summary>
		/// Explicit follow-up request ("try to reuse where possible localization from
		/// game"): reads the live GeoFaction's own real localized name
		/// (GeoFaction.Name — a LocalizedTextBind resolving through GeoFactionViewDef.Name,
		/// confirmed via source to be safely readable from any context with the
		/// live GeoFaction in hand, unlike ModalCommonResources.GetFactionInfo, which is
		/// only ever a serialized field on already-open UI modals, not a generically
		/// resolvable service) instead of a hardcoded English literal — matters most for
		/// Anu, whose actual in-game display name is "Disciples of Anu", not the bare "Anu"
		/// this mod's own fallback used to show unconditionally. Falls back to the game's own
		/// KEY_FACTION_NAME_NEW_JERICHO term (found in ChaosUkrainian/SourceLocalization —
		/// reused instead of a new mod-owned key, per explicit follow-up request: "reuse as
		/// much as possible to not create additional translation keys") only if geoLevel is
		/// null or the live faction can't be resolved (e.g. this game/DLC configuration
		/// doesn't have it).
		/// </summary>
		public static string DisplayName(this HelperFactionKind kind, GeoLevelController geoLevel)
		{
			GeoFaction faction = kind.ToFaction(geoLevel);
			if (faction?.Name != null)
				return faction.Name.Localize(null);
			return kind == HelperFactionKind.NewJericho ? new LocalizedTextBind("KEY_FACTION_NAME_NEW_JERICHO").Localize(null) : kind.ToString();
		}
	}

	/// <summary>
	/// A help request that's been paid for but not yet resolved (mission not yet launched,
	/// cancelled, or expired) — everything AllyRequestTracker needs to know about it, and
	/// everything ChaosFactionGeoscape needs to persist across a save/load (via the
	/// site's SiteId, the one piece not carried directly on this class — see
	/// ChaosFactionGSInstanceData/ChaosFactionSavedHelpRequest).
	/// </summary>
	internal class PendingHelpRequest
	{
		public HelperFactionKind HelperKind;
		public AskForHelpPaymentMethod Method;
		public FactionHelpOffer Offer;
		public double ExpiresAtHours;

		/// <summary>
		/// The request's own pre-picked squad — moved onto the request itself (rather
		/// than staying as a separate parameter passed into WireMissionLifecycle at
		/// wire time) as part of fixing a real reported bug in the equivalent debug-
		/// activity code path (DebugActivityTracker.WireMissionLifecycle's own doc
		/// comment has the full root-cause story; this tracker had the identical
		/// vulnerability, just never reported/confirmed here). Null for a request
		/// restored after a save/load (TacCharacterDef references aren't safe to
		/// serialize), rebuilt fresh at actual launch time instead, from HelperKind.
		/// </summary>
		public List<TacCharacterDef> Squad;

		/// <summary>
		/// The DifficultyThreatLevel Squad above was actually built against — explicit
		/// follow-up request: "I want to actually update the squad size to match
		/// current threat level, it is possible that it will change... rebuild squad
		/// logic to actual threat level." A paid request can sit pending for up to
		/// PendingHelpHoursMax (default up to 28h) before the player actually launches
		/// it, and WireMissionLifecycle's OnMissionActivated handler re-resolves threat
		/// level fresh at that later point — if it no longer matches what this field
		/// records, the squad is stale (rolled against the wrong MissionHelpSettings
		/// block) and gets rebuilt from scratch instead of reused as-is. Null for a
		/// request restored after a save/load, same as Squad — both stay unset together
		/// (restored requests already always rebuild fresh at launch, so there's
		/// nothing to compare against).
		/// </summary>
		public DifficultyThreatLevel? SquadThreatLevel;

		/// The Geoscape "objective" entry (top-left menu, alongside native ones like "Stop
		/// enemy invasion at haven X") shown while this request is active — set by
		/// AllyRequestTracker.AddActiveRequest, removed by RemoveActiveRequest. Kept here
		/// (not looked up again later) so removal always targets the exact same instance
		/// that was added, matching GeoFaction.AddObjective/RemoveObjective's own
		/// reference-based bookkeeping.
		public AskForHelpGeoFactionObjective Objective;
	}

	/// <summary>
	/// Geoscape objective shown while a help request (player-paid or free, from
	/// HavenDefenseRewardTracker) is active — "Await New Jericho's help at Some Base",
	/// alongside the countdown to expiry, mirroring vanilla's own per-mission objectives
	/// (MissionGeoFactionObjective is the closest vanilla equivalent, tied to one GeoMission
	/// the same way this is tied to one GeoSite). Deliberately mirrors that vanilla class's
	/// exact shape — [SerializeType], [SerializeMember] on live-reference fields, a private
	/// parameterless constructor for the deserializer — since GeoFactionObjective instances
	/// live inside GeoFaction._objectives, itself part of normal save data, so this gets
	/// swept into the save file whether we want it to or not; matching the one
	/// already-proven-to-work vanilla pattern is safer than inventing an untested one.
	/// That said, this mod's OWN save data (ChaosFactionGSInstanceData) — not whatever
	/// vanilla's serializer does with this object graph on load — is the actual source of
	/// truth for restoring a pending request; see AllyRequestTracker.RestorePendingRequests,
	/// which defensively clears out any stray instance of this class already present in
	/// GeoFaction.Objectives before rebuilding fresh ones, precisely to avoid relying on
	/// unverified round-trip behavior for a modded subclass of a serialized vanilla type.
	/// </summary>
	/// <summary>
	/// Shared by AskForHelpGeoFactionObjective and DebugActivityGeoFactionObjective so
	/// Patch_GeoObjectiveElementController_Update (ChaosFactionPatches.cs) can drive the
	/// top-left objectives panel's live "Xh remaining" countdown text for either kind of
	/// mod-added objective through one shared code path instead of two near-duplicates.
	/// </summary>
	internal interface IChaosTimedObjective
	{
		bool TryGetRemainingHours(out double hours);

		/// Lets Patch_GeoObjectiveElementController_Update remove itself from the
		/// top-left panel the moment TryGetRemainingHours starts returning false — see
		/// that patch's own doc comment for why this is needed even though the owning
		/// tracker (AllyRequestTracker/DebugActivityTracker) already calls RemoveObjective
		/// on its own tracked instance: a scene reload (any mission launch, not just a
		/// true save/load) round-trips GeoFaction._objectives through vanilla's own
		/// serializer, which can resurrect a disconnected copy of this [SerializeType]
		/// objective that the tracker never sees and therefore never removes.
		GeoFaction ResolveOwningFaction();
	}

	[SerializeType]
	internal class AskForHelpGeoFactionObjective : GeoFactionObjective, IChaosTimedObjective
	{
		[SerializeMember]
		public readonly GeoSite Site;

		[SerializeMember]
		public readonly GeoFactionDef HelperFactionDef;

		/// GivenByFaction is the helper faction itself (not the player), even though this
		/// only ever lives in the *player's* GeoFaction.Objectives list (AddObjective's
		/// caller is always playerFaction, see AllyRequestTracker.AddActiveRequest) — the
		/// base class's non-virtual GetIcon() reads GivenByFaction.GeoFactionViewDef.
		/// FactionIcon directly, so this is what makes the objective actually show the
		/// *helper's* icon (e.g. New Jericho's) rather than the player's own Phoenix icon,
		/// at the cost of sorting into UIModuleGeoObjectives' "secondary" (given by another
		/// faction) bucket instead of "primary" — a reasonable tradeoff since "who's
		/// helping" is the more useful glance-value here than which bucket it's filed under.
		public AskForHelpGeoFactionObjective(GeoSite site, GeoFaction helperFaction)
			: base(helperFaction)
		{
			Site = site;
			HelperFactionDef = helperFaction.Def;
			IsCriticalPath = true;
		}

		/// For the (de)serializer, mirroring MissionGeoFactionObjective's own pattern —
		/// never called directly by this mod's own code.
		private AskForHelpGeoFactionObjective()
		{
		}

		public override string GetTitle()
		{
			return ChaosFactionLocalization.Get("KEY_CHAOSFACTION_AWAIT_HELP_TITLE", HelperFactionDisplayName(), SiteName());
		}

		public override string GetDescription()
		{
			return ChaosFactionLocalization.Get("KEY_CHAOSFACTION_AWAIT_HELP_DESC", HelperFactionDisplayName(), SiteName(), RemainingTimeSuffix());
		}

		/// <summary>
		/// The helper faction's own real localized name (GeoFactionDef.GeoFactionViewDef.
		/// Name — the same field GeoFaction.Name itself reads) instead of
		/// PPFactionDef.GetName()'s raw internal identifier (e.g. "NewJericho" rather than
		/// "New Jericho") — explicit request: reuse the game's own localization instead of
		/// an internal name/hardcoded literal wherever possible.
		/// </summary>
		private string HelperFactionDisplayName()
		{
			return HelperFactionDef.GeoFactionViewDef.Name.Localize(null);
		}

		public override IEnumerable<GeoActor> GetRelatedActors()
		{
			if (Site != null)
				yield return Site;
		}

		public override bool IsCompleted()
		{
			return Site == null || !AllyRequestTracker.ActiveRequests.ContainsKey(Site);
		}

		public override void OnAssignedToFaction(GeoFaction faction)
		{
		}

		public override void OnUnassignedFromFaction(GeoFaction faction)
		{
		}

		private string SiteName() => GeoSiteDisplayHelper.GetDisplayName(Site);

		private string RemainingTimeSuffix()
		{
			if (!TryGetRemainingHours(out double remaining))
				return string.Empty;
			return ChaosFactionLocalization.Get("KEY_CHAOSFACTION_REMAINING_TIME_SUFFIX", $"{Mathf.Max(0f, (float)remaining):0.0}");
		}

		public bool TryGetRemainingHours(out double hours)
		{
			hours = 0;
			if (Site?.GeoLevel == null || !AllyRequestTracker.ActiveRequests.TryGetValue(Site, out PendingHelpRequest request))
				return false;
			hours = request.ExpiresAtHours - AllyRequestTracker.CurrentHours(Site.GeoLevel);
			return true;
		}

		public GeoFaction ResolveOwningFaction() => Site?.GeoLevel?.PhoenixFaction;
	}

	/// <summary>
	/// Cross-cutting runtime state for the "ask a faction for help" feature. Bridges the
	/// Geoscape request (context-menu ability activation) and the Tactical level that
	/// spawns the helpers, since a GeoMission instance never survives the
	/// Geoscape-to-Tactical scene swap.
	/// </summary>
	internal static class AllyRequestTracker
	{
		/// Sites with a paid-for, not-yet-resolved help request — keyed by GeoSite rather
		/// than GeoMission specifically so this survives a save/load: GeoMission objects
		/// don't survive serialization as the same reference (a reload deserializes a
		/// fresh instance for the same logical mission), but GeoSite does (GeoSite.SiteId
		/// is the game's own stable identity for it, confirmed by GeoSite's own save/load
		/// code using it the same way). A site can only ever have one ActiveMission at a
		/// time, so per-site (rather than per-mission) tracking is unambiguous. Entries are
		/// removed the moment the request resolves one way or another — launched
		/// (OnMissionActivated), truly cancelled (OnMissionCancel, once it's confirmed to
		/// not just be a launch-prompt close — see WireMissionLifecycle), or expired
		/// (HelpRequestedMarker's countdown calling back into ExpireRequest) — so a site
		/// whose mission concludes and is later replaced by a genuinely new one is never
		/// incorrectly still "already requested."
		public static readonly Dictionary<GeoSite, PendingHelpRequest> ActiveRequests = new Dictionary<GeoSite, PendingHelpRequest>();

		/// The troop templates picked for the most recently launched help request, set by
		/// the OnMissionActivated handler and consumed by ChaosFactionTactical.OnTacticalStart.
		/// Only one tactical mission can ever be in progress at a time, so a single pending
		/// list (rather than a per-mission map) is sufficient.
		public static List<TacCharacterDef> PendingAllyTemplates = new List<TacCharacterDef>();

		/// Which faction actually agreed to help (New Jericho/Anu/Synedrion), set alongside
		/// PendingAllyTemplates — Tactical-side code (GetOrCreateHelperFaction) needs this to
		/// know which PPFactionDef to instantiate a TacticalFaction for, since it can no
		/// longer assume New Jericho specifically now that any of the three can help.
		public static PPFactionDef PendingHelperFactionDef;

		/// Phoenix base defense only (explicit request): the ally squad arrives as a
		/// delayed reinforcement instead of spawning immediately alongside the player's own
		/// squad. Populated by WireMissionLifecycle's OnMissionActivated handler *instead
		/// of* PendingAllyTemplates/PendingHelperFactionDef when the rolled delay is > 0 —
		/// mutually exclusive with those two for a given request, never both. Consumed (and
		/// immediately cleared) by ChaosFactionTactical.OnTacticalStart into that instance's
		/// own fields, not read directly by OnPlayerTurnAboutToStart — see that class's own doc
		/// comment for why leaving these as bare statics across turns would risk a *later,
		/// unrelated* mission's turn count reaching the same number and spawning phantom
		/// allies.
		public static List<TacCharacterDef> PendingReinforcementTemplates;
		public static PPFactionDef PendingReinforcementFactionDef;
		public static int PendingReinforcementAtTurn = -1;

		/// Everything the ability framework doesn't already check for us: not already used,
		/// and relationship high enough. Resource affordability is handled natively by the
		/// ability's Cost field (PhoenixPoint auto-derives GeoAbilityTargetDisabledState.
		/// NotEnoughResources from it), so it's deliberately not re-checked here.
		public static bool CanRequestHelp(GeoMission mission, GeoFaction playerFaction, GeoFaction helperFaction, ChaosFactionConfig cfg)
		{
			if (mission?.Site == null || ActiveRequests.ContainsKey(mission.Site))
				return false;
			// Mutual exclusivity with debug Pure/Forsaken hostile activities (explicit
			// request) — a site with one of those active shouldn't also be askable for
			// friendly help.
			if (DebugActivityTracker.ActiveActivities.ContainsKey(mission.Site))
				return false;
			// Relationship is stored on the OTHER faction's side (their attitude toward the
			// player), same direction vanilla's own diplomacy UI reads it in
			// (MainDiplomacyRelationDisplayRowElement/UIModuleDiplomacySection) — not the
			// player's own Diplomacy dictionary, which stays at its default (0).
			return helperFaction.Diplomacy.GetDiplomacy(playerFaction) >= cfg.RelationshipThreshold;
		}

		/// <summary>Same in-game-hours convention FactionHelpOfferTracker's day count uses.</summary>
		public static double CurrentHours(GeoLevelController geoLevel)
		{
			return (geoLevel.Timing.Now - geoLevel.Timing.StartTime).TimeSpan.TotalHours;
		}

		/// <summary>
		/// Shared by both this tracker (help-request expiry, PendingHelpHoursMin/Max) and
		/// DebugActivityTracker (activity expiry, PendingActivityHoursMin/Max,
		/// ChaosFactionDebug.cs) — explicit request: roll a random expiry within a
		/// configured range instead of one fixed value, per request/activity. Defensively
		/// swaps min/max first (in case Mod Settings has min > max), mirroring the same
		/// defensive swap pattern already used for HelpMinSquadSize/HelpMaxSquadSize etc.
		/// UnityEngine.Random.Range's float overload is used (not a double-precision roll)
		/// since that's the same RNG every other roll in this mod already goes through —
		/// hour-scale precision loss from the float cast is not meaningful here.
		/// </summary>
		internal static double RollExpiryHours(double min, double max)
		{
			double lo = System.Math.Min(min, max);
			double hi = System.Math.Max(min, max);
			return UnityEngine.Random.Range((float)lo, (float)hi);
		}

		/// <summary>
		/// Adds the site/request to ActiveRequests, shows a matching Geoscape objective
		/// (top-left menu, alongside native ones like "Stop enemy invasion at haven X" —
		/// explicit request) via GeoFaction.AddObjective, and force-pauses Geoscape time
		/// while any request is active (also explicit request — "requested by player or
		/// automatically triggered" should both pause). Single choke point for both
		/// MarkRequested (fresh) and RestorePendingRequests (post-load) so the
		/// objective/pause side effects can't drift out of sync with the underlying dict.
		/// </summary>
		private static void AddActiveRequest(GeoSite site, PendingHelpRequest request, GeoFaction playerFaction, GeoFaction helperFaction)
		{
			ActiveRequests[site] = request;
			try
			{
				request.Objective = new AskForHelpGeoFactionObjective(site, helperFaction);
				playerFaction.AddObjective(request.Objective);
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to add help-request Geoscape objective: " + ex);
			}
			SitePauseCoordinator.Increment(site.GeoLevel);
		}

		/// <summary>Reverse of AddActiveRequest — removes the dict entry, the objective, and
		/// re-evaluates the pause override. Returns false (no-op) if the site had no active
		/// request, matching Dictionary.Remove's own semantics, so callers can keep using
		/// it as a guard the same way they used the old bare ActiveRequests.Remove(site).</summary>
		private static bool RemoveActiveRequest(GeoSite site, GeoFaction playerFaction)
		{
			if (!ActiveRequests.TryGetValue(site, out PendingHelpRequest request))
				return false;
			ActiveRequests.Remove(site);
			if (request.Objective != null)
			{
				try
				{
					playerFaction.RemoveObjective(request.Objective);
				}
				catch (System.Exception ex)
				{
					Log.Debug("Failed to remove help-request Geoscape objective: " + ex);
				}
			}
			SitePauseCoordinator.Decrement(site.GeoLevel);
			return true;
		}

		/// <summary>
		/// Called from AskForHelpAbility.OnPaymentChoice, after it has already deducted the
		/// chosen payment (the offer's resource, or relationship). Picks the troop squad,
		/// marks the site as used, rerolls the faction's own offer for next time, and wires
		/// the launch/cancel/expiry outcomes.
		/// </summary>
		public static void MarkRequested(GeoMission mission, GeoFaction playerFaction, GeoFaction helperFaction, ChaosFactionConfig cfg, AskForHelpPaymentMethod method, FactionHelpOffer offer)
		{
			GeoSite site = mission?.Site;
			if (site == null || ActiveRequests.ContainsKey(site))
				return;
			HelperFactionKind? kind = HelperFactionKindExtensions.FromFaction(site.GeoLevel, helperFaction);
			if (kind == null)
			{
				Log.Debug($"MarkRequested: {helperFaction?.Def?.name} is not one of the three tracked helper factions; refusing to mark the request.");
				return;
			}

			PendingHelpRequest request = new PendingHelpRequest
			{
				HelperKind = kind.Value,
				Method = method,
				Offer = offer,
				ExpiresAtHours = CurrentHours(site.GeoLevel) + System.Math.Max(1, RollExpiryHours(cfg.PendingHelpHoursMin, cfg.PendingHelpHoursMax)),
			};
			AddActiveRequest(site, request, playerFaction, helperFaction);

			// Resolved once here and threaded through both calls below, rather than each
			// re-resolving it independently — same mission, same answer either way.
			// missionType still matters for the offer cache's own 3-way key (see
			// FactionHelpOfferTracker); threatLevel is what actually drives the rolled
			// squad size/cost now (MissionHelpSettingsResolver).
			HelpMissionType? missionType = ContextualAbilitiesPatchHelper.ResolveMissionType(mission, site.GeoLevel);
			DifficultyThreatLevel threatLevel = ContextualAbilitiesPatchHelper.ResolveThreatLevel(mission);
			FactionHelpOfferTracker.RerollAfterUse(helperFaction, missionType, threatLevel, cfg);

			List<TacCharacterDef> squad = BuildSquad(mission, helperFaction, threatLevel, cfg);
			Log.Debug($"Help requested for {site.SiteName.Localize(null)}: picked {squad.Count} {helperFaction.Def.name} template(s).");
			PreloadSquadAssets(squad);
			request.Squad = squad;
			request.SquadThreatLevel = threatLevel;

			ShowRequestMarker(site, playerFaction, helperFaction, request);
			WireMissionLifecycle(site, mission, playerFaction);
		}

		/// <summary>
		/// Shown/refreshed on every successful MarkRequested/RestorePendingRequests call
		/// — unlike WireMissionLifecycle below, this has no "once per mission" guard,
		/// since the marker's label/countdown must reflect whichever request is
		/// CURRENTLY active for the site, and a fresh request always follows a proper
		/// Hide() from the previous one having expired/cancelled/launched first (so
		/// there's no stale-marker risk the way there was a stale-event-closure risk).
		/// </summary>
		private static void ShowRequestMarker(GeoSite site, GeoFaction playerFaction, GeoFaction helperFaction, PendingHelpRequest request)
		{
			string label = ChaosFactionLocalization.Get("KEY_CHAOSFACTION_MARKER_HELP_LABEL", helperFaction.Name.Localize(null));
			HelpRequestedMarker.Show(site, label, request.ExpiresAtHours, () => ExpireRequest(site, playerFaction, helperFaction, request));
		}

		/// <summary>Flattens a live request into its save form; null if it can't be saved.
		/// Shared by ChaosFactionGeoscape.RecordGeoscapeInstanceData and the launch
		/// snapshot (LaunchedMissionSnapshots).</summary>
		internal static ChaosFactionSavedHelpRequest ToSaved(GeoSite site, PendingHelpRequest request)
		{
			if (site == null || site.SiteId < 0 || request?.Offer == null)
				return null;
			return new ChaosFactionSavedHelpRequest
			{
				SiteId = site.SiteId,
				HelperKind = (int)request.HelperKind,
				Method = (int)request.Method,
				ResourceType = (int)request.Offer.ResourceType,
				ResourceAmount = request.Offer.ResourceAmount,
				RelationshipAmount = request.Offer.RelationshipAmount,
				ExpiresAtHours = request.ExpiresAtHours,
			};
		}

		/// <summary>
		/// Rebuilds a previously-paid-for request after a save/load — see
		/// ChaosFactionGeoscape.OnGeoscapeStart, which calls this once the Geoscape level
		/// (and its sites/missions) are fully available. Deliberately conservative: any
		/// entry that can't be fully resolved (site gone, its mission gone/changed, faction
		/// unavailable) is just skipped and logged rather than guessed at — the player
		/// loses that one pending request (same as the pre-fix behavior for every request),
		/// not a crash or a corrupted one.
		/// </summary>
		public static void RestorePendingRequests(GeoLevelController geoLevel, IEnumerable<ChaosFactionSavedHelpRequest> saved)
		{
			if (geoLevel?.PhoenixFaction != null)
			{
				// Defensive: AskForHelpGeoFactionObjective lives inside GeoFaction._objectives,
				// itself part of normal vanilla save data, so if vanilla's own serializer
				// happened to restore a stray instance from a previous session on its own,
				// clear it out first — this mod's ChaosFactionGSInstanceData (processed
				// below), not vanilla's objective-list round-trip, is the actual source of
				// truth being restored, so starting from a clean slate avoids ending up with
				// a duplicate/desynced objective regardless of whether that round-trip
				// happened to work.
				foreach (GeoFactionObjective stray in geoLevel.PhoenixFaction.Objectives.OfType<AskForHelpGeoFactionObjective>().ToList())
					geoLevel.PhoenixFaction.RemoveObjective(stray);
			}
			if (geoLevel == null || saved == null)
				return;
			foreach (ChaosFactionSavedHelpRequest entry in saved)
			{
				try
				{
					// Saved while the mission was launching, and that mission has since
					// completed (this is the Geoscape reload after tactical) — the
					// request was used, so don't bring it back.
					if (LaunchedMissionSnapshots.WasCompleted(entry.SiteId))
					{
						Log.Debug($"Not restoring help request for SiteId {entry.SiteId}: its mission was launched and has completed.");
						continue;
					}
					GeoSite site = geoLevel.Map?.AllSites?.FirstOrDefault(s => s.SiteId == entry.SiteId);
					GeoMission mission = site?.ActiveMission;
					if (site == null || mission == null)
					{
						Log.Debug($"Could not restore pending help request for SiteId {entry.SiteId}: site or its ActiveMission no longer exists.");
						continue;
					}

					HelperFactionKind kind = (HelperFactionKind)entry.HelperKind;
					GeoFaction helperFaction = kind.ToFaction(geoLevel);
					GeoFaction playerFaction = geoLevel.PhoenixFaction;
					if (helperFaction == null || playerFaction == null)
					{
						Log.Debug($"Could not restore pending help request for SiteId {entry.SiteId}: helper or player faction unavailable.");
						continue;
					}

					PendingHelpRequest request = new PendingHelpRequest
					{
						HelperKind = kind,
						Method = (AskForHelpPaymentMethod)entry.Method,
						Offer = new FactionHelpOffer
						{
							ResourceType = (ResourceType)entry.ResourceType,
							ResourceAmount = entry.ResourceAmount,
							RelationshipAmount = entry.RelationshipAmount,
						},
						ExpiresAtHours = entry.ExpiresAtHours,
					};
					AddActiveRequest(site, request, playerFaction, helperFaction);
					// Squad: null (default) — never persisted (would need to serialize
					// TacCharacterDef references), so a restored request that goes on to
					// actually launch picks its squad fresh at that point instead — see
					// WireMissionLifecycle.
					ShowRequestMarker(site, playerFaction, helperFaction, request);
					WireMissionLifecycle(site, mission, playerFaction);
					Log.Debug($"Restored pending help request for {site.SiteName?.Localize(null)} from {helperFaction.Def.name}, {(request.ExpiresAtHours - CurrentHours(geoLevel)):0.0}h remaining.");
				}
				catch (System.Exception ex)
				{
					Log.Debug($"Failed to restore pending help request for SiteId {entry.SiteId}: " + ex);
				}
			}
		}

		/// <summary>
		/// The full roster is used, including classes like "Technician" (turret deployer)
		/// and vehicles ("Armadillo"): none of these render/fire correctly through this
		/// generic infantry-oriented spawn path (missing skin/prefab resolution and weapon
		/// aim-point rigging that vanilla only ever supplies through curated, non-generic
		/// spawn paths this mod has no access to), but that's now a known, non-crashing
		/// cosmetic limitation (Patch_TacticalActor_GetAimPoints) rather than an exclusion
		/// — see CLAUDE.md.
		///
		/// Mutogs (Anu's tamed/mutated creatures — TacCharacterDef.IsMutog, backed by
		/// SharedGameTags.MutogTag, the same pattern IsVehicle uses for VehicleTag) are
		/// bucketed alongside vehicles here, rather than going through the "one per
		/// distinct class" diversity pass below: each Mutog subtype (Basher/Bladed/etc.)
		/// apparently carries its own distinct ClassTag, so the diversity pass was treating
		/// every subtype as a separate "class" and picking one of each — in-game this
		/// showed up as a squad stacked with several different Mutogs. This doesn't change
		/// TacCharacterDef.IsVehicle itself (still false for Mutogs, so ammo granting/stat
		/// rolls/etc. elsewhere are unaffected), only which bucket this squad-composition
		/// step puts them in.
		/// </summary>
		private static List<TacCharacterDef> BuildSquad(GeoMission mission, GeoFaction helperFaction, DifficultyThreatLevel? threatLevel, ChaosFactionConfig cfg)
		{
			MissionHelpSettings settings = MissionHelpSettingsResolver.Get(threatLevel, cfg);
			List<TacCharacterDef> eligible = helperFaction.GetEligibleTemplatesForMission(mission, 0).ToList();
			List<TacCharacterDef> vehicleCandidates = eligible.Where(t => t.IsVehicle).OrderBy(_ => Random.value).ToList();
			List<TacCharacterDef> mutogCandidates = eligible.Where(t => t.IsMutog).OrderBy(_ => Random.value).ToList();
			List<TacCharacterDef> infantry = eligible.Where(t => !t.IsVehicle && !t.IsMutog).ToList();

			// The squad size itself is a randomly-rolled "slot budget" (this threat level's
			// own HelpMinSquadSize..HelpMaxSquadSize inclusive) rather than one fixed size,
			// and a vehicle/Mutog costs more than one slot (NewJerichoVehicleUnitSize/
			// SynedrionVehicleUnitSize/MutogUnitSize, global — "heavier units count for
			// more" against the roll) while a regular trooper still costs exactly 1.
			int minSquadSize = Mathf.Min(settings.MinSquadSize, settings.MaxSquadSize);
			int maxSquadSize = Mathf.Max(settings.MinSquadSize, settings.MaxSquadSize);
			int squadSlotBudget = Mathf.Max(1, Random.Range(minSquadSize, maxSquadSize + 1));
			Log.Debug($"Rolled squad slot budget: {squadSlotBudget} (range {minSquadSize}-{maxSquadSize}, threat level {threatLevel?.ToString() ?? "unresolved"}).");

			// Per-faction max count for vehicles/Mutogs (this mission type's own
			// HelpMaxNewJerichoVehiclesInSquad/HelpMaxSynedrionVehiclesInSquad/
			// HelpMaxAnuMutogsInSquad) replaced the old single shared MaxVehiclesInSquad
			// count cap per explicit request — a given request is always for exactly one
			// helper faction, so only the max/unit-size pair matching that faction's own
			// "big unit" type is relevant; the other two resolve to 0/unused.
			// RollBigUnitSlots rolls VehicleSpawnChancePercent as a "streak" rather than a
			// fixed number of independent attempts — explicit follow-up request: roll once;
			// on success, add that vehicle/Mutog and, if the per-faction max hasn't been
			// reached yet, roll again; the first failed roll stops the streak immediately
			// (no more vehicles/Mutogs are added this request) and the rest of the squad
			// slot budget goes to regular infantry instead. So a max of 2 could still add 0
			// (first roll fails), 1 (first succeeds, second fails), or 2 (both succeed) — but
			// never "skip the first roll and land the second," since a failure always halts
			// the streak rather than just skipping that one attempt. Anu having no
			// vehicle-tagged templates (and NJ/Synedrion having no
			// Mutog-tagged ones) is the expected case, not assumed: an unexpected match
			// (e.g. a future DLC giving Anu an actual vehicle) still correctly gets a 0 max
			// from the switch below rather than silently falling into the wrong bucket.
			HelperFactionKind? kind = HelperFactionKindExtensions.FromFaction(mission.Site?.GeoLevel, helperFaction);
			int vehicleChance = Mathf.Clamp(settings.VehicleSpawnChancePercent, 0, 100);

			int vehicleMax = 0;
			int vehicleUnitSize = 1;
			switch (kind)
			{
				case HelperFactionKind.NewJericho:
					vehicleMax = Mathf.Max(0, settings.MaxNewJerichoVehiclesInSquad);
					vehicleUnitSize = Mathf.Max(1, cfg.NewJerichoVehicleUnitSize);
					break;
				case HelperFactionKind.Synedrion:
					vehicleMax = Mathf.Max(0, settings.MaxSynedrionVehiclesInSquad);
					vehicleUnitSize = Mathf.Max(1, cfg.SynedrionVehicleUnitSize);
					break;
			}
			int mutogMax = kind == HelperFactionKind.Anu ? Mathf.Max(0, settings.MaxAnuMutogsInSquad) : 0;
			int mutogUnitSize = Mathf.Max(1, cfg.MutogUnitSize);

			List<TacCharacterDef> squad = new List<TacCharacterDef>();
			int slotsUsed = RollBigUnitSlots(vehicleCandidates, vehicleMax, vehicleUnitSize, vehicleChance, squadSlotBudget, squad);
			slotsUsed += RollBigUnitSlots(mutogCandidates, mutogMax, mutogUnitSize, vehicleChance, squadSlotBudget - slotsUsed, squad);

			// New Jericho/Anu squads only (both this help squad and, per explicit follow-up
			// request, DebugActivityTracker's own squads — see ResolveAugmentedPool): each
			// regular trooper slot independently rolls a chance to be pulled from an
			// "augmented" splinter-faction pool instead — New Jericho's own Purists, or
			// Anu's own Fallen Ones. Synedrion has no augmented pool/chance and always draws
			// from its own regular pool only (a Synedrion-armor-filtered variant was tried
			// and then explicitly reverted — see ResolveAugmentedPool's own doc comment).
			(List<TacCharacterDef> augmentedPool, int augmentedChancePercent) = ResolveAugmentedPool(mission, kind, cfg);

			// Fills the remaining slot budget one trooper at a time (each trooper costs
			// exactly 1 slot) — per slot, first rolls which pool to draw this soldier from
			// (note: "the roll happen for each soldier independently", explicit request),
			// then picks from that pool preferring a ClassTag not yet represented anywhere
			// in the squad, falling back to any random pick from the pool once every class
			// already has a member. Same "diversity first, then repeats" rule the original
			// two-phase algorithm used, just evaluated fresh per slot (across whichever pool
			// that slot's roll picked) instead of as one separate first pass over a single
			// merged pool — with only one pool in play (Synedrion, or NewJericho/Anu whenever
			// their own augmented-pool roll misses), this produces the same distribution the
			// original two-phase version did.
			while (slotsUsed < squadSlotBudget)
			{
				bool useAugmented = augmentedPool != null && augmentedPool.Count > 0
					&& Random.Range(0, 100) < augmentedChancePercent;
				List<TacCharacterDef> pool = useAugmented ? augmentedPool : infantry;
				if (pool.Count == 0)
					pool = useAugmented ? infantry : augmentedPool;
				if (pool == null || pool.Count == 0)
					break;

				squad.Add(PickPreferringNewClass(pool, squad));
				slotsUsed++;
			}

			return squad;
		}

		/// <summary>Prefers a template whose ClassTag isn't already represented anywhere in
		/// squad (see BuildSquad's own doc comment on this), falling back to any random pick
		/// from pool once every class already has a member.</summary>
		private static TacCharacterDef PickPreferringNewClass(List<TacCharacterDef> pool, List<TacCharacterDef> squad)
		{
			var usedClasses = squad.Select(t => t.ClassTag).ToList();
			List<TacCharacterDef> fresh = pool.Where(t => !usedClasses.Contains(t.ClassTag)).ToList();
			List<TacCharacterDef> candidates = fresh.Count > 0 ? fresh : pool;
			return candidates[Random.Range(0, candidates.Count)];
		}

		/// <summary>
		/// Shared by both BuildSquad (this help squad) and DebugActivityTracker.BuildSquad
		/// (ChaosFactionDebug.cs) — explicit follow-up request: "I want to any New Jericho/
		/// Anu squad, regardless if it is help squad or it is from activity." Maps a
		/// HelperFactionKind to its augmented pool + config chance (New Jericho ->
		/// Purists/NewJerichoAugmentedSoldierChance, Anu -> Fallen Ones/
		/// AnuForsekenSoldierChance), or (null, 0) for Synedrion/anything else — internal so
		/// ChaosFactionDebug.cs can call it too, since DebugActivityTracker only ever knows
		/// a DebugActivityKind, not a HelperFactionKind (its own AddActivity maps
		/// DebugActivityKind.NewJericho/.Anu to the matching HelperFactionKind before
		/// calling this, same values, different enum).
		/// New Jericho draws from the *entire* Purists pool (no body-armor filter) —
		/// a Synedrion-specific augmented pool (restricted to whichever Purists templates
		/// happened to wear Synedrion-style body armor, split against New Jericho's own
		/// roll) was tried and then explicitly reverted per follow-up request: "remove
		/// synedrion augmentation chance option from config and from rolling for
		/// synedryon... Update back to roll for New Jericho entire pool from Pure... so New
		/// Jericho can take any unit from Pure pool if rolled with the defined chance."
		/// </summary>
		internal static (List<TacCharacterDef> pool, int chancePercent) ResolveAugmentedPool(GeoMission mission, HelperFactionKind? kind, ChaosFactionConfig cfg)
		{
			if (kind == HelperFactionKind.NewJericho)
				return (GetAugmentedInfantryPool(mission, "purists"), Mathf.Clamp(cfg.NewJerichoAugmentedSoldierChance, 0, 100));
			if (kind == HelperFactionKind.Anu)
				return (GetAugmentedInfantryPool(mission, "fallenones"), Mathf.Clamp(cfg.AnuForsekenSoldierChance, 0, 100));
			return (null, 0);
		}

		/// <summary>
		/// Resolves New Jericho's Purists / Anu's Fallen Ones as a GeoSubFaction (same
		/// DebugSubFactionResolver.Resolve this mod's debug tool already uses, and its own
		/// short-name lookups — "purists"/"fallenones" — confirmed correct via
		/// chaos_factions_list, see CLAUDE.md), then filters its own eligible templates down
		/// to non-vehicle, non-Mutog infantry the same way BuildSquad's own `infantry` list
		/// is filtered. Returns null (not an empty list) if the sub-faction itself couldn't
		/// be resolved, so callers' own null-check can distinguish "no augmented pool at
		/// all" from "resolved, but happens to have zero eligible templates right now."
		/// </summary>
		private static List<TacCharacterDef> GetAugmentedInfantryPool(GeoMission mission, string subFactionShortName)
		{
			GeoLevelController geoLevel = mission.Site?.GeoLevel;
			GeoSubFaction subFaction = DebugSubFactionResolver.Resolve(geoLevel, subFactionShortName);
			if (subFaction == null)
				return null;
			return subFaction.GetDeploymentForMission(mission, 0).OfType<TacCharacterDef>().Where(t => !t.IsVehicle && !t.IsMutog).ToList();
		}

		/// <summary>
		/// Rolls chancePercent as a streak, not maxCount independent attempts — explicit
		/// follow-up request: "roll vehicle spawn chance ... only once and if rolled
		/// successfully then add vehicle/mutog and if max ... count is not reached then to
		/// roll again, but only if previous roll was successful ... otherwise if failed
		/// continue to fill with soldiers." Each iteration rolls once; on success it adds
		/// the next candidate (in the already-shuffled candidates list order) and, if
		/// maxCount hasn't been reached, loops to roll again — the *first* failed roll
		/// breaks out immediately, so a failure always ends the streak rather than just
		/// skipping that one slot. A candidate that wouldn't fit the remaining budget also
		/// ends the streak (the budget only shrinks, so a later attempt at the same unit
		/// size can't fit either) rather than being silently skipped in favor of a later
		/// one. Returns the total slots used so the caller can thread the shrinking
		/// remaining budget into a second call (e.g. Mutogs after vehicles) without needing
		/// its own running total. `internal` (not `private`) so `DebugActivityTracker.
		/// BuildSquad` (`ChaosFactionDebug.cs`) can reuse it too — explicit follow-up
		/// request: "activity spawned units never roll vehicle/mutog... allow it to be
		/// rolled identically as it done in help requests."
		/// </summary>
		internal static int RollBigUnitSlots(List<TacCharacterDef> candidates, int maxCount, int unitSize, int chancePercent, int remainingBudget, List<TacCharacterDef> squad)
		{
			int slotsUsed = 0;
			for (int attempt = 0; attempt < maxCount && attempt < candidates.Count; attempt++)
			{
				if (Random.Range(0, 100) >= chancePercent)
					break;
				if (slotsUsed + unitSize > remainingBudget)
					break;
				squad.Add(candidates[attempt]);
				slotsUsed += unitSize;
			}
			return slotsUsed;
		}

		/// <summary>
		/// Root cause of allies spawning invisible on some (but not all) mission types:
		/// AddonSkinDataBase.GetPrefabAsset returns null if the addon's
		/// AssetReferenceGameObject isn't already loaded into memory — it only reads
		/// assetReference.Asset (a cached handle to a completed load), it never triggers
		/// one itself. Vanilla missions preload their whole scripted roster's assets ahead
		/// of time via AssetsManager.StartAcquireDependencies during level setup; our squad
		/// is chosen and injected by mod code the vanilla preloader has no visibility into,
		/// so a template like New Jericho's Technician — never otherwise referenced on an
		/// all-Pandoran mission (e.g. the Phoenix base infestation cleanup) — is still cold
		/// by the time OnTacticalStart spawns it. Confirmed via Player.log: the *exact
		/// same* Def and *exact same* GameTags resolved a real prefab on a Lair assault
		/// (where NJ troops are a normal possible encounter, so the asset was already warm)
		/// and NULL on a base infestation cleanup — not a tag-matching or mission-logic
		/// difference at all. Starting the load as early as the request is made (well
		/// before the mission-launch loading screen that follows) gives Addressables the
		/// most possible time to finish before the actual spawn call needs it — though for
		/// a request restored after a save/load with no pre-picked squad, this instead runs
		/// right at launch time (see WireMissionLifecycle), later than ideal but still
		/// better than not preloading at all.
		/// </summary>
		private static void PreloadSquadAssets(List<TacCharacterDef> squad)
		{
			AssetsManager assetsManager = GameUtl.GameComponent<AssetsManager>();
			if (assetsManager == null)
				return;
			assetsManager.StartAcquireDependencies(squad.Cast<object>(), "ChaosFaction ally squad");
			Log.Debug($"Started preloading assets for {squad.Count} ally template(s).");
		}

		/// Guards WireMissionLifecycle against subscribing more than once per mission —
		/// see that method's own doc comment for the bug this fixes. Never cleared: a
		/// GeoMission only ever activates/cancels once in its life, so a stale entry
		/// for an already-concluded mission is harmless dead weight, same reasoning
		/// DebugActivityTracker.WiredMissions/HavenDefenseRewardTracker.ProcessedMissions
		/// already rely on.
		private static readonly HashSet<GeoMission> WiredMissions = new HashSet<GeoMission>();

		/// <summary>
		/// Hooks mission.OnMissionActivated/OnMissionCancel exactly ONCE per mission.
		///
		/// Found (not separately reported, but confirmed to exist here by inspection
		/// after finding and fixing the identical bug in
		/// DebugActivityTracker.WireMissionLifecycle — see that method's own doc
		/// comment for the full root-cause story) the same vulnerability: this method
		/// used to be called again, on the same live mission, every time a NEW request
		/// was made for the same still-active site — which can genuinely happen, since
		/// an expired-but-not-yet-launched request (ExpireRequest) frees the site back
		/// up via CanRequestHelp, letting the player request help again from a
		/// DIFFERENT faction while the underlying mission is still the exact same
		/// object. Each call subscribed a FRESH pair of closures capturing that call's
		/// own helperFaction/squad/request, stacking on top of whatever was already
		/// subscribed from the earlier (expired) request — there was never a stored
		/// delegate reference to unsubscribe the old one with. Both handlers would then
		/// fire when the mission actually launched, in registration order: the STALE
		/// (first-registered) one would run first, successfully remove the by-then-
		/// current ActiveRequests entry via RemoveActiveRequest, and populate
		/// PendingAllyTemplates/PendingHelperFactionDef with ITS OWN stale
		/// squad/faction; the fresh closure would then run second, find the entry
		/// already removed, and bail via its own `if (!RemoveActiveRequest(...))
		/// return;` guard — so the wrong (previously-expired) faction's squad would
		/// spawn, even though the player had paid for and the Geoscape had correctly
		/// shown the current, different faction's help the whole time.
		///
		/// Fix, identical in shape to the debug-activity one: subscribe only once per
		/// mission (WiredMissions guards this), and have that one handler read
		/// whatever's CURRENTLY in ActiveRequests[site] at the moment the mission
		/// actually activates, instead of closing over data captured back whenever it
		/// was first wired — so it doesn't matter how many times help gets re-requested
		/// for this site before launch, the handler always reflects the latest one.
		/// helperFaction is re-resolved from PendingHelpRequest.HelperKind (an enum,
		/// safe to close over nothing and just look up fresh) rather than captured as a
		/// live GeoFaction reference. The marker (ShowRequestMarker, called by
		/// MarkRequested/RestorePendingRequests directly, not from here) is unaffected
		/// by this — it was never subject to the same staleness, since it's rebuilt
		/// fresh on every request rather than living inside a stacking event closure.
		/// </summary>
		private static void WireMissionLifecycle(GeoSite site, GeoMission mission, GeoFaction playerFaction)
		{
			if (mission == null || !WiredMissions.Add(mission))
				return;

			mission.OnMissionActivated += (m, result) =>
			{
				// Removal doubles as both "guard against a request that already expired
				// while pending" (ExpireRequest already removed it — don't also spawn
				// allies for free) and as ordinary cleanup once launched (a
				// site's "already requested" bookkeeping is only meaningful pre-launch;
				// once resolved, a genuinely new later mission on the same site should be
				// freshly askable, not permanently blocked by this stale entry).
				if (!ActiveRequests.TryGetValue(site, out PendingHelpRequest request))
					return;
				GeoFaction helperFaction = request.HelperKind.ToFaction(site.GeoLevel);
				if (helperFaction == null)
					return;
				// Keep a copy for the pre-mission autosave vanilla writes right after
				// this event — see LaunchedMissionSnapshots.
				ChaosFactionSavedHelpRequest snapshot = ToSaved(site, request);
				if (!RemoveActiveRequest(site, playerFaction))
					return;
				if (snapshot != null)
					LaunchedMissionSnapshots.HelpRequests[snapshot.SiteId] = snapshot;

				// Explicit follow-up request: "I want to actually update the squad size
				// to match current threat level, it is possible that it will change...
				// rebuild squad logic to actual threat level." A paid request can sit
				// pending for hours before launch, so threat level is re-resolved fresh
				// here rather than trusted from request time — if it still matches what
				// request.Squad was built against, the already-preloaded squad is reused
				// as-is; otherwise (or for a save/load-restored request, which never had
				// a squad to begin with) it's rebuilt from scratch against the CURRENT
				// threat level and preloaded late instead of early (less lead time before
				// spawn, but correctness against the current threat level matters more
				// here than the preload optimization).
				DifficultyThreatLevel currentThreatLevel = ContextualAbilitiesPatchHelper.ResolveThreatLevel(m);
				bool threatLevelChanged = request.Squad != null && request.SquadThreatLevel != currentThreatLevel;
				List<TacCharacterDef> finalSquad = request.Squad != null && !threatLevelChanged ? request.Squad : null;
				if (finalSquad == null)
				{
					finalSquad = BuildSquad(m, helperFaction, currentThreatLevel, ChaosFactionMain.Cfg);
					PreloadSquadAssets(finalSquad);
					if (threatLevelChanged)
						Log.Debug($"Threat level changed since the request was made ({request.SquadThreatLevel} -> {currentThreatLevel}); squad rebuilt to match.");
				}
				HelpRequestedMarker.Hide(site);

				// Phoenix base defense only, per explicit request: the ally squad arrives
				// as a delayed reinforcement (PhoenixBaseDefenseReinforcementMinTurns..
				// MaxTurns, rolled here) instead of spawning immediately like every other
				// eligible mission type. Checking the mission's own C# type here (not a
				// Tactical-side tag re-derivation) is deliberate — this is the one place
				// that still has the real GeoPhoenixBaseDefenseMission instance in hand,
				// before the Geoscape-to-Tactical scene swap loses it for good.
				ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
				int reinforcementTurn = 0;
				if (m is GeoPhoenixBaseDefenseMission && cfg != null)
				{
					int minTurns = Mathf.Max(0, Mathf.Min(cfg.PhoenixBaseDefenseReinforcementMinTurns, cfg.PhoenixBaseDefenseReinforcementMaxTurns));
					int maxTurns = Mathf.Max(0, Mathf.Max(cfg.PhoenixBaseDefenseReinforcementMinTurns, cfg.PhoenixBaseDefenseReinforcementMaxTurns));
					// +1 on top of the raw roll, always — explicit request: "when rolled add
					// +1, I want to have always +1 to what rolled by config". Skipped only
					// when both bounds are 0, so the documented "set both to 0 to spawn
					// immediately" escape hatch still actually spawns immediately (turn 0)
					// instead of always landing on turn 1.
					if (minTurns > 0 || maxTurns > 0)
						reinforcementTurn = Random.Range(minTurns, maxTurns + 1);
				}

				if (reinforcementTurn > 0)
				{
					PendingReinforcementTemplates = finalSquad;
					PendingReinforcementFactionDef = helperFaction.PPFactionDef;
					PendingReinforcementAtTurn = reinforcementTurn;
					Log.Debug($"Mission activated, {finalSquad.Count} ally template(s) from {helperFaction.Def.name} queued to arrive as reinforcements on turn {reinforcementTurn}.");
				}
				else
				{
					PendingAllyTemplates = finalSquad;
					PendingHelperFactionDef = helperFaction.PPFactionDef;
					Log.Debug($"Mission activated, {finalSquad.Count} ally template(s) from {helperFaction.Def.name} queued for spawn on tactical start.");
				}
			};
			mission.OnMissionCancel += m =>
			{
				// GeoMission.Cancel() fires this same event both when the player truly backs
				// out of the mission for good (MissionDef.ClearMissionOnCancel true — the
				// site's own ActiveMission gets nulled out inside Cancel() itself) and when
				// they just close the mission-brief launch-confirmation popup without
				// actually declining anything (ClearMissionOnCancel false for that mission
				// type — e.g. backing out of an alien base assault's launch prompt to hit it
				// another day; the same mission stays the site's ActiveMission, still
				// attackable later). Reported in-game as a bug: the pending help request
				// (and its payment) was being wiped/refunded on the latter case too, even
				// though the player still intended to launch this exact mission later.
				// Site.ActiveMission == m is the same signal Cancel() itself already used to
				// decide whether to clear it — if the mission is still there, this wasn't a
				// real decline, so keep the request (and its marker/payment) intact.
				if (m.Site != null && m.Site.ActiveMission == m)
				{
					Log.Debug("Mission-brief closed without declining the mission itself (still the site's active mission); keeping the pending help request.");
					return;
				}
				if (!RemoveActiveRequest(site, playerFaction))
					return;
				HelpRequestedMarker.Hide(site);
				Log.Debug("Mission cancelled before launch; payment is not refunded (explicit request: no refund for a mission the player never launched).");
			};
		}

		/// <summary>
		/// Invoked by the marker's own countdown (HelpMarkerCountdown) once the rolled
		/// PendingHelpHoursMin..PendingHelpHoursMax expiry has passed without the mission
		/// launching — "if the player didn't come to attack, the marker disappears" was the
		/// explicit request. Deliberately does NOT refund the payment: an earlier version
		/// did, on the theory that a payment for nothing was a dead end, but the player
		/// explicitly asked for no refund at all if the mission was never launched/accepted
		/// — the cost is the price of having asked, not of the mission actually happening.
		/// </summary>
		private static void ExpireRequest(GeoSite site, GeoFaction playerFaction, GeoFaction helperFaction, PendingHelpRequest request)
		{
			if (!RemoveActiveRequest(site, playerFaction))
				return;
			HelpRequestedMarker.Hide(site);
			Log.Debug($"Help request for {site.SiteName?.Localize(null)} expired without launching; payment is not refunded.");
		}
	}

	/// <summary>
	/// Per-faction "pity" streak for HavenDefenseRewardTracker's free-help roll — explicit
	/// request: "when roll failed... next time roll will happen for that faction it
	/// should add 25+10, and if next roll will fail again it should add again... 25+20,
	/// and if it succeed, then go back to 25... also it should survive save, missions
	/// etc". Tracked per HelperFactionKind, not per GeoSite/GeoMission — there's no
	/// target site yet at roll time (one is only picked afterward, if the roll
	/// succeeds), and the whole point is "this faction keeps failing to help me", not
	/// anything tied to one specific Haven. Survives saves and mission transitions the
	/// same way AllyRequestTracker.ActiveRequests/DebugActivityTracker.ActiveActivities
	/// already do, via ChaosFactionGeoscape's save-data plumbing — but unlike those two,
	/// there's no live GeoSite/GeoFaction/GeoMission reference here at all (just a plain
	/// enum-keyed int), so there's no stale-reference risk across an ordinary Geoscape
	/// reload either — the save round-trip exists purely so a true save/load (exit and
	/// relaunch, or loading a different save) doesn't silently reset the streak.
	/// </summary>
	internal static class HavenDefenseFreeHelpPity
	{
		private static readonly Dictionary<HelperFactionKind, int> FailureStreaks = new Dictionary<HelperFactionKind, int>();

		public static int GetFailureStreak(HelperFactionKind kind)
		{
			return FailureStreaks.TryGetValue(kind, out int streak) ? streak : 0;
		}

		public static void RecordFailure(HelperFactionKind kind)
		{
			FailureStreaks[kind] = GetFailureStreak(kind) + 1;
		}

		public static void ResetStreak(HelperFactionKind kind)
		{
			FailureStreaks.Remove(kind);
		}

		/// <summary>For ChaosFactionGeoscape.RecordGeoscapeInstanceData.</summary>
		public static IEnumerable<KeyValuePair<HelperFactionKind, int>> AllStreaks()
		{
			return FailureStreaks;
		}

		/// <summary>
		/// For ChaosFactionGeoscape.OnGeoscapeStart — replaces whatever's currently
		/// tracked with what was saved (empty/null on a brand new game, which correctly
		/// starts every faction's streak at 0).
		/// </summary>
		public static void RestoreStreaks(IEnumerable<ChaosFactionSavedHavenPity> saved)
		{
			FailureStreaks.Clear();
			if (saved == null)
				return;
			foreach (ChaosFactionSavedHavenPity entry in saved)
			{
				if (entry == null || entry.FailureStreak <= 0)
					continue;
				HelperFactionKind kind = (HelperFactionKind)entry.HelperKind;
				if (System.Enum.IsDefined(typeof(HelperFactionKind), kind))
					FailureStreaks[kind] = entry.FailureStreak;
			}
		}
	}

	/// <summary>
	/// "Defend a Haven → its faction is grateful → they send free help nearby" — explicit
	/// request. Hooked into every GeoHavenDefenseMission's OnUpdateableMissionEnded (see
	/// Patch_GeoUpdateableMission_Started, ChaosFactionPatches.cs, for how every instance
	/// gets caught regardless of how it was constructed) rather than GeoMission.
	/// OnMissionCompleted: GeoHavenDefenseMission.OnUpdateMission calls
	/// UpdateGeoscapeMissionState -> GeoUpdateableMission.TriggerMissionEnd directly for a
	/// fully auto-resolved defense (ticks down via the mission's own Update loop while the
	/// player is elsewhere, never touches tactical) — that path fires OnUpdateableMissionEnded
	/// but *not* OnMissionCompleted (the latter only fires from GeoMission.Complete, which
	/// is only ever reached via the player actually playing the mission out in tactical and
	/// returning a TacMissionResult). Explicit request was "trigger even if it was auto
	/// resolved without player", which OnUpdateableMissionEnded satisfies and
	/// OnMissionCompleted alone would not have.
	///
	/// Two real vanilla quirks were found and worked around here, both explicit follow-up
	/// requests ("fix it to roll only once, and in case if player helped in haven defense
	/// to roll after nest etc revealed"):
	///
	/// 1. When the player wins the tactical mission decisively (attacker's own
	/// TacFactionState is Defeated), GeoHavenDefenseMission.ApplyMissionResults (called
	/// from inside GeoMission.Complete, BEFORE GenerateTacticalReward runs) calls
	/// UpdateGeoscapeMissionState(true), which internally calls TriggerMissionEnd() once
	/// — and then, back in ApplyMissionResults, calls CompleteUpdateableMission() right
	/// after, which calls TriggerMissionEnd() a SECOND time. OnUpdateableMissionEnded
	/// (and therefore this tracker) would otherwise fire twice for one single decisive
	/// win. ProcessedMissions (below) makes the actual grant/roll idempotent per mission
	/// instance regardless of how many times the underlying event fires.
	///
	/// 2. RevealPandoranSiteMissionOutcomeDef (e.g. "defending this Haven reveals the
	/// Nest that attacked it") is applied inside GeoMission.GenerateTacticalReward, which
	/// GeoMission.Complete calls AFTER ApplyMissionResults/TriggerMissionEnd — meaning a
	/// naive OnUpdateableMissionEnded-only hook would roll (and search for a target site
	/// via FindClosestEligibleSite) BEFORE a Nest revealed as a reward of this very
	/// mission is actually visible/eligible on the map. CompletingMissions (below), set
	/// by a Prefix/Postfix pair on GeoMission.Complete, detects "this
	/// OnUpdateableMissionEnded firing happened synchronously from inside a Complete()
	/// call" and defers the actual roll to the matching Postfix — which runs after
	/// GenerateTacticalReward (and any reveal it applied) has fully finished. A firing
	/// that happens outside of Complete() (the fully-auto-resolved path, where there is
	/// no reveal step at all) is unaffected and still processed immediately, exactly as
	/// before.
	/// </summary>
	internal static class HavenDefenseRewardTracker
	{
		/// <summary>
		/// Everything TryGrantFreeHelp needs about one specific Haven-defense mission,
		/// captured once at HookMission time (mission start) rather than re-derived
		/// from mission.Site/.Owner/.GeoLevel/.GetEnemyFaction()/.AttackingSites at
		/// event-handling time — explicit follow-up request, after investigating a
		/// report that the attack counter wasn't incrementing when a Haven got fully
		/// destroyed. Root cause candidate: GeoSite.DestroySite() (called from inside
		/// GeoHavenDefenseMission.UpdateGeoscapeMissionState on the AttackersWon
		/// branch) reentrantly fires OnUpdateableMissionEnded from INSIDE that same
		/// in-progress Update() tick, before the mission's own explicit
		/// TriggerMissionEnd() call later in the same method — and since
		/// GeoUpdateableMission's own Update() runs inside TimingScheduler's single
		/// try/catch with no catchException delegate supplied, any exception anywhere
		/// in that reentrant chain (most plausibly vanilla's own earlier-registered
		/// OnUpdateableMissionEnded subscriber, from GeoSite.RegisterMission, which
		/// runs before this mod's own handler) silently aborts the whole tick with
		/// nothing thrown that reaches this mod's own try/catch. None of Site/Owner/
		/// GeoLevel/AttackingSites/the-attacker-is-Pandoran result actually change over
		/// a GeoHavenDefenseMission's life (Site/Owner/GeoLevel are set once at
		/// construction; AttackingSites is fed from the constructor's own attacker
		/// parameter and never touched again), so capturing them once, early, removes
		/// any dependency on this tracker's own code successfully reading live mission
		/// state at a moment that might be compromised by the reentrancy above —
		/// whether or not that reentrancy turns out to be the full story, reading
		/// invariant data once instead of re-deriving it from a live, possibly
		/// mid-transition object graph is strictly safer either way.
		///
		/// Save/load-safe the same way HookMission itself already is: StartUpdating/
		/// ResumeUpdating (Patch_GeoUpdateableMission_Started) are each called exactly
		/// once per live mission instance, including a freshly-deserialized one after a
		/// save load (a save/load never reuses the same C# object — a new
		/// GeoHavenDefenseMission is created and ResumeUpdating fires on THAT new
		/// instance), so this snapshot is always captured fresh against whatever
		/// instance is actually live right now, never a stale pre-load one.
		/// </summary>
		private class HavenMissionSnapshot
		{
			public GeoSite HavenSite;
			public GeoFaction HavenOwner;
			public GeoLevelController GeoLevel;
			public List<GeoSite> AttackingSites;
			public bool IsPandoranAttacker;
		}

		/// Doubles as the "already hooked" guard (StartUpdating and ResumeUpdating are
		/// each only ever called once per instance in practice, but there's no harm in
		/// being defensive about it) and as pending snapshot storage — an entry is only
		/// ever removed once actually consumed by TryGrantFreeHelpOnce (whichever of
		/// the two call sites reaches it first), so it never grows unbounded over a
		/// long campaign.
		private static readonly Dictionary<GeoHavenDefenseMission, HavenMissionSnapshot> HookedMissions = new Dictionary<GeoHavenDefenseMission, HavenMissionSnapshot>();

		/// Mission instances currently inside a GeoMission.Complete(...) call (set by
		/// Patch_GeoMission_Complete's Prefix, cleared by its Postfix, ChaosFactionPatches.cs)
		/// — an OnUpdateableMissionEnded firing while a mission is in this set happened
		/// too early (before GenerateTacticalReward/the reveal outcome), so it's deferred
		/// to the Postfix instead. Always balanced by the same Complete() call that added
		/// it, so this never grows unbounded either.
		private static readonly HashSet<GeoHavenDefenseMission> CompletingMissions = new HashSet<GeoHavenDefenseMission>();

		/// Every mission instance that has actually been rolled/granted for already — a
		/// GeoHavenDefenseMission object is never reused for a second siege once its own
		/// concludes, so unlike the two sets above this one is deliberately never removed
		/// from; that's what makes it a reliable "only once, ever, for this exact mission"
		/// guard against the double-TriggerMissionEnd quirk above, regardless of how many
		/// times or from which of the two call sites this tracker gets invoked for it.
		private static readonly HashSet<GeoHavenDefenseMission> ProcessedMissions = new HashSet<GeoHavenDefenseMission>();

		public static void HookMission(GeoHavenDefenseMission mission)
		{
			if (mission == null || HookedMissions.ContainsKey(mission))
				return;
			GeoSite havenSite = mission.Site;
			GeoLevelController geoLevel = havenSite?.GeoLevel;
			GeoFaction havenOwner = havenSite?.Owner;
			IGeoFactionMissionParticipant enemyFaction = mission.GetEnemyFaction();
			bool isPandoranAttacker = enemyFaction != null && geoLevel?.AlienFaction != null && enemyFaction.PPFactionDef == geoLevel.AlienFaction.PPFactionDef;
			HookedMissions[mission] = new HavenMissionSnapshot
			{
				HavenSite = havenSite,
				HavenOwner = havenOwner,
				GeoLevel = geoLevel,
				AttackingSites = mission.AttackingSites?.ToList(),
				IsPandoranAttacker = isPandoranAttacker,
			};
			mission.OnUpdateableMissionEnded += OnHavenDefenseEnded;
		}

		/// <summary>For Patch_GeoMission_Complete's Prefix.</summary>
		public static void MarkCompleting(GeoHavenDefenseMission mission)
		{
			if (mission != null)
				CompletingMissions.Add(mission);
		}

		/// <summary>
		/// For Patch_GeoMission_Complete's Postfix — runs after the full Complete() call
		/// (ApplyMissionResults, GenerateTacticalReward/reveal outcomes, OnMissionCompleted,
		/// AfterMissionCompleted) has finished. Only actually rolls if this particular
		/// Complete() call is what concluded the whole siege (mission.Status left
		/// InProgress means the player just played out one tactical instance that didn't
		/// decisively end it — e.g. a loss — so the siege continues and there's nothing to
		/// roll for yet; it'll be picked up later, either by another OnUpdateableMissionEnded
		/// firing from a subsequent auto-resolve tick, or a subsequent Complete() call).
		/// mission.Status itself is read live (not from the snapshot) since it's exactly
		/// the thing that changes over the mission's life and needs a fresh read here —
		/// only Site/Owner/GeoLevel/AttackingSites/the-attacker-check are snapshotted.
		/// </summary>
		public static void OnCompleteFinished(GeoHavenDefenseMission mission)
		{
			if (mission == null)
				return;
			CompletingMissions.Remove(mission);
			if (mission.Status == GeoscapeMissionStatus.InProgress)
				return;
			if (!HookedMissions.TryGetValue(mission, out HavenMissionSnapshot snapshot))
				return;
			// Reached only via GeoMission.Complete(), i.e. the player personally played
			// this defense out in tactical — playerParticipated: true.
			TryGrantFreeHelpOnce(mission, snapshot, true);
		}

		private static void OnHavenDefenseEnded(GeoUpdateableMission mission)
		{
			GeoHavenDefenseMission havenDefense = mission as GeoHavenDefenseMission;
			if (havenDefense == null)
				return;
			if (CompletingMissions.Contains(havenDefense))
			{
				// Fired synchronously from inside GeoMission.Complete(), before
				// GenerateTacticalReward has had a chance to run — defer to
				// OnCompleteFinished (the matching Postfix) instead. The snapshot stays
				// in HookedMissions until that Postfix actually consumes it.
				return;
			}
			if (!HookedMissions.TryGetValue(havenDefense, out HavenMissionSnapshot snapshot))
				return;
			// Reached outside of any Complete() call — the defense concluded entirely
			// on its own while the player was elsewhere - playerParticipated: false.
			TryGrantFreeHelpOnce(havenDefense, snapshot, false);
		}

		private static void TryGrantFreeHelpOnce(GeoHavenDefenseMission mission, HavenMissionSnapshot snapshot, bool playerParticipated)
		{
			// Consumed regardless of outcome below — the snapshot's only job was to
			// survive from HookMission to whichever of the two call sites reaches this
			// point first.
			HookedMissions.Remove(mission);
			if (!ProcessedMissions.Add(mission))
				return;
			try
			{
				TryGrantFreeHelp(snapshot, playerParticipated);
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to process haven-defense free-help reward: " + ex);
			}
		}

		/// <summary>
		/// Win or lose doesn't matter (explicit request) — the Haven's *owner* being
		/// grateful for the player's relationship with them, not the outcome of the fight
		/// itself, is what this represents (matches the request: "there is enough
		/// friendship with the faction which was attacked", i.e. GeoSite.Owner, not
		/// whoever attacked it).
		/// </summary>
		private static void TryGrantFreeHelp(HavenMissionSnapshot snapshot, bool playerParticipated)
		{
			ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
			if (cfg == null || snapshot == null)
				return;
			GeoSite havenSite = snapshot.HavenSite;
			GeoLevelController geoLevel = snapshot.GeoLevel;
			GeoFaction havenOwner = snapshot.HavenOwner;
			if (havenSite == null || geoLevel == null || havenOwner == null)
				return;

			// Only roll when the Haven was actually attacked by the Pandorans -
			// explicit request: "only roll when pandoran attacked haven". Resolved
			// once, at HookMission time, into snapshot.IsPandoranAttacker (see that
			// class's own doc comment for why this and every other field here comes
			// from the snapshot instead of a live mission.* read).
			if (!snapshot.IsPandoranAttacker)
			{
				Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)} ended; attacker wasn't Pandoran, skipping free-help roll.");
				return;
			}

			// "Game never reveal nest etc if haven defended itself without player help?"
			// — confirmed true: RevealPandoranSiteMissionOutcomeDef only ever applies via
			// GeoMission.GenerateTacticalReward, which is only reached by the player
			// actually playing the mission out (GeoMission.Complete). This mod fills
			// that gap for the auto-resolved case by calling GeoAlienFaction.
			// TryRevealAlienBase directly — unconditionally, no roll of this mod's own
			// gating it. (An earlier version had its own separate reveal-chance roll
			// stacked on top of vanilla's own internal one inside TryRevealAlienBase
			// itself — two redundant gates for the same decision; removed per explicit
			// follow-up request, so vanilla's own CheckForBaseReveal/BaseAttacksCounter
			// now fully owns whether it actually reveals.)
			//
			// HavenSelfDefensePandoranSiteAttackTracker's own separate counter (used
			// only for the "how many times has this base attacked" map marker/console
			// display, entirely decoupled from the reveal decision above) still
			// increments here regardless of reveal state or playerParticipated —
			// explicit follow-up requests: "increase it on revealed too, I want to know
			// how many times that base attacked" and "erase counter only when it is
			// destroyed" (see that class's own doc comment for the full history of why
			// this mod's own tracker, not vanilla's, is what backs that display).
			{
				GeoSite attackedBase = ResolveAttackedPandoranBaseSite(snapshot.AttackingSites, geoLevel, havenSite, cfg);
				if (attackedBase == null)
				{
					Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)} ended; no eligible Pandoran site found to track/reveal-check.");
				}
				else
				{
					int attackCount = HavenSelfDefensePandoranSiteAttackTracker.IncrementAndGet(attackedBase);
					// Extra safety net, explicit follow-up request: "do this counter
					// check logic on all enemy bases after each haven/base defense
					// after our counter increment" — re-syncs every unrevealed
					// Pandoran base's counter against vanilla's own native
					// BaseAttacksCounter right here too, not just once at Geoscape
					// start, then re-reads this specific base's own count in case the
					// sync bumped it further (e.g. vanilla's own counter for this same
					// base had independently advanced beyond what IncrementAndGet just
					// produced) so the reveal roll below uses the most accurate value.
					HavenSelfDefensePandoranSiteAttackTracker.SyncWithNativeCounters(geoLevel);
					attackCount = HavenSelfDefensePandoranSiteAttackTracker.GetCount(attackedBase);
					if (attackedBase.GetVisible(geoLevel.PhoenixFaction))
					{
						Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)} ended; {GeoSiteDisplayHelper.GetDisplayName(attackedBase)} is already revealed, no reveal attempt needed.");
					}
					else if (!playerParticipated)
					{
						TryRevealPandoranBase(geoLevel, attackedBase, cfg, attackCount, $"Haven defense at {havenSite.SiteName.Localize(null)} self-resolved");
					}
					else
					{
						Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)} ended (player played it out); no reveal attempt here — already had its own chance via the mission's own outcome, if any.");
					}
				}
			}

			// Resolved before the roll (not after, like the original grant-eligibility
			// check further down) since the pity bonus below is tracked per faction.
			HelperFactionKind? kind = HelperFactionKindExtensions.FromFaction(geoLevel, havenOwner);
			if (kind == null)
			{
				Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)} ended; owner {havenOwner.Def?.name} isn't one of the three tracked helper factions, skipping free-help grant.");
				return;
			}

			// Pity: each consecutive failed roll for this faction raises its own next
			// roll's chance by HelpForFreeAfterHavenDefenseNextBonusPercent, stacking
			// on top of the previous bonus — explicit request. A successful roll resets
			// that faction's streak back down to the plain base chance.
			int baseChance = Mathf.Clamp(cfg.HelpForFreeAfterHavenDefenseChancePercent, 0, 100);
			int failureStreak = HavenDefenseFreeHelpPity.GetFailureStreak(kind.Value);
			int pityBonus = Mathf.Max(0, cfg.HelpForFreeAfterHavenDefenseNextBonusPercent) * failureStreak;
			int chance = Mathf.Clamp(baseChance + pityBonus, 0, 100);
			int roll = Random.Range(0, 100);
			if (roll >= chance)
			{
				HavenDefenseFreeHelpPity.RecordFailure(kind.Value);
				Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)} ended; free-help roll failed (rolled {roll}, needed < {chance}% chance = {baseChance}% base + {pityBonus}% pity); {havenOwner.Def?.name}'s streak is now {failureStreak + 1}.");
				return;
			}
			HavenDefenseFreeHelpPity.ResetStreak(kind.Value);

			GeoFaction playerFaction = geoLevel.PhoenixFaction;
			if (playerFaction == null || havenOwner.Diplomacy.GetDiplomacy(playerFaction) < cfg.RelationshipThreshold)
			{
				Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)} ended; relationship with {havenOwner.Def.name} below RelationshipThreshold, skipping free-help grant.");
				return;
			}

			GeoSite target = FindClosestEligibleSite(geoLevel, havenSite, havenOwner, cfg);
			if (target == null)
			{
				Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)} ended; no eligible nearby site found within radius {cfg.HelpForFreeAfterHavenDefenseRadius} for a free-help grant.");
				return;
			}

			// Amount 0 both ways: nothing is actually charged either way, so there's
			// nothing for a later cancel/expire to have needed to refund regardless.
			FactionHelpOffer freeOffer = new FactionHelpOffer { ResourceType = ResourceType.Supplies, ResourceAmount = 0, RelationshipAmount = 0 };
			AllyRequestTracker.MarkRequested(target.ActiveMission, playerFaction, havenOwner, cfg, AskForHelpPaymentMethod.Resource, freeOffer);
			Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)} ended; free-help roll succeeded (rolled {roll}, needed < {chance}% chance = {baseChance}% base + {pityBonus}% pity); {havenOwner.Def.name} is grateful and sends free help to {target.SiteName.Localize(null)}.");
		}

		/// <summary>
		/// Reuses ContextualAbilitiesPatchHelper.IsEligibleMission (made internal for this)
		/// so a free grant respects exactly the same mission-type/config-toggle rules as a
		/// manual request, and the same "don't offer a faction help against itself" guard
		/// TryAddAskForHelpAbility already applies. GeoMap.SitesInRange is the same
		/// spatial-cache-backed lookup vanilla itself uses for range queries (e.g.
		/// scavenging ambush range), rather than a naive scan-and-filter over every site.
		/// </summary>
		private static GeoSite FindClosestEligibleSite(GeoLevelController geoLevel, GeoSite havenSite, GeoFaction havenOwner, ChaosFactionConfig cfg)
		{
			EarthUnits radius = new EarthUnits(Mathf.Max(0f, cfg.HelpForFreeAfterHavenDefenseRadius));
			IEnumerable<GeoSite> nearby = geoLevel.Map?.SitesInRange(havenSite, radius, true);
			if (nearby == null)
				return null;

			GeoSite closest = null;
			EarthUnits closestDistance = EarthUnits.Zero;
			foreach (GeoSite candidate in nearby)
			{
				GeoMission mission = candidate.ActiveMission;
				if (mission == null)
					continue;
				if (!ContextualAbilitiesPatchHelper.IsEligibleMissionForAskForHelp(mission, geoLevel, cfg))
					continue;
				if (AllyRequestTracker.ActiveRequests.ContainsKey(candidate) || DebugActivityTracker.ActiveActivities.ContainsKey(candidate))
					continue;
				IGeoFactionMissionParticipant enemyFaction = mission.GetEnemyFaction();
				if (enemyFaction?.PPFactionDef == havenOwner.PPFactionDef)
					continue;
				// Same EnableRequestForHelpForAnyMissionType per-candidate rule the manual
				// request loop applies (TryAddAskForHelpAbility) — havenOwner here plays the
				// same role helperFaction does there: only grant this free favor from
				// havenOwner if it's actually at war with the mission's enemy, when that
				// enemy is itself one of the three main factions and the mission is only
				// reachable via the broadened toggle in the first place.
				if (!ContextualAbilitiesPatchHelper.IsHelperFactionEligibleAgainstEnemy(havenOwner, enemyFaction, mission, geoLevel, cfg))
					continue;
				if (!candidate.GetVisible(geoLevel.PhoenixFaction))
					continue;

				EarthUnits distance = geoLevel.Map.DistanceBetweenSites(havenSite, candidate);
				if (closest == null || distance.CompareTo(closestDistance) < 0)
				{
					closest = candidate;
					closestDistance = distance;
				}
			}
			return closest;
		}

		/// <summary>
		/// Prefers the real Pandoran base that actually attacked this Haven over guessing
		/// by distance — explicit follow-up request: "is there actually way to know which
		/// enemy base attacked haven?" GeoHavenDefenseMission.AttackingSites (public,
		/// backed by HavenAttacker.ParticipatingSites) turns out to be exactly the
		/// mechanism vanilla's own RevealPandoranSiteMissionOutcomeDef reveal outcome
		/// reads from — the mission's own constructor feeds AttackingSites straight into
		/// MissionData.Sources via AddSourceRange, and that's the exact collection
		/// RevealPandoranSiteMissionOutcomeDef.ApplyOutcome filters for GeoSiteType.
		/// AlienBase sites to reveal. For a genuine periodic Pandoran attack
		/// (GeoAlienFaction.CheckForHavenAttack/AttackHaven) this is a single real
		/// GeoAlienBase site — the specific base whose own attack-counter fired — not a
		/// guess; a scripted/event-triggered attack (GeoFactionReward.
		/// ApplyAlienAttackHavens) can legitimately list several real attacking bases, in
		/// which case the closest of them is used. Filtered to AlienBase type and
		/// Functioning (a destroyed attacker is correctly skipped) — deliberately NOT
		/// filtered on visibility here, unlike FindClosestUnrevealedPandoranBaseSite
		/// (below, now only the fallback): explicit follow-up request, "increase [the
		/// attack counter] on revealed too, I want to know how many times that base
		/// attacked Haven" — the real attacker should keep being identified and counted
		/// even after it's already been revealed, only the reveal-roll step (in
		/// TryGrantFreeHelp) itself needs to check visibility, to skip rolling for a base
		/// that's already known. Falls back to the nearest-unrevealed-site heuristic only
		/// when attackingSites yields nothing usable: a non-Pandoran attacker (shouldn't
		/// reach here at all, given the Pandoran-attacker gate earlier in
		/// TryGrantFreeHelp, but defensive regardless), null/empty ParticipatingSites, or
		/// every real attacking site already destroyed. Takes the already-captured
		/// snapshot.AttackingSites list rather than reading mission.AttackingSites live
		/// — see HavenMissionSnapshot's own doc comment for why.
		/// </summary>
		private static GeoSite ResolveAttackedPandoranBaseSite(List<GeoSite> attackingSites, GeoLevelController geoLevel, GeoSite havenSite, ChaosFactionConfig cfg)
		{
			GeoSite closestRealAttacker = null;
			EarthUnits closestRealAttackerDistance = EarthUnits.Zero;
			if (attackingSites != null)
			{
				foreach (GeoSite candidate in attackingSites)
				{
					if (candidate == null || candidate.Type != GeoSiteType.AlienBase)
						continue;
					if (candidate.State != GeoSiteState.Functioning)
						continue;

					EarthUnits distance = geoLevel.Map.DistanceBetweenSites(havenSite, candidate);
					if (closestRealAttacker == null || distance.CompareTo(closestRealAttackerDistance) < 0)
					{
						closestRealAttacker = candidate;
						closestRealAttackerDistance = distance;
					}
				}
			}
			if (closestRealAttacker != null)
			{
				Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)}: resolved actual attacking Pandoran base {GeoSiteDisplayHelper.GetDisplayName(closestRealAttacker)} via AttackingSites.");
				return closestRealAttacker;
			}

			Log.Debug($"Haven defense at {havenSite.SiteName.Localize(null)}: no usable attacking site found via AttackingSites, falling back to nearest unrevealed Pandoran base within radius.");
			return FindClosestUnrevealedPandoranBaseSite(geoLevel, havenSite, cfg);
		}

		/// <summary>
		/// Fallback for ResolveAttackedPandoranBaseSite above, when the mission's own
		/// AttackingSites doesn't resolve to a usable real attacker — same
		/// GeoMap.SitesInRange spatial query FindClosestEligibleSite already uses, just
		/// filtered for an actual Pandoran base site (GeoSiteType.AlienBase — the same
		/// filter RevealPandoranSiteMissionOutcomeDef.ApplyOutcome itself uses) that isn't
		/// already visible to the player, rather than an eligible mission site. The
		/// Functioning check (mirrors IsEligibleMission's own site.State guard) keeps a
		/// destroyed base from ever being returned here — which is also what keeps
		/// HavenSelfDefensePandoranSiteAttackTracker from ever accumulating a counter for
		/// one, without needing its own separate "was this site destroyed" hook.
		/// </summary>
		private static GeoSite FindClosestUnrevealedPandoranBaseSite(GeoLevelController geoLevel, GeoSite havenSite, ChaosFactionConfig cfg)
		{
			EarthUnits radius = new EarthUnits(Mathf.Max(0f, cfg.HelpForFreeAfterHavenDefenseRadius));
			IEnumerable<GeoSite> nearby = geoLevel.Map?.SitesInRange(havenSite, radius, true);
			if (nearby == null)
				return null;

			GeoSite closest = null;
			EarthUnits closestDistance = EarthUnits.Zero;
			foreach (GeoSite candidate in nearby)
			{
				if (candidate.Type != GeoSiteType.AlienBase)
					continue;
				if (candidate.State != GeoSiteState.Functioning)
					continue;
				if (candidate.GetVisible(geoLevel.PhoenixFaction))
					continue;

				EarthUnits distance = geoLevel.Map.DistanceBetweenSites(havenSite, candidate);
				if (closest == null || distance.CompareTo(closestDistance) < 0)
				{
					closest = candidate;
					closestDistance = distance;
				}
			}
			return closest;
		}

		/// <summary>
		/// Extends the same per-Pandoran-base attack counter
		/// (HavenSelfDefensePandoranSiteAttackTracker) to Phoenix base defenses —
		/// explicit follow-up request: "increase same way counter for phoenix base
		/// defense mission against pandoran enemy", then "I want to increment it in any
		/// case, even if player will not fight for base or lose it." A loss is already
		/// covered here: Complete() fires regardless of win/lose, and this method never
		/// looks at the TacMissionResult's outcome at all, only at who the enemy was —
		/// so OnPhoenixBaseDefenseCompleted (hooked from Patch_GeoMission_Complete's
		/// Postfix, ChaosFactionPatches.cs, the same generic GeoMission.Complete patch
		/// already used for the Haven-defense fix) covers both win and lose uniformly.
		/// "Will not fight for base" (a genuine decline, never launched) is a separate
		/// case Complete() never sees at all — OnPhoenixBaseDefenseCancelled (hooked
		/// from a dedicated Patch_GeoPhoenixBaseDefenseMission_Cancel Postfix) covers
		/// that one. Unlike GeoHavenDefenseMission, GeoPhoenixBaseDefenseMission is a
		/// plain GeoMission with no auto-resolve-while-player-is-elsewhere path at all
		/// (confirmed via source), so between these two hooks every way this
		/// mission type can end is covered, with no playerParticipated branching and no
		/// reveal roll needed — only the counter increment (this mod deliberately
		/// doesn't ask for help against its own home base's attacker via the reveal
		/// mechanism, so a roll would have nothing to feed into here anyway).
		/// </summary>
		public static void OnPhoenixBaseDefenseCompleted(GeoPhoenixBaseDefenseMission mission)
		{
			try
			{
				TrackPhoenixBaseDefenseAttack(mission, "ended");
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to process phoenix-base-defense Pandoran attack counter: " + ex);
			}
		}

		/// <summary>
		/// The "player will not fight for base" half of the pair described in
		/// OnPhoenixBaseDefenseCompleted's own doc comment above. GeoMission.Cancel()
		/// fires for two different real situations though — the same quirk
		/// AllyRequestTracker.WireMissionLifecycle already documents/works around for
		/// its own pending-request bookkeeping: a genuine decline
		/// (MissionDef.ClearMissionOnCancel clears Site.ActiveMission) and simply
		/// closing the mission-brief launch-confirmation popup without declining
		/// anything (the same mission stays Site.ActiveMission, still launchable
		/// later). Only the former is "did not fight for the base" — the latter isn't a
		/// real outcome yet, so it's skipped here via the exact same
		/// Site.ActiveMission == mission check WireMissionLifecycle already uses.
		/// </summary>
		public static void OnPhoenixBaseDefenseCancelled(GeoPhoenixBaseDefenseMission mission)
		{
			try
			{
				if (mission?.Site == null || mission.Site.ActiveMission == mission)
					return;
				TrackPhoenixBaseDefenseAttack(mission, "declined/abandoned");
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to process phoenix-base-defense-cancelled Pandoran attack counter: " + ex);
			}
		}

		/// <summary>
		/// Shared by OnPhoenixBaseDefenseCompleted/OnPhoenixBaseDefenseCancelled — calls
		/// TryRevealAlienBase unconditionally (no playerParticipated concept exists for
		/// this mission type, unlike Haven defense — see the doc comments above), same
		/// "no roll of this mod's own, let vanilla's own chance fully own it" approach
		/// TryGrantFreeHelp's own self-defense block uses. Whether
		/// RevealPandoranSiteMissionOutcomeDef is actually attached to this mission
		/// type's own Def data couldn't be confirmed from source alone, but
		/// calling TryRevealAlienBase here regardless is harmless either way — it's a
		/// no-op once already visible. HavenSelfDefensePandoranSiteAttackTracker's own
		/// counter (map marker/console display only, decoupled from the reveal
		/// decision) still increments unconditionally, same as the Haven case.
		/// </summary>
		private static void TrackPhoenixBaseDefenseAttack(GeoPhoenixBaseDefenseMission mission, string outcomeDescription)
		{
			ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
			if (cfg == null || mission == null)
				return;
			GeoSite baseSite = mission.Site;
			GeoLevelController geoLevel = baseSite?.GeoLevel;
			if (geoLevel == null)
				return;

			IGeoFactionMissionParticipant enemyFaction = mission.GetEnemyFaction();
			if (enemyFaction == null || geoLevel.AlienFaction == null || enemyFaction.PPFactionDef != geoLevel.AlienFaction.PPFactionDef)
				return;

			GeoSite attackedBase = ResolveAttackedPandoranBaseSiteForPhoenixBaseDefense(mission, geoLevel, baseSite, cfg);
			if (attackedBase == null)
			{
				Log.Debug($"Phoenix base defense at {GeoSiteDisplayHelper.GetDisplayName(baseSite)} {outcomeDescription}; no eligible Pandoran site found to track/reveal-check.");
				return;
			}

			int attackCount = HavenSelfDefensePandoranSiteAttackTracker.IncrementAndGet(attackedBase);
			// Extra safety net, explicit follow-up request: "do this counter check
			// logic on all enemy bases after each haven/base defense after our counter
			// increment" — see the matching comment in TryGrantFreeHelp's own Haven
			// self-defense block for the full reasoning.
			HavenSelfDefensePandoranSiteAttackTracker.SyncWithNativeCounters(geoLevel);
			attackCount = HavenSelfDefensePandoranSiteAttackTracker.GetCount(attackedBase);
			TryRevealPandoranBase(geoLevel, attackedBase, cfg, attackCount, $"Phoenix base defense at {GeoSiteDisplayHelper.GetDisplayName(baseSite)} {outcomeDescription}");
		}

		/// <summary>
		/// Rolls this mod's own reveal chance instead of delegating to vanilla's
		/// internal TryRevealAlienBase/CheckForBaseReveal roll — explicit follow-up
		/// request, after this mod briefly delegated the decision fully to vanilla:
		/// "I want for our mod to have that reveal chance after haven self defend and
		/// next bonus chance (multiplied by counter) to be in our config."
		/// HavenSelfDefensePandoranSiteRevealChance is the base chance;
		/// HavenSelfDefensePandoranSiteNextRevealChanceBonus x attackCount (this base's
		/// own HavenSelfDefensePandoranSiteAttackTracker count, already incremented by
		/// the caller for this same attack before calling here) is added on top — the
		/// same "pity" shape this mod already uses for the ask-for-help roll
		/// (HavenDefenseFreeHelpPity), just per Pandoran base instead of per faction.
		///
		/// Deliberately does NOT call TryRevealAlienBase at all — that would
		/// reintroduce the double-gating problem this mod already removed once (our own
		/// roll deciding whether to even attempt it, then vanilla's own internal roll
		/// deciding again). Reveals directly via GeoSite.RevealSite instead, the same
		/// underlying call TryRevealAlienBase itself makes on success — and, unlike
		/// vanilla's own version, this roll's actual rolled number IS loggable, since
		/// it's this mod's own Random.Range call, not one buried inside vanilla's own
		/// method with no way to read it back out.
		///
		/// Still honors vanilla's own scanner-range auto-reveal first
		/// (GeoPhoenixFaction.IsSiteInBaseScannerRange) — that's not a chance-based
		/// mechanic at all, just "is this already within your radar's range", so an
		/// already-in-range base shouldn't have to wait on a dice roll it doesn't need.
		/// </summary>
		private static bool TryRevealPandoranBase(GeoLevelController geoLevel, GeoSite site, ChaosFactionConfig cfg, int attackCount, string logPrefix)
		{
			GeoFaction playerFaction = geoLevel?.PhoenixFaction;
			if (playerFaction == null || site.GetVisible(playerFaction))
				return false;

			if (playerFaction is GeoPhoenixFaction phoenixFaction && phoenixFaction.IsSiteInBaseScannerRange(site, true))
			{
				site.RevealSite(playerFaction);
				Log.Debug($"{logPrefix}: {GeoSiteDisplayHelper.GetDisplayName(site)} was already within base-scanner range — revealed without a roll.");
				return true;
			}

			// Bug report: on the very first tracked attack against a base, the roll was
			// using the full chance + bonus (e.g. 20% base + 10% from "1 tracked
			// attack" = 30%) instead of the plain base chance — attackCount here is the
			// POST-increment count (already includes this very attack, since the
			// caller's IncrementAndGet runs before this method), so the bonus should
			// reflect only PRIOR attacks (attackCount - 1), matching the same "first
			// roll uses the base chance, the bonus only grows after a failure" pity
			// shape HavenDefenseFreeHelpPity already uses for the ask-for-help roll.
			int priorAttacks = Mathf.Max(0, attackCount - 1);
			int baseChance = Mathf.Clamp(cfg.HavenSelfDefensePandoranSiteRevealChance, 0, 100);
			int bonus = Mathf.Max(0, cfg.HavenSelfDefensePandoranSiteNextRevealChanceBonus) * priorAttacks;
			int chance = Mathf.Clamp(baseChance + bonus, 0, 100);
			int roll = Random.Range(0, 100);
			bool revealed = roll < chance;
			if (revealed)
				site.RevealSite(playerFaction);
			Log.Debug($"{logPrefix}: reveal roll against {GeoSiteDisplayHelper.GetDisplayName(site)} {(revealed ? "succeeded" : "failed")} (rolled {roll}, needed < {chance}% = {baseChance}% base + {bonus}% from {priorAttacks} prior tracked attack(s); this is attack #{attackCount}).");
			return revealed;
		}

		/// <summary>
		/// Same "find the real attacking Pandoran base" logic as
		/// ResolveAttackedPandoranBaseSite above, adapted for GeoPhoenixBaseDefenseMission.
		/// Confirmed via source: this mission type carries the exact same kind
		/// of real attacker-site data GeoHavenDefenseMission does — its constructor takes
		/// an attackingSites parameter (fed from PhoenixBaseAttacker.ParticipatingSites,
		/// itself populated by GeoAlienFaction.StartPhoenixBaseAssault with the actual
		/// GeoAlienBase site(s) that triggered the assault), stores it in a private
		/// _attackingSites field, and also feeds it into MissionData.AddSourceRange — the
		/// same mechanism RevealPandoranSiteMissionOutcomeDef reads elsewhere. The only
		/// difference from the Haven case is there's no public getter for it (unlike
		/// GeoHavenDefenseMission.AttackingSites), so it's read via reflection here.
		/// </summary>
		private static GeoSite ResolveAttackedPandoranBaseSiteForPhoenixBaseDefense(GeoPhoenixBaseDefenseMission mission, GeoLevelController geoLevel, GeoSite baseSite, ChaosFactionConfig cfg)
		{
			GeoSite closestRealAttacker = null;
			EarthUnits closestRealAttackerDistance = EarthUnits.Zero;
			List<GeoSite> attackingSites = GetPhoenixBaseAttackingSites(mission);
			if (attackingSites != null)
			{
				foreach (GeoSite candidate in attackingSites)
				{
					if (candidate == null || candidate.Type != GeoSiteType.AlienBase)
						continue;
					if (candidate.State != GeoSiteState.Functioning)
						continue;

					EarthUnits distance = geoLevel.Map.DistanceBetweenSites(baseSite, candidate);
					if (closestRealAttacker == null || distance.CompareTo(closestRealAttackerDistance) < 0)
					{
						closestRealAttacker = candidate;
						closestRealAttackerDistance = distance;
					}
				}
			}
			if (closestRealAttacker != null)
			{
				Log.Debug($"Phoenix base defense at {GeoSiteDisplayHelper.GetDisplayName(baseSite)}: resolved actual attacking Pandoran base {GeoSiteDisplayHelper.GetDisplayName(closestRealAttacker)} via _attackingSites.");
				return closestRealAttacker;
			}

			Log.Debug($"Phoenix base defense at {GeoSiteDisplayHelper.GetDisplayName(baseSite)}: no usable attacking site found via _attackingSites, falling back to nearest unrevealed Pandoran base within radius.");
			return FindClosestUnrevealedPandoranBaseSite(geoLevel, baseSite, cfg);
		}

		/// <summary>
		/// GeoPhoenixBaseDefenseMission._attackingSites has no public accessor (unlike
		/// GeoHavenDefenseMission.AttackingSites), so it's read by reflection — degrades
		/// to null (falls straight to the nearest-site fallback above) rather than
		/// throwing if the field is ever renamed/removed in a future game update.
		/// </summary>
		private static List<GeoSite> GetPhoenixBaseAttackingSites(GeoPhoenixBaseDefenseMission mission)
		{
			try
			{
				System.Reflection.FieldInfo field = typeof(GeoPhoenixBaseDefenseMission).GetField("_attackingSites", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
				return field?.GetValue(mission) as List<GeoSite>;
			}
			catch (System.Exception ex)
			{
				Log.Debug("Failed to read GeoPhoenixBaseDefenseMission's private _attackingSites field via reflection: " + ex);
				return null;
			}
		}
	}

	/// <summary>
	/// Per-Pandoran-base "attack counter" backing the map marker/console display below
	/// — explicit request history: originally built as this mod's own tracker; briefly
	/// replaced with GeoAlienBase's native BaseAttacksCounter after investigating it
	/// (see the reveal-roll doc comment in HavenDefenseRewardTracker.TryGrantFreeHelp
	/// for that investigation's findings); reverted back to this mod's own tracker per
	/// a later explicit follow-up ("Lets better use our counter implementation")
	/// specifically because the native counter permanently stops incrementing the
	/// instant a base is revealed (TryRevealAlienBase's own outer GetVisible guard
	/// never touches it again after that point), which can't satisfy "I want to know
	/// how many times that base attacked even when was revealed already" — this mod's
	/// own tracker keeps counting past reveal, native's can't. The reveal *decision*
	/// itself still delegates fully to vanilla though (TryRevealAlienBase, called
	/// unconditionally with no roll of this mod's own gating it) — only the
	/// tracking/display of "how many times has this base attacked" uses this tracker,
	/// decoupled from whether/when a reveal actually happens.
	///
	/// Keyed by GeoSite.SiteId rather than the GeoSite reference itself, matching every
	/// other site-keyed piece of state this mod persists (AllyRequestTracker.
	/// ActiveRequests, DebugActivityTracker.ActiveActivities) — a GeoSite object
	/// reference doesn't survive a scene reload/save-load, but SiteId is the game's own
	/// stable identity for it. Only ever cleared once the base is actually destroyed —
	/// no live "on destroy" event hook exists, so this is detected lazily wherever a
	/// tracked site is next resolved (AllTrackedSites prunes a since-destroyed entry as
	/// it enumerates; RestoreCounts does the same at the save/load boundary).
	///
	/// Display: "once revealed then don't display the counter, Name title something as
	/// Unrevealed Attacks" — later explicit follow-up request. The counter itself keeps
	/// incrementing after reveal (per the paragraph above), but RefreshMarker only ever
	/// shows the marker while the site is still unrevealed, titled "Unrevealed Attacks:
	/// N" — once revealed the number would be frozen from the marker's own perspective
	/// anyway (this mod stops calling TryRevealAlienBase for an already-visible site,
	/// so nothing would update it further even though the underlying counter itself
	/// keeps climbing on further attacks). ShowUnrevealedAttacksCounter (config,
	/// default true) — "if true then this is displayed even if chaos_factions_debug is
	/// disabled" — is OR'd with ChaosFactionDebugFlag.Enabled, so either one alone is
	/// enough to show it.
	/// </summary>
	internal static class HavenSelfDefensePandoranSiteAttackTracker
	{
		private static readonly Dictionary<int, int> AttackCounts = new Dictionary<int, int>();

		public static int GetCount(GeoSite site)
		{
			if (site == null || site.SiteId < 0)
				return 0;
			return AttackCounts.TryGetValue(site.SiteId, out int count) ? count : 0;
		}

		public static int IncrementAndGet(GeoSite site)
		{
			if (site == null || site.SiteId < 0)
				return 0;
			int next = GetCount(site) + 1;
			AttackCounts[site.SiteId] = next;
			RefreshMarker(site);
			return next;
		}

		/// Explicit request: "erase counter only when it is destroyed" — only ever
		/// reached now via the lazy destroyed-site pruning in AllTrackedSites/
		/// RestoreCounts below (never on a successful reveal — see this class's own doc
		/// comment).
		public static void ResetCounter(GeoSite site)
		{
			if (site == null)
				return;
			AttackCounts.Remove(site.SiteId);
			RefreshMarker(site);
		}

		/// <summary>
		/// Shows/updates/hides this one site's marker to match its current count —
		/// called on every count change (increment, reset) and from RefreshAllMarkers
		/// below. One choke point for "does this site's marker need to exist right
		/// now" rather than duplicating the enabled+revealed+count check at every call
		/// site.
		/// </summary>
		private static void RefreshMarker(GeoSite site)
		{
			if (site == null)
				return;
			GeoLevelController geoLevel = site.GeoLevel;
			GeoFaction playerFaction = geoLevel?.PhoenixFaction;
			ChaosFactionConfig cfg = ChaosFactionMain.Cfg;
			bool displayEnabled = ChaosFactionDebugFlag.Enabled || (cfg != null && cfg.ShowUnrevealedAttacksCounter);
			bool alreadyRevealed = playerFaction == null || site.GetVisible(playerFaction);
			int count = GetCount(site);

			if (!displayEnabled || alreadyRevealed || count <= 0)
			{
				HelpRequestedMarker.HideCounter(site);
				return;
			}
			HelpRequestedMarker.ShowOrUpdateCounter(site, ChaosFactionLocalization.Get("KEY_CHAOSFACTION_UNREVEALED_ATTACKS_LABEL", count), ChaosFactionColors.HostileRed);
		}

		/// <summary>For SetDebugMode (ChaosFactionDebug.cs) whenever the debug flag is
		/// toggled either way, ChaosFactionMain.OnConfigChanged whenever
		/// ShowUnrevealedAttacksCounter itself is toggled in Mod Settings, and
		/// RestoreCounts below — re-evaluates every currently-tracked site's marker
		/// against the two settings' current combined state (RefreshMarker's own OR
		/// logic means a single "hide everything" pass would be wrong here whenever the
		/// other setting is still keeping some markers visible).</summary>
		public static void RefreshAllMarkers(GeoLevelController geoLevel)
		{
			foreach ((GeoSite Site, int Count) entry in AllTrackedSites(geoLevel))
				RefreshMarker(entry.Site);
		}

		/// <summary>For ChaosFactionGeoscape.RecordGeoscapeInstanceData.</summary>
		public static IEnumerable<KeyValuePair<int, int>> AllCounts() => AttackCounts;

		/// <summary>
		/// For chaos_factions_state (ChaosFactionDebug.cs) and RefreshAllMarkers above
		/// — resolves each tracked SiteId back to its live GeoSite, dropping (and, for
		/// a since-destroyed site, also erasing — see this class's own doc comment) any
		/// entry that no longer resolves to a real, still-functioning site. A revealed
		/// base is NOT dropped here — only State != Functioning (destroyed) is grounds
		/// for removal; RefreshMarker is what decides display state for a revealed one
		/// (hidden), not this enumeration. Iterates a snapshot (ToList()) of
		/// AttackCounts rather than the live dictionary directly, since this method
		/// removes entries as it goes (via ResetCounter) — mutating a dictionary while
		/// yield-iterating it directly would throw.
		/// </summary>
		public static IEnumerable<(GeoSite Site, int Count)> AllTrackedSites(GeoLevelController geoLevel)
		{
			if (geoLevel?.Map?.AllSites == null)
				yield break;
			foreach (KeyValuePair<int, int> kv in AttackCounts.ToList())
			{
				GeoSite site = geoLevel.Map.AllSites.FirstOrDefault(s => s.SiteId == kv.Key);
				if (site == null)
					continue;
				if (site.State != GeoSiteState.Functioning)
				{
					ResetCounter(site);
					continue;
				}
				yield return (site, kv.Value);
			}
		}

		/// <summary>
		/// For ChaosFactionGeoscape.OnGeoscapeStart — resolving every saved entry back
		/// to a live site and dropping it only if that site is now gone or destroyed
		/// (NOT merely revealed/visible) is what makes "erase only when destroyed"
		/// actually happen across a save/load boundary, the same lazy-cleanup-on-restore
		/// approach AllyRequestTracker/DebugActivityTracker's own restores already use
		/// for their own stale entries.
		/// </summary>
		public static void RestoreCounts(GeoLevelController geoLevel, IEnumerable<ChaosFactionSavedPandoranSiteAttackCount> saved)
		{
			AttackCounts.Clear();
			if (geoLevel == null || saved == null)
				return;
			foreach (ChaosFactionSavedPandoranSiteAttackCount entry in saved)
			{
				if (entry == null || entry.AttackCount <= 0)
					continue;
				GeoSite site = geoLevel.Map?.AllSites?.FirstOrDefault(s => s.SiteId == entry.SiteId);
				if (site == null || site.State != GeoSiteState.Functioning)
					continue;
				AttackCounts[entry.SiteId] = entry.AttackCount;
			}
			RefreshAllMarkers(geoLevel);
		}

		/// <summary>
		/// For ChaosFactionGeoscape.OnGeoscapeStart, called right after RestoreCounts —
		/// explicit follow-up request: "after load and initializing... check native
		/// game counter for that enemy base and ours mod counter, and in case if native
		/// game counter is higher than ours then update our counter to that higher
		/// number." Vanilla's own GeoAlienBase.BaseAttacksCounter can grow
		/// independently of this mod's own tracking: a played-out Haven defense that
		/// reveals a Pandoran base via vanilla's own RevealPandoranSiteMissionOutcomeDef
		/// (GeoMission.GenerateTacticalReward) increments BaseAttacksCounter directly —
		/// a code path this mod's own tracker deliberately never touches at all
		/// (HavenDefenseRewardTracker.TryGrantFreeHelp skips the reveal step entirely
		/// for playerParticipated == true, precisely because that vanilla outcome
		/// already ran its own attempt). So vanilla's counter can end up higher than
		/// this mod's own for a base attacked mostly during played-out defenses,
		/// meaning this mod's own reveal-chance pity bonus (HavenDefenseRewardTracker.
		/// TryRevealPandoranBase) would under-represent that base's real attack
		/// history. Syncing up to the higher of the two (never down — this mod's own
		/// counter also keeps counting attacks after vanilla's own counter has already
		/// stopped, per this class's own doc comment) closes that gap.
		///
		/// Scans every unrevealed Pandoran base on the map, not just sites this mod
		/// already had an entry for — vanilla's own counter can be nonzero for a base
		/// this mod's own tracker has never touched at all (e.g. it was only ever
		/// attacked during played-out defenses).
		/// </summary>
		public static void SyncWithNativeCounters(GeoLevelController geoLevel)
		{
			// Verbose per-site/summary logging was added temporarily to confirm this
			// method actually runs and behaves correctly (it did — confirmed via a real
			// Player.log capture showing honest "native <= ours, no change" results for
			// every candidate site) — trimmed back down to only logging an actual
			// counter change, per explicit follow-up request: "log only if actually
			// updated counter after sync".
			if (geoLevel?.Map?.AllSites == null)
				return;
			GeoFaction playerFaction = geoLevel.PhoenixFaction;
			foreach (GeoSite site in geoLevel.Map.AllSites)
			{
				if (site.Type != GeoSiteType.AlienBase || site.State != GeoSiteState.Functioning)
					continue;
				if (playerFaction != null && site.GetVisible(playerFaction))
					continue;

				int nativeCount = site.GetComponent<GeoAlienBase>()?.BaseAttacksCounter ?? 0;
				int ourCount = GetCount(site);
				if (nativeCount <= ourCount)
					continue;

				AttackCounts[site.SiteId] = nativeCount;
				RefreshMarker(site);
				Log.Debug($"Synced attack counter for {GeoSiteDisplayHelper.GetDisplayName(site)} up to vanilla's own BaseAttacksCounter ({ourCount} -> {nativeCount}).");
			}
		}
	}
	/// <summary>
	/// Keeps a launched site's help request / activity alive in save data until its
	/// mission actually finishes. Bug report: "when user load last save (game doing save
	/// just before mission starts) then the pending activity or request for help is gone."
	///
	/// Root cause: vanilla GeoMission.Launch() fires OnMissionActivated (where both
	/// trackers remove their live entry) BEFORE GeoLevelController.LaunchTacticalGameCrt
	/// writes the pre-mission autosave, so that autosave never contained the entry.
	/// Loading it put the player back on the Geoscape, in front of the same still-active
	/// mission, but with the paid-for request / rolled activity gone.
	///
	/// Fix: on activation each tracker still removes its live entry (so tactical-side
	/// bridging and Geoscape UI behave exactly as before) but first copies it here, keyed
	/// by SiteId. RecordGeoscapeInstanceData writes these copies alongside the live ones,
	/// in the same save lists, so the save format is unchanged. They are then cleared:
	/// - when the mission completes (Patch_GeoMission_Complete): that SiteId is also
	///   remembered in CompletedSiteIds so the Geoscape reload after tactical, which
	///   restores from the pre-mission record, drops the entry instead of restoring it.
	///   Vanilla calls Complete() before ModManager.OnGeoscapeStart, so the id is known
	///   in time;
	/// - when tactical starts (ChaosFactionTactical.OnTacticalStart): the pre-mission
	///   autosave has been written by then, and clearing here keeps the post-mission
	///   autosave (written on the Geoscape reload before Complete() runs) from carrying
	///   the entry forward;
	/// - at the end of every OnGeoscapeStart: by then the save data has either restored
	///   the entry as live again (loaded the pre-mission save) or dropped it.
	/// </summary>
	internal static class LaunchedMissionSnapshots
	{
		public static readonly Dictionary<int, ChaosFactionSavedHelpRequest> HelpRequests = new Dictionary<int, ChaosFactionSavedHelpRequest>();
		public static readonly Dictionary<int, ChaosFactionSavedActivity> Activities = new Dictionary<int, ChaosFactionSavedActivity>();

		/// Sites whose mission completed since the last OnGeoscapeStart.
		public static readonly HashSet<int> CompletedSiteIds = new HashSet<int>();

		public static void OnMissionCompleted(GeoMission mission)
		{
			GeoSite site = mission?.Site;
			if (site == null || site.SiteId < 0)
				return;
			HelpRequests.Remove(site.SiteId);
			Activities.Remove(site.SiteId);
			CompletedSiteIds.Add(site.SiteId);
		}

		public static bool WasCompleted(int siteId) => CompletedSiteIds.Contains(siteId);

		public static void OnTacticalStart()
		{
			HelpRequests.Clear();
			Activities.Clear();
		}

		public static void OnGeoscapeStartFinished()
		{
			HelpRequests.Clear();
			Activities.Clear();
			CompletedSiteIds.Clear();
		}
	}
}

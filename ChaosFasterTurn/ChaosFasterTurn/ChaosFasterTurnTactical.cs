using Base.Serialization.General;
using PhoenixPoint.Modding;
using PhoenixPoint.Tactical.Levels;

namespace ChaosFasterTurn
{
	/// <summary>
	/// Mod's custom save data for tactical.
	/// </summary>
	[SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
	public class ChaosFasterTurnTacInstanceData
	{
		public int ExampleData;
	}

	/// <summary>
	/// Represents a mod instance specific for Tactical game.
	/// Each time Tactical level is loaded, new mod's ModTactical is created. 
	/// </summary>
	public class ChaosFasterTurnTactical : ModTactical
	{
		/// <summary>
		/// Called when Tactical starts.
		/// </summary>
		public override void OnTacticalStart() {
			/// Tactical level controller is accessible at any time.
			TacticalLevelController tacController = Controller;
			/// ModMain is accesible at any time
			ChaosFasterTurnMain main = (ChaosFasterTurnMain)Main;

			/// Make the controller visible to the hotkey poller (ChaosFasterTurnUpdater) and the
			/// turn-scaling logic (FasterTurnState), then react to whichever faction's turn is
			/// already active (matters when loading straight into a save mid-turn) and to every
			/// future turn change. NewTurnEvent fires right before a faction's turn actually starts
			/// executing (see PhoenixPoint.Tactical.Levels.TacticalFaction.PlayTurnCrt), so the
			/// speed is in place before that faction's first animation plays.
			/// Defensive: if the previous level somehow ended mid-overwatch-shot (Suppressed left
			/// true - see ChaosFasterTurnOverwatchPause.cs), don't carry that into a fresh level.
			FasterTurnState.Suppressed = false;
			FasterTurnState.Controller = tacController;
			FasterTurnState.ApplyForCurrentFaction();
			/// Same defensive reset as FasterTurnState.Suppressed above, for the independent
			/// player-movement speed control (ChaosFasterTurnPlayerMoveSpeed.cs) - never carry a
			/// mid-movement state left over from a previous level into a fresh one.
			PlayerMoveSpeedState.Reset();
			tacController.NewTurnEvent += OnNewTurnEvent;
			/// The speed badge lives on this level's own HUD Canvas, so it's rebuilt per level
			/// rather than persisted on the mod's own GameObject like ChaosFasterTurnUpdater is.
			FasterTurnSpeedBadge.BuildFor(tacController);
		}
		/// <summary>
		/// Called when Tactical ends.
		/// </summary>
		public override void OnTacticalEnd() {
			if (Controller != null) {
				Controller.NewTurnEvent -= OnNewTurnEvent;
			}
			FasterTurnState.ResetAllActors();
			FasterTurnState.Controller = null;
			PlayerMoveSpeedState.Reset();
			FasterTurnSpeedBadge.Clear();
			base.OnTacticalEnd();
		}

		/// <summary>
		/// Re-applies the current speed multiplier for whichever faction's turn is starting -
		/// enemy turns get sped up, the player's own turn is always normal speed. If
		/// ResetSpeedOnPlayerTurn is enabled (the default), the player's turn starting also
		/// forgets the chosen multiplier, so the next enemy turn begins back at x1.
		/// </summary>
		private void OnNewTurnEvent(TacticalFaction prevFaction, TacticalFaction nextFaction) {
			bool playerTurnStarting = nextFaction != null && nextFaction.IsControlledByPlayer;
			if (!playerTurnStarting) {
				/// Defensive - see PlayerMoveSpeedState.Reset(): a movement action should never
				/// still be tracked once an AI turn starts, but a turn can end mid-move via some
				/// edge case (e.g. the player ending their turn early), so this guarantees a clean
				/// slate before any enemy animation plays.
				PlayerMoveSpeedState.Reset();
			}
			if (playerTurnStarting && (ChaosFasterTurnMain.Cfg?.ResetSpeedOnPlayerTurn ?? true)) {
				FasterTurnState.SetMultiplier(1);
			}
			else {
				FasterTurnState.ApplyForCurrentFaction();
			}
		}
		/// <summary>
		/// Called when Tactical save is being process. At this point level is already created, but TacticalStart is not called.
		/// </summary>
		/// <param name="data">Instance data serialized for this mod. Cannot be null.</param>
		public override void ProcessTacticalInstanceData(object instanceData) {
			ChaosFasterTurnTacInstanceData data = (ChaosFasterTurnTacInstanceData)instanceData;
		}
		/// <summary>
		/// Called when Tactical save is going to be generated, giving mod option for custom save data.
		/// </summary>
		/// <returns>Object to serialize or null if not used.</returns>
		public override object RecordTacticalInstanceData() {
			 return new ChaosFasterTurnTacInstanceData() { ExampleData = 5 };
		}
		/// <summary>
		/// Called when new turn starts in tactical. At this point all factions must play in their order.
		/// </summary>
		/// <param name="turnNumber">Current turn number</param>
		public override void OnNewTurn(int turnNumber) {
		}
	}
}

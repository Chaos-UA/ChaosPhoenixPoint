using PhoenixPoint.Modding;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Levels;

namespace ChaosUmbraBalance
{
	public class ChaosUmbraBalanceTactical : ModTactical
	{
		private ChaosUmbraBalanceMain UmbraMain => (ChaosUmbraBalanceMain)Main;

		public override void OnTacticalStart()
		{
			Log.Debug("[ChaosUmbraBalance] OnTacticalStart.");
			Controller.NewTurnEvent += OnNewTurnEvent;
		}

		public override void OnTacticalEnd()
		{
			Log.Debug("[ChaosUmbraBalance] OnTacticalEnd — clearing Umbra tracker.");
			if (Controller != null)
				Controller.NewTurnEvent -= OnNewTurnEvent;
			UmbraTracker.Clear();
			base.OnTacticalEnd();
		}

		public override void OnNewTurn(int turnNumber) { }

		// Fires whenever the active faction changes. When the player's turn starts,
		// kill any Umbras that were detected and suppressed during the enemy turn.
		private void OnNewTurnEvent(TacticalFaction prevFaction, TacticalFaction nextFaction)
		{
			if (!nextFaction.IsControlledByPlayer) return;

			var config = UmbraMain?.Config;
			if (config == null || !config.DisableUmbra) return;

			foreach (var umbra in UmbraTracker.KnownUmbras)
			{
				if (umbra == null || !umbra.IsAlive) continue;
				Log.Debug($"[ChaosUmbraBalance] Player turn start: killing known Umbra {umbra.name}.");
				umbra.ApplyDamage(new DamageResult { HealthDamage = (float)umbra.GetHealth() + 1f });
			}
		}
	}
}
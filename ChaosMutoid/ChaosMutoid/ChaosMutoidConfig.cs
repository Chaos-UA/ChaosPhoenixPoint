using PhoenixPoint.Modding;

namespace ChaosMutoid
{
	/// <summary>
	/// ModConfig is mod settings that players can change from within the game.
	/// Config is only editable from players in main menu.
	/// Only one config can exist per mod assembly.
	/// Config is serialized on disk as json.
	/// </summary>
	public class ChaosMutoidConfig : ModConfig
	{
		/// Only public fields are serialized.
		/// Supported types for in-game UI are:

		[ConfigField(
			text: "Allow Mutoid Body Part Mutation",
			description: "Lets Mutoid soldiers use the Mutation screen to change their Head/Torso/Legs " +
			             "body parts with Anu organic augments, same as regular human soldiers. " +
			             "Vanilla hides this screen for Mutoids entirely.")]
		public bool EnableMutoidBodyPartMutation = true;

		[ConfigField(
			text: "Allow Mutoid Body Part Augmentation",
			description: "Lets Mutoid soldiers use the Bionics screen to change their Head/Torso/Legs " +
			             "body parts with New Jericho robotic augments, same as regular human soldiers. " +
			             "Vanilla hides this screen for Mutoids entirely. Off by default, separate from " +
			             "Mutation above.")]
		public bool EnableMutoidBodyPartAugmentation = false;

		[ConfigField(
			text: "Allow Replacing Mutation With Augmentation (And Vice Versa)",
			description: "Lets any soldier (not just Mutoids) replace an already-mutated body part with " +
			             "a Bionic augment, or an already-augmented body part with a Mutation - same as " +
			             "swapping any other equipment. Vanilla only " +
			             "allows replacing a Mutation with another Mutation, or a Bionic with another " +
			             "Bionic, never across categories. Off by default.")]
		public bool EnableReplacementForMutationAndAugmentation = true;

		[ConfigField(
			text: "Allow Augmenting/Mutating All 3 Body Parts",
			description: "Vanilla caps any soldier at 2 augmented body parts (Head/Torso/Legs combined, " +
			             "Mutation or Bionic) out of the 3 available - the 3rd always shows as limit. When on, that cap is lifted so all 3 body")]
		public bool EnableFullAugmentationOrMutation = false;

		[ConfigField(
			text: "Debug Logging",
			description: "Useful for troubleshooting the Mutoid ability re-learn flow")]
		public bool EnableDebugLog = false;
	}
}

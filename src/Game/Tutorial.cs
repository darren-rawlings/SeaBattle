using System.Collections.Generic;
using Renegade.Ui;

namespace SeaBattle.Game;

/// <summary>The guided tour. Clips are recorded from the game with --clip (see Main.PlayClip).</summary>
public static class Tutorial
{
	public static readonly IReadOnlyList<TutorialPage> Pages = new[]
	{
		new TutorialPage("Two seas",
			"The far sea is the enemy's: their five ships are hidden there. The near sea is yours, with your fleet showing. Sink all of theirs before they sink yours.",
			"res://tutorial/fire.ogv"),
		new TutorialPage("Place your fleet",
			"Before the first shot, drag a ship to move it and tap it to turn it. Ships can't touch, not even at a corner. Or press Randomize for a new layout.",
			"res://tutorial/place.ogv"),
		new TutorialPage("Fire!",
			"Tap a square of the far sea. A splash and a white peg is a miss: their turn. A red peg is a hit, and you fire again. Hit every square of a ship and it sinks.",
			"res://tutorial/fire.ogv"),
		new TutorialPage("Ships left to sink",
			"The fleet at the top shows the enemy ships still afloat, each as long as the ship. Sunk ones are struck out, so you know what's left to find.",
			"res://tutorial/win.ogv"),
		new TutorialPage("Victory",
			"Sink the whole fleet and their wrecks burn on the water. Stuck? Radar (ad) shows where part of an enemy ship is hiding. Starting over mid-battle counts as a loss, unless you watch an ad.",
			"res://tutorial/win.ogv"),
	};
}

using System.Linq;
using Renegade.Core;
using SeaBattle.Core;
using Xunit;

namespace SeaBattle.Tests;

public class SeaBattleTests
{
	[Fact]
	public void RandomFleetsFitAndNeverTouch()
	{
		for (int seed = 0; seed < 100; seed++)
		{
			var fleet = new Fleet();
			fleet.PlaceRandomly(new System.Random(seed));
			Assert.Equal(5, fleet.Ships.Count);
			Assert.Equal(17, fleet.Ships.SelectMany(s => s.Squares()).Distinct().Count());
			foreach (var ship in fleet.Ships)
				Assert.True(fleet.CanPlace(ship, ignoring: ship));
		}
	}

	[Fact]
	public void ShipsCantTouchEvenAtACorner()
	{
		var fleet = new Fleet();
		fleet.Place(new Ship("Destroyer", 2, 0, 0, true));
		Assert.False(fleet.CanPlace(new Ship("Cruiser", 3, 1, 2, true)));
		Assert.True(fleet.CanPlace(new Ship("Cruiser", 3, 2, 0, true)));
	}

	[Fact]
	public void HittingEverySquareSinksTheShip()
	{
		var fleet = new Fleet();
		fleet.Place(new Ship("Destroyer", 2, 4, 4, true));
		Assert.Equal(ShotResult.Miss, fleet.Fire(0));
		Assert.Equal(ShotResult.Hit, fleet.Fire(44));
		Assert.Equal(ShotResult.Sunk, fleet.Fire(45));
		Assert.True(fleet.AllSunk);
	}

	private static int AverageShots(Difficulty difficulty, int firstSeed)
	{
		int total = 0;
		for (int seed = 0; seed < 20; seed++)
		{
			var fleet = new Fleet();
			fleet.PlaceRandomly(new System.Random(firstSeed + seed));
			var gunner = new ComputerPlayer(difficulty, seed);
			while (!fleet.AllSunk)
			{
				fleet.Fire(gunner.ChooseShot(fleet));
				total++;
			}
		}
		return total / 20;
	}

	[Fact]
	public void TheLevelsGetSharper()
	{
		int easy = AverageShots(Difficulty.Easy, 0), medium = AverageShots(Difficulty.Medium, 0), hard = AverageShots(Difficulty.Hard, 0);
		Assert.True(hard < medium && medium < easy, $"Easy {easy}, Medium {medium}, Hard {hard} shots on average.");
		Assert.True(hard <= 60, $"Hard averaged {hard} shots.");
	}

	[Fact]
	public void TurningAShipKeepsTheSquareYouHeldAndStaysOnTheGrid()
	{
		var cruiser = new Ship("Cruiser", 3, 4, 4, Across: true);
		var turned = cruiser.Turned(1);
		Assert.False(turned.Across);
		Assert.Contains(4 * Fleet.Size + 5, turned.Squares());
		Assert.Equal(new[] { 35, 45, 55 }, turned.Squares());
		var corner = new Ship("Carrier", 5, 9, 7, Across: false).At(9, 7);
		Assert.Equal(5, corner.Row);
		Assert.All(new Ship("Carrier", 5, 0, 8, Across: false).Turned(0).Squares(), square => Assert.InRange(square, 0, Fleet.Squares - 1));
	}

	[Fact]
	public void MovingAShipByHandMustStayClearOfTheOthers()
	{
		var fleet = new Fleet();
		var destroyer = new Ship("Destroyer", 2, 0, 0, Across: true);
		var cruiser = new Ship("Cruiser", 3, 5, 5, Across: true);
		fleet.Place(destroyer);
		fleet.Place(cruiser);
		Assert.False(fleet.TryReplace(cruiser, cruiser.At(1, 0)));
		Assert.True(fleet.TryReplace(cruiser, cruiser.At(2, 0)));
		Assert.Contains(new Ship("Cruiser", 3, 2, 0, true), fleet.Ships);
		Assert.False(fleet.TryReplace(destroyer, destroyer.Turned(0))); // It would touch the cruiser.
		Assert.True(fleet.TryReplace(destroyer, destroyer.At(0, 8).Turned(0)));
		Assert.DoesNotContain(destroyer, fleet.Ships);
	}

	[Fact]
	public void TheBattleSoundsAreMadeAndNeverClip()
	{
		foreach (var sound in new[] { NavalSynth.Cannon(1), NavalSynth.Splash(1), NavalSynth.Hit(1), NavalSynth.Explosion(1) })
		{
			Assert.True(sound.Length > 10000);
			float peak = sound.Max(System.Math.Abs);
			Assert.InRange(peak, 0.85f, 0.91f);
			Assert.DoesNotContain(sound, float.IsNaN);
		}
	}
}

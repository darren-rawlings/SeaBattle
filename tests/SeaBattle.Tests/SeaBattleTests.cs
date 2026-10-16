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
}

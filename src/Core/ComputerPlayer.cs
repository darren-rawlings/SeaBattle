using System;
using System.Collections.Generic;
using System.Linq;
using Renegade.Core;

namespace SeaBattle.Core;

/// <summary>
/// The computer's gunner.
/// Easy fires at random and follows up a hit only half the time.
/// Medium hunts on a checkerboard (every ship covers at least one of those squares), then follows a
/// hit along the line.
/// Hard fires where a ship is most likely to be: for every square it counts the ways the remaining
/// ships could lie across it, given every hit and miss so far, and picks the best.
/// </summary>
public sealed class ComputerPlayer
{
	private readonly Random _random;

	public Difficulty Difficulty { get; }

	public ComputerPlayer(Difficulty difficulty, int? seed = null)
	{
		Difficulty = difficulty;
		_random = seed is { } s ? new Random(s) : new Random();
	}

	public int ChooseShot(Fleet target)
	{
		var open = Enumerable.Range(0, Fleet.Squares).Where(square => !target.WasShot(square)).ToList();
		if (open.Count == 0)
			throw new InvalidOperationException("Nowhere left to fire.");

		// Hits on ships not yet sunk: finish those first.
		var wounded = Enumerable.Range(0, Fleet.Squares)
			.Where(square => target.WasShot(square) && target.ShipAt(square) is { } ship && !target.IsSunk(ship))
			.ToList();

		if (Difficulty == Difficulty.Hard)
			return MostLikely(target, wounded, open);

		if (wounded.Count > 0 && (Difficulty == Difficulty.Medium || _random.NextDouble() < 0.5))
		{
			var follow = FollowUp(target, wounded);
			if (follow.Count > 0)
				return follow[_random.Next(follow.Count)];
		}
		if (Difficulty == Difficulty.Medium)
		{
			var parity = open.Where(square => (square / Fleet.Size + square % Fleet.Size) % 2 == 0).ToList();
			if (parity.Count > 0)
				return parity[_random.Next(parity.Count)];
		}
		return open[_random.Next(open.Count)];
	}

	/// <summary>Open squares next to the wounded ones, along the line once two hits show the direction.</summary>
	private static List<int> FollowUp(Fleet target, List<int> wounded)
	{
		var candidates = new List<int>();
		bool line = wounded.Count >= 2;
		bool across = line && wounded.Select(s => s / Fleet.Size).Distinct().Count() == 1;
		foreach (int square in wounded)
		{
			int row = square / Fleet.Size, column = square % Fleet.Size;
			foreach (var (dr, dc) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
			{
				if (line && (across ? dr != 0 : dc != 0))
					continue;
				int r = row + dr, c = column + dc;
				if (r is >= 0 and < Fleet.Size && c is >= 0 and < Fleet.Size && !target.WasShot(r * Fleet.Size + c))
					candidates.Add(r * Fleet.Size + c);
			}
		}
		return candidates.Distinct().ToList();
	}

	private int MostLikely(Fleet target, List<int> wounded, List<int> open)
	{
		var weight = new int[Fleet.Squares];
		var known = new HashSet<int>(wounded);
		// Misses and the squares of sunk ships rule a placement out.
		bool Blocked(int square) => target.WasShot(square) && (target.ShipAt(square) is not { } ship || target.IsSunk(ship));
		foreach (var ship in target.Ships.Where(ship => !target.IsSunk(ship)))
		{
			for (int row = 0; row < Fleet.Size; row++)
			{
				for (int column = 0; column < Fleet.Size; column++)
				{
					foreach (bool across in new[] { true, false })
					{
						if (across ? column + ship.Length > Fleet.Size : row + ship.Length > Fleet.Size)
							continue;
						var squares = new Ship(ship.Name, ship.Length, row, column, across).Squares().ToList();
						if (squares.Any(Blocked))
							continue;
						// Placements through known hits are far more likely.
						int score = 1 + squares.Count(known.Contains) * 40;
						foreach (int square in squares)
						{
							if (!target.WasShot(square))
								weight[square] += score;
						}
					}
				}
			}
		}
		int best = open.Max(square => weight[square]);
		var choices = open.Where(square => weight[square] == best).ToList();
		return choices[_random.Next(choices.Count)];
	}
}

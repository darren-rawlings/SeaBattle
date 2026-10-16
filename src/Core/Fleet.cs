using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaBattle.Core;

/// <summary>A ship on the grid: its first square, length and direction.</summary>
public sealed record Ship(string Name, int Length, int Row, int Column, bool Across)
{
	public IEnumerable<int> Squares()
	{
		for (int i = 0; i < Length; i++)
			yield return (Row + (Across ? 0 : i)) * Fleet.Size + Column + (Across ? i : 0);
	}

	/// <summary>The same ship with its first square at (row, column), pushed back inside the grid if need be.</summary>
	public Ship At(int row, int column)
	{
		int maxRow = Fleet.Size - (Across ? 1 : Length);
		int maxColumn = Fleet.Size - (Across ? Length : 1);
		return this with { Row = Math.Clamp(row, 0, maxRow), Column = Math.Clamp(column, 0, maxColumn) };
	}

	/// <summary>The ship turned a quarter about one of its squares (counted from the first), kept inside the grid.</summary>
	public Ship Turned(int pivot)
	{
		int row = Row + (Across ? 0 : pivot), column = Column + (Across ? pivot : 0);
		var turned = this with { Across = !Across };
		return turned.Across ? turned.At(row, column - pivot) : turned.At(row - pivot, column);
	}
}

public enum ShotResult
{
	Miss,
	Hit,
	Sunk,
}

/// <summary>
/// One side's 10×10 sea: the ships and the shots fired at it. The classic fleet is a carrier (5),
/// battleship (4), cruiser (3), submarine (3) and destroyer (2). Ships may not touch, even at a
/// corner, which keeps the game fair and readable.
/// </summary>
public sealed class Fleet
{
	public const int Size = 10;
	public const int Squares = Size * Size;

	public static readonly (string Name, int Length)[] Classic =
	{
		("Carrier", 5), ("Battleship", 4), ("Cruiser", 3), ("Submarine", 3), ("Destroyer", 2),
	};

	private readonly List<Ship> _ships = new();
	private readonly bool[] _shot = new bool[Squares];

	public IReadOnlyList<Ship> Ships => _ships;

	public bool WasShot(int square) => _shot[square];

	public Ship? ShipAt(int square) => _ships.FirstOrDefault(ship => ship.Squares().Contains(square));

	public bool IsSunk(Ship ship) => ship.Squares().All(square => _shot[square]);

	public bool AllSunk => _ships.Count > 0 && _ships.All(IsSunk);

	public int ShipsLeft => _ships.Count(ship => !IsSunk(ship));

	/// <summary>True if the ship fits on the grid and touches no other ship.</summary>
	public bool CanPlace(Ship ship, Ship? ignoring = null)
	{
		int endRow = ship.Row + (ship.Across ? 0 : ship.Length - 1);
		int endColumn = ship.Column + (ship.Across ? ship.Length - 1 : 0);
		if (ship.Row < 0 || ship.Column < 0 || endRow >= Size || endColumn >= Size)
			return false;
		var taken = new HashSet<int>();
		foreach (var other in _ships.Where(other => other != ignoring))
		{
			foreach (int square in other.Squares())
			{
				int row = square / Size, column = square % Size;
				for (int dr = -1; dr <= 1; dr++)
				{
					for (int dc = -1; dc <= 1; dc++)
					{
						if (row + dr is >= 0 and < Size && column + dc is >= 0 and < Size)
							taken.Add((row + dr) * Size + column + dc);
					}
				}
			}
		}
		return ship.Squares().All(square => !taken.Contains(square));
	}

	/// <summary>Moves or turns one of the ships (placing your fleet by hand), if the new spot is clear.</summary>
	public bool TryReplace(Ship old, Ship moved)
	{
		int index = _ships.IndexOf(old);
		if (index < 0 || !CanPlace(moved, ignoring: old))
			return false;
		_ships[index] = moved;
		return true;
	}

	public void Place(Ship ship)
	{
		if (!CanPlace(ship))
			throw new InvalidOperationException($"{ship.Name} doesn't fit there.");
		_ships.Add(ship);
	}

	public void Clear()
	{
		_ships.Clear();
		Array.Clear(_shot);
	}

	/// <summary>Places the classic fleet at random, retrying until everything fits.</summary>
	public void PlaceRandomly(Random random)
	{
		while (true)
		{
			_ships.Clear();
			bool ok = true;
			foreach (var (name, length) in Classic)
			{
				bool placed = false;
				for (int attempt = 0; attempt < 200 && !placed; attempt++)
				{
					var ship = new Ship(name, length, random.Next(Size), random.Next(Size), random.Next(2) == 0);
					if (CanPlace(ship))
					{
						_ships.Add(ship);
						placed = true;
					}
				}
				if (!placed)
				{
					ok = false;
					break;
				}
			}
			if (ok)
				return;
		}
	}

	public ShotResult Fire(int square)
	{
		if (_shot[square])
			throw new InvalidOperationException("Already fired there.");
		_shot[square] = true;
		var ship = ShipAt(square);
		if (ship == null)
			return ShotResult.Miss;
		return IsSunk(ship) ? ShotResult.Sunk : ShotResult.Hit;
	}
}

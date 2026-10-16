using System.Collections.Generic;
using Godot;

namespace SeaBattle.Game;

/// <summary>
/// The enemy ships still to sink: each ship drawn as a row of squares, its length. Sunk ships go
/// dim and get struck through, so you can see at a glance what's left out there (and how long it is).
/// </summary>
public partial class FleetLegend : Control
{
	private const int TextSize = 22;
	private const float CellSize = 16;
	private const float CellGap = 3;
	private const float ShipGap = 20;

	private readonly List<(string Name, int Length, bool Sunk)> _ships = new();

	public FleetLegend()
	{
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public void Set(IEnumerable<(string Name, int Length, bool Sunk)> ships)
	{
		_ships.Clear();
		_ships.AddRange(ships);
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (_ships.Count == 0)
			return;
		var font = GetThemeDefaultFont();
		int left = _ships.FindAll(ship => !ship.Sunk).Count;
		string title = left == 0 ? "Enemy fleet: all sunk" : $"Enemy fleet: {left} to sink";
		float titleWidth = font.GetStringSize(title, HorizontalAlignment.Left, -1, TextSize).X;
		DrawString(font, new Vector2((Size.X - titleWidth) / 2 + 2, 24), title, HorizontalAlignment.Left, -1, TextSize, new Color(0, 0, 0, 0.55f));
		DrawString(font, new Vector2((Size.X - titleWidth) / 2, 22), title, HorizontalAlignment.Left, -1, TextSize, new Color("fff3e0"));

		float total = -ShipGap;
		foreach (var ship in _ships)
			total += ship.Length * (CellSize + CellGap) - CellGap + ShipGap;
		float x = (Size.X - total) / 2;
		const float y = 34;
		var afloat = new Color("cbd5e1");
		var sunk = new Color("cbd5e1") with { A = 0.25f };
		foreach (var ship in _ships)
		{
			float start = x;
			for (int i = 0; i < ship.Length; i++)
			{
				var cell = new Rect2(x, y, CellSize, CellSize);
				DrawRect(cell, ship.Sunk ? sunk : afloat);
				if (!ship.Sunk)
					DrawRect(cell, new Color("475569"), filled: false, width: 1.5f);
				x += CellSize + CellGap;
			}
			if (ship.Sunk)
				DrawLine(new Vector2(start - 3, y + CellSize / 2), new Vector2(x - CellGap + 3, y + CellSize / 2), new Color("ef4444"), 3f, antialiased: true);
			x += ShipGap - CellGap;
		}
	}
}

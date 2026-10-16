using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Renegade.Ads;
using Renegade.Audio;
using Renegade.Core;
using Renegade.Dev;
using Renegade.Looks;
using Renegade.Play;
using Renegade.Ui;
using SeaBattle.Core;

namespace SeaBattle.Game;

/// <summary>
/// Sea Battle against the computer. Two seas lie on the table: the enemy's (far, hidden) and yours
/// (near, with your fleet showing). Tap a square of the enemy sea to fire: a shell arcs over and
/// lands with a splash (miss) or a burst of fire on a red peg (hit). Sinking a ship raises it out of
/// the water for a moment, burning, then it sinks with a groan (the signature "sinking"). Before the
/// battle, Shuffle rearranges your fleet. Drag sideways to spin the board.
/// </summary>
public partial class Main : Node3D
{
	public const string Title = "Sea Battle";

	/// <summary>Google's test ad units until the real ones exist in AdMob (see the release playbook).</summary>
	private static readonly AdMobConfig AdUnits = AdMobConfig.Test;

	private const float Cell = 0.5f;
	private const float SeaHalf = Fleet.Size * Cell / 2f;
	private const float SeaGap = 0.7f;
	private const float Thickness = 0.3f;

	/// <summary>The enemy sea sits far from you, yours near.</summary>
	private static readonly Vector3 EnemyCenter = new(0, 0, -(SeaHalf + SeaGap / 2));
	private static readonly Vector3 HomeCenter = new(0, 0, SeaHalf + SeaGap / 2);

	private readonly Fleet _enemy = new();
	private readonly Fleet _home = new();
	private readonly Scoreboard _wins = new();
	private readonly List<Node3D> _markers = new();
	private readonly Dictionary<Ship, Node3D> _homeHulls = new();
	private readonly ImpactBank<Style> _impacts = new(Styles.Impact);
	private readonly ComputerTurn _computer = new();
	private readonly System.Random _random = new();

	private GameSettings _settings = null!;
	private Node3D _rig = null!;
	private Camera3D _camera = null!;
	private SoundPool _sound = null!;
	private GameHud _hud = null!;
	private IAdService _ads = null!;
	private AdSchedule? _adSchedule;
	private SpinControl _spin = null!;
	private MeshInstance3D _deck = null!;
	private Button _shuffle = null!;
	private bool _started;
	private bool _busy;
	private bool _playerTurn = true;
	private bool _over;
	private int _shots;

	private bool _demo;
	private bool _quitAfterDemo;
	private float _initialSpin;
	private ComputerPlayer? _demoGunner;

	public override void _Ready()
	{
		DevLog.Tag = "sea";
		AudioWatch.Attach(this, Title);
		_settings = GameSettings.Load(board: Style.Wood, pieces: Style.Metal);
		ReadDeveloperArgs();
		AudioServer.SetBusMute(0, _settings.Muted);

		_camera = new Camera3D { Position = new Vector3(0, 12.6f, 6.2f), Fov = 50 };
		AddChild(_camera);
		_camera.LookAt(new Vector3(0, -0.4f, 0.6f));
		Stage.Build(this, -Thickness, new Color("0d1a24"));
		_rig = new Node3D();
		AddChild(_rig);
		_rig.RotateY(_initialSpin);
		BuildSeas();
		Stage.WarmUp(this, -Thickness, Sparkle.Halo);

		_sound = new SoundPool();
		AddChild(_sound);
		_adSchedule ??= new AdSchedule(_settings.GamesSinceAd);
		var theme = HudTheme.Warm;
		_ads = AdServices.Create(this, AdUnits, _demo, new PlaceholderAds
		{
			TextColor = theme.Text,
			RewardTitle = "Watch for a radar sweep",
			ClaimText = "Sweep",
			SkipText = "Skip",
		});

		_hud = new GameHud(theme);
		AddChild(_hud);
		_hud.Build(new HudOptions(Title, "Tap the far sea to fire  ·  Drag sideways to spin", PiecesLabel: "Ships",
			PieceName: s => s switch { Style.Plastic => "Plastic", Style.Wood => "Wood", Style.Metal => "Steel", Style.Glass => "Glass", _ => "Marble" },
			UndoText: "Radar (ad)", LevelOnly: true),
			_settings.Muted, GameMode.PlayerVsComputer, _settings.Difficulty, _settings.Board, _settings.Pieces);
		_hud.NewGamePressed += OnNewGamePressed;
		_hud.UndoPressed += OnRadarPressed;
		_hud.MuteToggled += muted =>
		{
			_settings.Muted = muted;
			AudioServer.SetBusMute(0, muted);
			_settings.Save();
		};
		_hud.ModeChosen += (_, difficulty) =>
		{
			_settings.Difficulty = difficulty;
			_settings.Save();
			if (!_started)
				RefreshUi();
		};
		_hud.BoardChosen += style =>
		{
			_settings.Board = style;
			_settings.Save();
			_deck.MaterialOverride = Materials.For(Styles.BoardLook(style));
		};
		_hud.PiecesChosen += style =>
		{
			_settings.Pieces = style;
			_settings.Save();
			DrawHomeFleet();
		};

		var layer = new CanvasLayer();
		AddChild(layer);
		_shuffle = new Button { Text = "Shuffle fleet", FocusMode = Control.FocusModeEnum.None };
		_shuffle.AddThemeFontSizeOverride("font_size", 26);
		foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed" })
		{
			var box = new StyleBoxFlat { BgColor = theme.ButtonDark, AntiAliasing = true };
			box.SetCornerRadiusAll(32);
			box.SetContentMarginAll(12);
			_shuffle.AddThemeStyleboxOverride(state, box);
		}
		_shuffle.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
		_shuffle.OffsetLeft = -140;
		_shuffle.OffsetRight = 140;
		_shuffle.OffsetTop = -192;
		_shuffle.OffsetBottom = -132;
		_shuffle.Pressed += () =>
		{
			if (_started)
				return;
			_home.Clear();
			_home.PlaceRandomly(_random);
			DrawHomeFleet();
			Clack(HomeCenter, 0.5f);
		};
		layer.AddChild(_shuffle);

		_spin = new SpinControl(_rig, () => !_busy);
		_spin.Tapped += OnTapped;
		StartGame();
	}

	// ---------------------------------------------------------------- World

	private static Vector3 SquareCenter(Vector3 sea, int square) =>
		sea + new Vector3((square % Fleet.Size - 4.5f) * Cell, 0, (square / Fleet.Size - 4.5f) * Cell);

	private void BuildSeas()
	{
		_deck = new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(SeaHalf * 2 + 0.8f, Thickness, SeaHalf * 4 + SeaGap + 0.8f) },
			Position = new Vector3(0, -Thickness / 2, 0),
			MaterialOverride = Materials.For(Styles.BoardLook(_settings.Board)),
		};
		_rig.AddChild(_deck);

		// Water: a deep blue glass panel for each sea, with a fine grid over it.
		var water = Materials.For(new Look(Look.GlassPattern, new Color("1e5aa8"), new Color("6fb3ff"), 0.05f, Opacity: 0.85f));
		var line = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.9f, 1f, 0.25f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
		foreach (var sea in new[] { EnemyCenter, HomeCenter })
		{
			_rig.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(SeaHalf * 2, 0.04f, SeaHalf * 2) }, MaterialOverride = water, Position = sea + new Vector3(0, 0.02f, 0) });
			for (int i = 0; i <= Fleet.Size; i++)
			{
				float at = (i - 5) * Cell;
				_rig.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.015f, 0.005f, SeaHalf * 2) }, MaterialOverride = line, Position = sea + new Vector3(at, 0.045f, 0) });
				_rig.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(SeaHalf * 2, 0.005f, 0.015f) }, MaterialOverride = line, Position = sea + new Vector3(0, 0.045f, at) });
			}
		}
	}

	/// <summary>A ship: a hull with a pointed bow and a little bridge, lying along its squares.</summary>
	private Node3D MakeHull(Ship ship, Vector3 sea)
	{
		var node = new Node3D();
		var look = Styles.Pieces(_settings.Pieces).Two;
		var material = Materials.For(look);
		float length = ship.Length * Cell - 0.08f;
		node.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = Cell * 0.3f, Height = length, RadialSegments = 16, Rings = 4 }, MaterialOverride = material, RotationDegrees = new Vector3(0, 0, 90), Scale = new Vector3(1, 1, 0.55f), Position = new Vector3(0, 0.06f, 0) });
		node.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Cell * 0.5f, 0.16f, Cell * 0.32f) }, MaterialOverride = material, Position = new Vector3(-length * 0.15f, 0.2f, 0) });
		var first = SquareCenter(sea, ship.Row * Fleet.Size + ship.Column);
		var last = SquareCenter(sea, ship.Squares().Last());
		node.Position = (first + last) / 2 + new Vector3(0, 0.04f, 0);
		node.RotationDegrees = new Vector3(0, ship.Across ? 0 : 90, 0);
		_rig.AddChild(node);
		return node;
	}

	private void DrawHomeFleet()
	{
		foreach (var hull in _homeHulls.Values)
			hull.QueueFree();
		_homeHulls.Clear();
		foreach (var ship in _home.Ships)
			_homeHulls[ship] = MakeHull(ship, HomeCenter);
	}

	/// <summary>A peg on a square: white for a miss, red for a hit.</summary>
	private Node3D MakePeg(Vector3 sea, int square, bool hit)
	{
		var peg = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = Cell * 0.2f, Height = Cell * 0.4f, RadialSegments = 12, Rings = 6 },
			MaterialOverride = Materials.For(hit
				? new Look(Look.PlasticPattern, new Color("ef4444"), new Color("991b1b"), 0.3f)
				: new Look(Look.PlasticPattern, new Color("f5f5f4"), new Color("a8a29e"), 0.3f)),
			Position = SquareCenter(sea, square) + new Vector3(0, hit ? 0.3f : 0.12f, 0),
		};
		_rig.AddChild(peg);
		_markers.Add(peg);
		return peg;
	}

	// ---------------------------------------------------------------- Input

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_hud.MenuOpen)
			_spin.HandleInput(@event);
	}

	private void OnTapped(Vector2 screen)
	{
		if (_busy || _over || !_playerTurn)
			return;
		var from = _camera.ProjectRayOrigin(screen);
		var direction = _camera.ProjectRayNormal(screen);
		if (direction.Y > -0.01f)
			return;
		var local = _rig.ToLocal(from + direction * (-from.Y / direction.Y)) - EnemyCenter;
		int column = Mathf.FloorToInt(local.X / Cell + 5), row = Mathf.FloorToInt(local.Z / Cell + 5);
		if (column is < 0 or >= Fleet.Size || row is < 0 or >= Fleet.Size)
			return;
		int square = row * Fleet.Size + column;
		if (!_enemy.WasShot(square))
			_ = Fire(square, atEnemy: true);
	}

	// ---------------------------------------------------------------- Battle

	private async Task Fire(int square, bool atEnemy)
	{
		_busy = true;
		_spin.Stop();
		if (!_started)
		{
			_started = true;
			_shuffle.Visible = false;
			_hud.HideHint();
		}
		var fleet = atEnemy ? _enemy : _home;
		var sea = atEnemy ? EnemyCenter : HomeCenter;
		var result = fleet.Fire(square);
		if (atEnemy)
			_shots++;
		DevLog.Write($"{(atEnemy ? "you" : "computer")} fire {square}: {result}");

		// A shell arcs from the other side's sea.
		var target = SquareCenter(sea, square);
		var start = (atEnemy ? HomeCenter : EnemyCenter) + new Vector3(0, 0.3f, 0);
		var shell = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f }, MaterialOverride = Materials.For(Styles.Pieces(Style.Metal).One) };
		_rig.AddChild(shell);
		var flight = CreateTween();
		flight.TweenMethod(Callable.From<float>(t =>
		{
			shell.Position = start.Lerp(target, t) + new Vector3(0, 3.2f * 4 * t * (1 - t), 0);
		}), 0f, 1f, 0.6f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		_sound.Play(_impacts.Random(Style.Table), _rig.ToGlobal(start), -4f, 0.6f);
		await ToSignal(flight, Tween.SignalName.Finished);
		shell.QueueFree();

		var peg = MakePeg(sea, square, result != ShotResult.Miss);
		if (result == ShotResult.Miss)
		{
			Splash(target);
			_sound.Play(_impacts.Random(Style.Glass), _rig.ToGlobal(target), -6f, 0.55f);
		}
		else
		{
			Sparkle.Burst(_rig, target + new Vector3(0, 0.2f, 0));
			_sound.Play(_impacts.Random(Style.Metal), _rig.ToGlobal(target), 2f, 0.7f);
			if (!atEnemy && _home.ShipAt(square) is { } hitShip && _homeHulls.TryGetValue(hitShip, out var hull))
			{
				// A brief flash on your ship where it was hit.
				var meshes = hull.GetChildren().OfType<MeshInstance3D>().ToList();
				meshes.ForEach(mesh => mesh.MaterialOverlay = Sparkle.Halo);
				GetTree().CreateTimer(0.35).Timeout += () => meshes.ForEach(mesh => mesh.MaterialOverlay = null);
			}
		}
		if (result == ShotResult.Sunk && fleet.ShipAt(square) is { } sunk)
			await Sink(sunk, sea, atEnemy);

		if (fleet.AllSunk)
		{
			_over = true;
			_wins.Win(first: atEnemy);
		}
		else if (result == ShotResult.Miss)
		{
			_playerTurn = !atEnemy;
		}
		_busy = false;
		RefreshUi(result, atEnemy);
	}

	private void Splash(Vector3 at)
	{
		var spray = new CpuParticles3D
		{
			Position = at + new Vector3(0, 0.05f, 0),
			Amount = 24,
			Lifetime = 0.6f,
			OneShot = true,
			Explosiveness = 0.95f,
			Mesh = new SphereMesh { Radius = 0.03f, Height = 0.06f, Material = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.93f, 1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } },
			Direction = Vector3.Up,
			Spread = 25f,
			InitialVelocityMin = 1.5f,
			InitialVelocityMax = 2.6f,
			Gravity = new Vector3(0, -7f, 0),
		};
		_rig.AddChild(spray);
		spray.Emitting = true;
		GetTree().CreateTimer(1.0).Timeout += spray.QueueFree;
	}

	/// <summary>
	/// The signature: the sunk ship rises out of the water, burning, then rolls over and sinks with a
	/// low groan. The enemy's ships only appear here, when sunk.
	/// </summary>
	private async Task Sink(Ship ship, Vector3 sea, bool enemy)
	{
		var hull = enemy ? MakeHull(ship, sea) : _homeHulls[ship];
		foreach (var mesh in hull.GetChildren().OfType<MeshInstance3D>())
			mesh.MaterialOverlay = Sparkle.Halo;
		var rest = hull.Position;
		var tween = CreateTween();
		tween.TweenProperty(hull, "position:y", rest.Y + 0.5f, 0.3f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
		tween.TweenInterval(0.25f);
		tween.TweenProperty(hull, "rotation:x", 0.6f, 0.5f);
		tween.Parallel().TweenProperty(hull, "position:y", rest.Y - 0.12f, 0.6f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		Sparkle.Burst(_rig, rest + new Vector3(0, 0.4f, 0));
		_sound.Play(_impacts.Random(Style.Table), hull.GlobalPosition, 4f, 0.45f);
		_hud.SetStatus(enemy ? $"You sank their {ship.Name.ToLowerInvariant()}!" : $"They sank your {ship.Name.ToLowerInvariant()}!", enemy ? new Color("f5c542") : new Color("ff8a8a"));
		await ToSignal(tween, Tween.SignalName.Finished);
		foreach (var mesh in hull.GetChildren().OfType<MeshInstance3D>())
			mesh.MaterialOverlay = null;
		if (enemy)
			_markers.Add(hull);
		await Wait(0.4f);
	}

	private void Clack(Vector3 at, float strength)
	{
		_sound.PlayImpact(_impacts.Random(_settings.Pieces), _rig.ToGlobal(at), strength, Styles.GainDb(_settings.Pieces));
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		_spin.Process(dt);
		if (_busy || _over)
			return;
		if (_playerTurn)
		{
			if (_demo)
			{
				_demoGunner ??= new ComputerPlayer(Difficulty.Medium);
				_ = Fire(_demoGunner.ChooseShot(_enemy), atEnemy: true);
			}
			return;
		}
		if (!_computer.Thinking)
		{
			var gunner = new ComputerPlayer(_settings.Difficulty);
			_computer.MinThinkTime = _demo ? 0.1f : 0.6f;
			_computer.Start(() => gunner.ChooseShot(_home), $"computer ({gunner.Difficulty})");
			return;
		}
		if (_computer.Poll(dt) is { } square && !_home.WasShot(square))
			_ = Fire(square, atEnemy: false);
	}

	// ---------------------------------------------------------------- Games, radar, ads

	private void OnNewGamePressed()
	{
		if (_busy)
			return;
		bool adDue = _adSchedule!.GameFinished();
		_settings.GamesSinceAd = _adSchedule.GamesSinceAd;
		_settings.Save();
		if (adDue)
		{
			DevLog.Write("ad: between-games interstitial");
			_ads.ShowInterstitial(StartGame);
		}
		else
		{
			StartGame();
		}
	}

	private void StartGame()
	{
		_computer.Cancel();
		foreach (var marker in _markers)
			marker.QueueFree();
		_markers.Clear();
		_enemy.Clear();
		_home.Clear();
		_enemy.PlaceRandomly(_random);
		_home.PlaceRandomly(_random);
		DrawHomeFleet();
		_started = false;
		_over = false;
		_playerTurn = true;
		_shots = 0;
		_shuffle.Visible = !_demo;
		RefreshUi();
	}

	private bool CanRadar => !_busy && !_over && _playerTurn && _started;

	private void OnRadarPressed()
	{
		if (!CanRadar)
			return;
		DevLog.Write("ad: rewarded (radar)");
		_ads.ShowRewarded(rewarded: Radar, closed: () => RefreshUi());
	}

	/// <summary>A radar sweep: one unhit square of an enemy ship glows briefly, so you know where to aim.</summary>
	private void Radar()
	{
		var hidden = _enemy.Ships.Where(ship => !_enemy.IsSunk(ship)).SelectMany(ship => ship.Squares()).Where(square => !_enemy.WasShot(square)).ToList();
		if (hidden.Count == 0)
			return;
		int square = hidden[_random.Next(hidden.Count)];
		var blip = new MeshInstance3D
		{
			Mesh = new CylinderMesh { TopRadius = Cell * 0.45f, BottomRadius = Cell * 0.45f, Height = 0.02f, RadialSegments = 24 },
			MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 1f, 0.5f, 0.6f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
			Position = SquareCenter(EnemyCenter, square) + new Vector3(0, 0.06f, 0),
		};
		_rig.AddChild(blip);
		var pulse = CreateTween().SetLoops(4);
		pulse.TweenProperty(blip, "scale", Vector3.One * 1.3f, 0.3f);
		pulse.TweenProperty(blip, "scale", Vector3.One, 0.3f);
		GetTree().CreateTimer(2.6).Timeout += blip.QueueFree;
		_hud.SetStatus("Radar contact!", new Color("6ee7a0"));
	}

	// ---------------------------------------------------------------- UI

	private void RefreshUi(ShotResult? last = null, bool byYou = false)
	{
		_hud.SetScore($"Their ships {_enemy.ShipsLeft}   ·   yours {_home.ShipsLeft}   ·   wins {_wins.First}–{_wins.Second}");
		_hud.SetNewGameVisible(_over && !_busy);
		_hud.SetUndoVisible(CanRadar);
		if (_over)
		{
			bool won = _enemy.AllSunk;
			_hud.SetStatus(won ? $"Victory in {_shots} shots!" : "Your fleet is sunk", won ? new Color("f5c542") : new Color("ff8a8a"));
			if (_demo && _quitAfterDemo)
				GetTree().CreateTimer(1.5).Timeout += () => GetTree().Quit();
			return;
		}
		if (!_started)
		{
			_hud.SetStatus("Shuffle your fleet, then fire!", new Color("fff3e0"));
			return;
		}
		string status = last switch
		{
			ShotResult.Hit => byYou ? "Hit! Fire again" : "They hit you. They fire again",
			ShotResult.Miss => byYou ? "Miss. Their turn" : "They missed. Your turn",
			_ => _playerTurn ? "Your turn: fire!" : "Their turn",
		};
		if (last != ShotResult.Sunk)
			_hud.SetStatus(status, _playerTurn ? new Color("fff3e0") : new Color("ffb4b4"));
	}

	private SignalAwaiter Wait(float seconds) => ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

	// ---------------------------------------------------------------- Developer flags

	/// <summary>
	/// Developer flags after `--` (or in user://dev_args.txt on a phone): `--log`, `--demo` (a
	/// computer fires for you too), `--quit-after-demo`, `--games-since-ad=4`, `--board=Wood`,
	/// `--pieces=Metal`, `--difficulty=Hard`, `--spin=30`. Style flags aren't saved.
	/// </summary>
	private void ReadDeveloperArgs()
	{
		foreach (var (name, value) in DevArgs.Read())
		{
			switch (name)
			{
				case "--log":
					DevLog.Enabled = true;
					break;
				case "--demo":
					_demo = true;
					_settings.Muted = false;
					break;
				case "--quit-after-demo":
					_quitAfterDemo = true;
					break;
				case "--games-since-ad":
					_adSchedule = new AdSchedule(int.Parse(value));
					break;
				case "--board" when System.Enum.TryParse(value, out Style board):
					_settings.Board = board;
					break;
				case "--pieces" when System.Enum.TryParse(value, out Style pieces):
					_settings.Pieces = pieces;
					break;
				case "--difficulty" when System.Enum.TryParse(value, out Difficulty difficulty):
					_settings.Difficulty = difficulty;
					break;
				case "--spin":
					_initialSpin = Mathf.DegToRad(float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
					break;
			}
		}
	}
}

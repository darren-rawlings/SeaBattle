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
/// the water for a moment, burning, then it sinks (the signature "sinking"). Before the battle, drag
/// your ships to place them (tap one to turn it), or press Randomize. The sounds are synthesised:
/// the cannon's boom, a splash for a miss, a clang for a hit, an explosion for a sinking. Drag the
/// table sideways to spin the board.
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
	private readonly Dictionary<Ship, Node3D> _enemyHulls = new();
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
	private FleetLegend _legend = null!;
	private readonly List<Flames> _fires = new();
	private bool _started;
	private bool _busy;
	private bool _playerTurn = true;
	private bool _over;
	private int _shots;

	/// <summary>The battle's synthesised sounds, a few variations of each.</summary>
	private AudioStreamWav[] _cannon = null!, _splash = null!, _hitSound = null!, _explosion = null!;

	/// <summary>While placing your fleet: the ship held, which of its squares is under the finger, and where it would go.</summary>
	private Ship? _held;
	private int _heldPivot;
	private Ship? _heldTarget;

	private bool _demo;
	private bool _quitAfterDemo;
	private string? _clip;
	private float _initialSpin;
	private ComputerPlayer? _demoGunner;

	public override void _Ready()
	{
		DevLog.Tag = "sea";
		AudioWatch.Attach(this, Title);
		_settings = GameSettings.Load(board: Style.Wood, pieces: Style.Metal);
		ReadDeveloperArgs();
		AudioServer.SetBusMute(0, _settings.Muted);

		_camera = new Camera3D { Position = new Vector3(0, 12.6f, 6.2f), Fov = 60 };
		AddChild(_camera);
		_camera.LookAt(new Vector3(0, -0.4f, 0.25f));
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
		_hud.Build(new HudOptions(Title, "Drag a ship to move it, tap it to turn", PiecesLabel: "Ships",
			PieceName: s => s switch { Style.Plastic => "Plastic", Style.Wood => "Wood", Style.Metal => "Steel", Style.Glass => "Glass", _ => "Marble" },
			UndoText: "Radar (ad)", LevelOnly: true), _settings);
		_hud.NewGamePressed += OnNewGamePressed;
		_hud.UndoPressed += OnRadarPressed;
		_hud.RestartPressed += OnRestartPressed;
		_hud.SetTutorial(Tutorial.Pages);
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

		_shuffle = _hud.AddButton("Randomize", false, Control.LayoutPreset.CenterBottom, -140, 140, -192, -132, 26);
		_shuffle.Pressed += () =>
		{
			if (_started || _held != null)
				return;
			_home.Clear();
			_home.PlaceRandomly(_random);
			DrawHomeFleet();
			Clack(HomeCenter, 0.5f);
		};

		// The enemy ships left to sink, under the top buttons; on its own layer so clips show it.
		var legendLayer = new CanvasLayer { Layer = 0 };
		AddChild(legendLayer);
		_legend = new FleetLegend();
		Widgets.Anchor(_legend, Control.LayoutPreset.TopWide, left: 20, right: -20, top: 206, bottom: 262);
		legendLayer.AddChild(_legend);

		_cannon = Bank(NavalSynth.Cannon);
		_splash = Bank(NavalSynth.Splash);
		_hitSound = Bank(NavalSynth.Hit);
		_explosion = Bank(NavalSynth.Explosion);

		_spin = new SpinControl(_rig, () => !_busy && !_settings.FreezeBoard);
		// Freeze the board also turns it back to where it started.
		_spin.Frozen = () => _settings.FreezeBoard && !_busy && _held == null;
		_spin.Tapped += OnTapped;
		_spin.TryGrab = TryGrabShip;
		_spin.Dragged += DragShip;
		_spin.Released += DropShip;
		if (_clip != null)
		{
			_hud.Visible = false;
			_ = PlayClip(_clip);
			return;
		}
		StartGame();
		if (!_demo)
			_hud.OfferTutorialOnce(_settings);
		_hud.ApplyDevFlags();
	}

	private static AudioStreamWav[] Bank(System.Func<int, float[]> make) =>
		Enumerable.Range(1, 3).Select(seed => ImpactBank<int>.ToStream(make(seed))).ToArray();

	private AudioStreamWav Pick(AudioStreamWav[] bank) => bank[_random.Next(bank.Length)];

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

		// Water: a deep blue glass panel for each sea, with a clear grid drawn over it.
		var water = Materials.For(new Look(Look.GlassPattern, new Color("1e5aa8"), new Color("6fb3ff"), 0.05f, Opacity: 0.85f));
		var grid = new ShaderMaterial { Shader = new Shader { Code = GridShader } };
		grid.SetShaderParameter("cells", (float)Fleet.Size);
		foreach (var sea in new[] { EnemyCenter, HomeCenter })
		{
			_rig.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(SeaHalf * 2, 0.04f, SeaHalf * 2) }, MaterialOverride = water, Position = sea + new Vector3(0, 0.02f, 0) });
			_rig.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(SeaHalf * 2, SeaHalf * 2) }, MaterialOverride = grid, Position = sea + new Vector3(0, 0.045f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
		}
	}

	/// <summary>
	/// The sea grid, drawn per pixel: every line is at least a couple of pixels wide however far
	/// away it is (thin line meshes vanish in the distant sea), the border twice as bold.
	/// </summary>
	private const string GridShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform float cells = 10.0;
uniform vec4 line_color : source_color = vec4(0.88, 0.95, 1.0, 0.75);
uniform float line_px = 2.0;
uniform float edge_px = 4.0;
void fragment() {
	vec2 g = UV * cells;
	vec2 w = max(fwidth(g), vec2(1e-4));
	vec2 d = abs(fract(g - 0.5) - 0.5) / w;
	float line = 1.0 - clamp(min(d.x, d.y) - line_px * 0.5 + 0.5, 0.0, 1.0);
	vec2 e = min(g, cells - g) / w;
	float edge = 1.0 - clamp(min(e.x, e.y) - edge_px * 0.5 + 0.5, 0.0, 1.0);
	ALBEDO = line_color.rgb;
	ALPHA = line_color.a * max(line, edge);
}";

	/// <summary>A ship: a hull with a pointed bow and a little bridge, lying along its squares.</summary>
	private Node3D MakeHull(Ship ship, Vector3 sea)
	{
		var node = new Node3D();
		var look = Styles.Pieces(_settings.Pieces).Two;
		var material = Materials.For(look);
		float length = ship.Length * Cell - 0.08f;
		node.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = Cell * 0.3f, Height = length, RadialSegments = 16, Rings = 4 }, MaterialOverride = material, RotationDegrees = new Vector3(0, 0, 90), Scale = new Vector3(1, 1, 0.55f), Position = new Vector3(0, 0.06f, 0) });
		node.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Cell * 0.5f, 0.16f, Cell * 0.32f) }, MaterialOverride = material, Position = new Vector3(-length * 0.15f, 0.2f, 0) });
		node.Position = HullPosition(ship, sea);
		node.RotationDegrees = new Vector3(0, ship.Across ? 0 : 90, 0);
		_rig.AddChild(node);
		return node;
	}

	private static Vector3 HullPosition(Ship ship, Vector3 sea)
	{
		var first = SquareCenter(sea, ship.Row * Fleet.Size + ship.Column);
		var last = SquareCenter(sea, ship.Squares().Last());
		return (first + last) / 2 + new Vector3(0, 0.04f, 0);
	}

	private static IEnumerable<MeshInstance3D> MeshesOf(Node3D node) => node.GetChildren().OfType<MeshInstance3D>();

	// ---------------------------------------------------------------- Placing your fleet

	/// <summary>The square of a sea under a screen point, or -1.</summary>
	private int SquareAt(Vector2 screen, Vector3 sea)
	{
		var from = _camera.ProjectRayOrigin(screen);
		var direction = _camera.ProjectRayNormal(screen);
		if (direction.Y > -0.01f)
			return -1;
		var local = _rig.ToLocal(from + direction * (-from.Y / direction.Y)) - sea;
		int column = Mathf.FloorToInt(local.X / Cell + 5), row = Mathf.FloorToInt(local.Z / Cell + 5);
		return column is < 0 or >= Fleet.Size || row is < 0 or >= Fleet.Size ? -1 : row * Fleet.Size + column;
	}

	/// <summary>Before the first shot, a press on one of your ships picks it up (instead of spinning the board).</summary>
	private bool TryGrabShip(Vector2 screen)
	{
		if (_started || _busy || _demo || _hud.MenuOpen)
			return false;
		int square = SquareAt(screen, HomeCenter);
		if (square < 0 || _home.ShipAt(square) is not { } ship)
			return false;
		_held = ship;
		_heldPivot = ship.Squares().ToList().IndexOf(square);
		_heldTarget = ship;
		var hull = _homeHulls[ship];
		foreach (var mesh in MeshesOf(hull))
			mesh.MaterialOverlay = Sparkle.Halo;
		CreateTween().TweenProperty(hull, "position:y", HullPosition(ship, HomeCenter).Y + 0.25f, 0.1f);
		return true;
	}

	/// <summary>The held ship follows the finger square by square, glowing where it fits and red where it doesn't.</summary>
	private void DragShip(Vector2 screen)
	{
		if (_held is not { } ship)
			return;
		int square = SquareAt(screen, HomeCenter);
		if (square < 0)
			return;
		int row = square / Fleet.Size, column = square % Fleet.Size;
		var target = ship.Across ? ship.At(row, column - _heldPivot) : ship.At(row - _heldPivot, column);
		if (target == _heldTarget)
			return;
		_heldTarget = target;
		ShowHeld(target);
	}

	private void ShowHeld(Ship target)
	{
		var hull = _homeHulls[_held!];
		hull.Position = HullPosition(target, HomeCenter) + new Vector3(0, 0.25f, 0);
		hull.RotationDegrees = new Vector3(0, target.Across ? 0 : 90, 0);
		bool fits = _home.CanPlace(target, ignoring: _held);
		foreach (var mesh in MeshesOf(hull))
			mesh.MaterialOverlay = fits ? Sparkle.Halo : BlockedOverlay;
	}

	/// <summary>Let go: the ship settles where it fits (or goes back). A tap without moving turns it.</summary>
	private void DropShip(Vector2 screen, bool moved)
	{
		if (_held is not { } ship)
			return;
		var target = moved ? _heldTarget! : ship.Turned(_heldPivot);
		var hull = _homeHulls[ship];
		_homeHulls.Remove(ship);
		bool placed = _home.TryReplace(ship, target);
		var final = placed ? target : ship;
		_homeHulls[final] = hull;
		hull.RotationDegrees = new Vector3(0, final.Across ? 0 : 90, 0);
		foreach (var mesh in MeshesOf(hull))
			mesh.MaterialOverlay = null;
		var settle = CreateTween();
		settle.TweenProperty(hull, "position", HullPosition(final, HomeCenter), 0.12f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		Clack(HullPosition(final, HomeCenter), placed ? 0.6f : 0.3f);
		if (!placed)
			_hud.SetStatus(moved ? "Ships can't touch, even at a corner" : "No room to turn it there", Colors.White);
		_held = null;
		_heldTarget = null;
		if (placed)
			RefreshUi();
	}

	private static readonly StandardMaterial3D BlockedOverlay = new()
	{
		AlbedoColor = new Color(1f, 0.2f, 0.15f, 0.55f),
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		BlendMode = BaseMaterial3D.BlendModeEnum.Add,
	};

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
		// Your guns are near you, theirs far across the table.
		_sound.Play(Pick(_cannon), _rig.ToGlobal(start), atEnemy ? -3f : -8f, 0.95f + (float)_random.NextDouble() * 0.1f);
		await ToSignal(flight, Tween.SignalName.Finished);
		shell.QueueFree();

		var peg = MakePeg(sea, square, result != ShotResult.Miss);
		if (result == ShotResult.Miss)
		{
			Splash(target);
			_sound.Play(Pick(_splash), _rig.ToGlobal(target), -2f, 0.95f + (float)_random.NextDouble() * 0.1f);
		}
		else
		{
			Sparkle.Burst(_rig, target + new Vector3(0, 0.2f, 0));
			_sound.Play(Pick(_hitSound), _rig.ToGlobal(target), 0f, 0.95f + (float)_random.NextDouble() * 0.1f);
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
			await SetWrecksAlight(beaten: atEnemy ? _enemyHulls : _homeHulls);
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
		_sound.Play(Pick(_explosion), hull.GlobalPosition, 3f);
		_hud.SetStatus(enemy ? $"You sank their {ship.Name.ToLowerInvariant()}!" : $"They sank your {ship.Name.ToLowerInvariant()}!", enemy ? new Color("f5c542") : new Color("ff8a8a"));
		await ToSignal(tween, Tween.SignalName.Finished);
		foreach (var mesh in hull.GetChildren().OfType<MeshInstance3D>())
			mesh.MaterialOverlay = null;
		if (enemy)
		{
			_markers.Add(hull);
			_enemyHulls[ship] = hull;
		}
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
		if (_busy || _over || _clip != null)
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
		PutOutFire();
		_enemyHulls.Clear();
		_held = null;
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

	/// <summary>Starting over mid-battle is a surrender (a loss), unless you watch an ad.</summary>
	private void OnRestartPressed()
	{
		if (_busy)
			return;
		if (_over || !_started)
		{
			StartGame();
			return;
		}
		_hud.AskRestart(!_demo, countLoss =>
		{
			if (countLoss)
				_wins.Win(first: false);
			StartGame();
		}, then => _ads.ShowRewarded(rewarded: then, closed: () => { }));
	}

	// ---------------------------------------------------------------- The winner's fire

	/// <summary>Victory: every wreck of the beaten fleet bursts into flames on the water, one after another.</summary>
	private async Task SetWrecksAlight(Dictionary<Ship, Node3D> beaten)
	{
		int i = 0;
		foreach (var (ship, hull) in beaten.OrderByDescending(pair => pair.Key.Length).ToList())
		{
			_fires.Add(Flames.SetAlight(_rig, hull.Position + new Vector3(0, 0.12f, 0), MeshesOf(hull),
				radius: ship.Length * Cell * 0.32f, size: 0.8f + ship.Length * 0.12f, withLight: i == 0));
			i++;
			await Wait(0.18f);
		}
	}

	private void PutOutFire()
	{
		foreach (var fire in _fires)
			fire.Extinguish();
		_fires.Clear();
		foreach (var hull in _homeHulls.Values)
		{
			foreach (var mesh in MeshesOf(hull))
				mesh.MaterialOverlay = null;
		}
	}

	// ---------------------------------------------------------------- Tutorial clips

	/// <summary>
	/// Plays one scripted scene for the tutorial (with --clip=name; Record-Clips.ps1 records it),
	/// then quits. The HUD is hidden; the fleet legend stays.
	/// </summary>
	private async Task PlayClip(string clip)
	{
		// The clip is square: frame the seas closer, with the legend along the very top.
		_camera.Fov = 50;
		_camera.LookAt(new Vector3(0, -0.4f, 0.1f));
		Widgets.Anchor(_legend, Control.LayoutPreset.TopWide, left: 20, right: -20, top: 8, bottom: 64);
		_settings.Muted = false;
		var seeded = new System.Random(5);
		_enemy.Clear();
		_home.Clear();
		_enemy.PlaceRandomly(seeded);
		_home.PlaceRandomly(seeded);
		DrawHomeFleet();
		RefreshUi();
		await Wait(0.8f);
		switch (clip)
		{
			case "place":
			{
				// Pick a ship up, slide it along, set it down; then tap it to turn it.
				var ship = _home.Ships.OrderBy(s => s.Length).First(s => _home.CanPlace(s.Turned(0), ignoring: s));
				_held = ship;
				_heldPivot = 0;
				_heldTarget = ship;
				foreach (var mesh in MeshesOf(_homeHulls[ship]))
					mesh.MaterialOverlay = Sparkle.Halo;
				await Wait(0.4f);
				foreach (int step in Enumerable.Range(1, 3))
				{
					var slid = ship.Across ? ship.At(ship.Row, ship.Column - step) : ship.At(ship.Row - step, ship.Column);
					if (!_home.CanPlace(slid, ignoring: ship))
						break;
					_heldTarget = slid;
					ShowHeld(slid);
					await Wait(0.35f);
				}
				DropShip(Vector2.Zero, moved: true);
				await Wait(0.9f);
				_held = _home.Ships.First(s => s.Name == ship.Name);
				_heldPivot = 0;
				DropShip(Vector2.Zero, moved: false);
				await Wait(1.2f);
				_home.Clear();
				_home.PlaceRandomly(seeded);
				DrawHomeFleet();
				Clack(HomeCenter, 0.5f);
				await Wait(1.2f);
				break;
			}
			case "fire":
			{
				// A miss, then hits along a ship until it sinks.
				var target = _enemy.Ships.First(s => s.Length == 3);
				int miss = Enumerable.Range(0, Fleet.Squares).First(s => _enemy.ShipAt(s) == null && s / Fleet.Size > 3);
				await Fire(miss, atEnemy: true);
				_playerTurn = true;
				await Wait(0.6f);
				foreach (int square in target.Squares())
				{
					await Fire(square, atEnemy: true);
					await Wait(0.5f);
				}
				await Wait(1.2f);
				break;
			}
			default:
			{
				// The last ship goes down, and the beaten fleet burns.
				var ships = _enemy.Ships.ToList();
				foreach (var ship in ships.Take(ships.Count - 1))
				{
					foreach (int square in ship.Squares())
					{
						_enemy.Fire(square);
						MakePeg(EnemyCenter, square, hit: true);
					}
					_enemyHulls[ship] = MakeHull(ship, EnemyCenter);
					_markers.Add(_enemyHulls[ship]);
				}
				_started = true;
				RefreshUi();
				await Wait(0.6f);
				foreach (int square in ships[^1].Squares())
				{
					await Fire(square, atEnemy: true);
					await Wait(0.4f);
				}
				await Wait(2.8f);
				break;
			}
		}
		GetTree().Quit();
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
		_hud.SetRestartVisible(!_over);
		_legend.Set(_enemy.Ships.Select(ship => (ship.Name, ship.Length, _enemy.IsSunk(ship))));
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
			_hud.SetStatus("Place your ships, then fire!", new Color("fff3e0"));
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
				case "--clip":
					_clip = value;
					break;
			}
		}
	}
}

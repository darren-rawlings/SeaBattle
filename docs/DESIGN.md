# Sea Battle: design

Store title: **Sea Battle** (the generic name; "Battleship" is a Hasbro trademark). Package `com.renegadegames.seabattle`.

## The game
You against the computer, each with a 10×10 sea and the classic fleet (carrier 5, battleship 4,
cruiser 3, submarine 3, destroyer 2; ships never touch). Take turns firing at a square of the
other's sea; a hit lets you fire again. Sink the whole enemy fleet to win.

## Feel
- Two glass seas on a deck in the chosen board material: the enemy's far away (hidden), yours
  near with your fleet showing. The grid is drawn per pixel, so its lines stay clear even in the
  far sea.
- Before the first shot, drag a ship to move it (it glows where it fits, red where it would
  touch another) and tap it to turn it; or press Randomize.
- Tap the far sea to fire: a shell arcs over the deck and lands with a splash and a white peg
  (miss) or a burst of sparks and a red peg (hit).
- Synthesised sounds: the cannon's boom (louder for your guns, far off for theirs), a splash for
  a miss, an iron clang and crunch for a hit, an explosion with a rumble and crackle for a sinking.
- A legend under the top buttons shows the enemy ships still to sink, each as long as the ship;
  sunk ones are dimmed and struck out.
- Victory: the beaten fleet's wrecks catch fire on the water.
- **Signature effect, the sinking:** a sunk ship rises out of the water, burning, then rolls over
  and sinks; enemy ships are only revealed this way.
- Drag sideways to spin the board.

## Levels (the computer's gunner)
Easy fires at random and follows up hits half the time; Medium hunts on a checkerboard and
follows hits along the line; Hard fires where a ship is most likely to be (counting every way the
remaining ships could lie, given all hits and misses).

## Ads
Between-games interstitial every 5th game. **Radar (ad)** briefly shows one square of an enemy ship.

## Later
Two players passing one phone; salvo mode.

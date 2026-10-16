using System;
using Renegade.Core.Synth;

namespace SeaBattle.Core;

/// <summary>
/// The battle's sounds, synthesised (no licensed audio): the cannon's boom, a shell splashing into
/// the sea, a shell striking a hull, and a ship exploding. Each is a little different per seed.
/// </summary>
public static class NavalSynth
{
	private const int Rate = ImpactSynth.SampleRate;

	/// <summary>The gun firing: a deep thump that drops in pitch, a sharp crack, and a rumble of smoke.</summary>
	public static float[] Cannon(int seed)
	{
		var rng = new Random(seed);
		var samples = new float[(int)(1.1 * Rate)];
		double pitch = 1 + (rng.NextDouble() - 0.5) * 0.15;
		AddSweep(samples, 0, 95 * pitch, 42 * pitch, gain: 1.0, decay: 0.18);
		AddNoise(samples, rng, 0, brightness: 0.6, gain: 0.7, attack: 0.001, decay: 0.025);
		AddNoise(samples, rng, 0, brightness: 0.05, gain: 1.6, attack: 0.004, decay: 0.35);
		return Finish(samples);
	}

	/// <summary>A miss: the shell slaps into the water, a hollow plop, then spray and droplets falling back.</summary>
	public static float[] Splash(int seed)
	{
		var rng = new Random(seed);
		var samples = new float[(int)(1.4 * Rate)];
		AddNoise(samples, rng, 0, brightness: 0.35, gain: 1.0, attack: 0.012, decay: 0.28, highPass: true);
		AddSweep(samples, 0.01, 260, 110, gain: 0.45, decay: 0.09);
		AddNoise(samples, rng, 0.05, brightness: 0.12, gain: 0.5, attack: 0.08, decay: 0.45, highPass: true);
		// Droplets: tiny bright blips scattered over the tail.
		for (int i = 0; i < 26; i++)
		{
			double at = 0.18 + rng.NextDouble() * 0.9;
			AddSweep(samples, at, 1400 + rng.NextDouble() * 2200, 900 + rng.NextDouble() * 900, gain: 0.05 + rng.NextDouble() * 0.06, decay: 0.012);
		}
		return Finish(samples);
	}

	/// <summary>A hit: a heavy clang of iron, a crunch of splintering, and a muffled boom inside the hull.</summary>
	public static float[] Hit(int seed)
	{
		var rng = new Random(seed);
		var samples = new float[(int)(1.2 * Rate)];
		double pitch = 1 + (rng.NextDouble() - 0.5) * 0.1;
		foreach (var (ratio, gain) in new[] { (1.0, 0.5), (2.32, 0.35), (3.97, 0.22), (5.6, 0.12) })
			AddSweep(samples, 0, 150 * pitch * ratio, 150 * pitch * ratio * 0.97, gain, decay: 0.22 / (1 + ratio * 0.2));
		AddNoise(samples, rng, 0, brightness: 0.5, gain: 0.9, attack: 0.002, decay: 0.07);
		AddNoise(samples, rng, 0.01, brightness: 0.18, gain: 0.6, attack: 0.01, decay: 0.16);
		AddSweep(samples, 0.015, 80, 40, gain: 0.8, decay: 0.2);
		AddNoise(samples, rng, 0.02, brightness: 0.04, gain: 1.2, attack: 0.01, decay: 0.3);
		return Finish(samples);
	}

	/// <summary>A ship sunk: a big blast, a long rolling rumble, and crackling as it burns.</summary>
	public static float[] Explosion(int seed)
	{
		var rng = new Random(seed);
		var samples = new float[(int)(2.4 * Rate)];
		AddNoise(samples, rng, 0, brightness: 0.45, gain: 0.9, attack: 0.002, decay: 0.06);
		AddSweep(samples, 0, 70, 28, gain: 1.2, decay: 0.45);
		AddNoise(samples, rng, 0, brightness: 0.03, gain: 2.4, attack: 0.02, decay: 0.8);
		AddNoise(samples, rng, 0.25, brightness: 0.015, gain: 1.6, attack: 0.3, decay: 0.7);
		// Crackle: sharp little pops through the burn.
		for (int i = 0; i < 40; i++)
		{
			double at = 0.1 + rng.NextDouble() * 1.9;
			AddNoise(samples, rng, at, brightness: 0.7, gain: 0.08 + rng.NextDouble() * 0.12, attack: 0.0005, decay: 0.006, length: 0.04);
		}
		return Finish(samples);
	}

	/// <summary>A sine gliding from one pitch to another, dying away exponentially.</summary>
	private static void AddSweep(float[] samples, double start, double from, double to, double gain, double decay)
	{
		int first = (int)(start * Rate);
		int length = Math.Min(samples.Length - first, (int)(decay * 8 * Rate));
		double phase = 0;
		for (int i = 0; i < length; i++)
		{
			double t = (double)i / Rate;
			double glide = Math.Exp(-t / (decay * 1.5));
			double frequency = to + (from - to) * glide;
			phase += Math.Tau * frequency / Rate;
			double envelope = Math.Min(1, t / 0.002) * Math.Exp(-t / decay);
			samples[first + i] += (float)(gain * envelope * Math.Sin(phase));
		}
	}

	/// <summary>
	/// Low-passed noise (brightness 0..1: how much of the high end gets through), optionally with
	/// the deep end taken out, shaped by an attack and an exponential decay.
	/// </summary>
	private static void AddNoise(float[] samples, Random rng, double start, double brightness, double gain, double attack, double decay, bool highPass = false, double length = 0)
	{
		int first = (int)(start * Rate);
		int count = Math.Min(samples.Length - first, (int)((length > 0 ? length : decay * 8) * Rate));
		double low = 0, slow = 0;
		for (int i = 0; i < count; i++)
		{
			double t = (double)i / Rate;
			low += brightness * (rng.NextDouble() * 2 - 1 - low);
			slow += 0.01 * (low - slow);
			double value = highPass ? low - slow : low;
			double envelope = Math.Min(1, t / attack) * Math.Exp(-t / decay);
			samples[first + i] += (float)(gain * envelope * value);
		}
	}

	/// <summary>Short fades at both ends, then a peak of 0.9.</summary>
	private static float[] Finish(float[] samples)
	{
		int fade = Rate / 50;
		for (int i = 0; i < fade && i < samples.Length; i++)
			samples[samples.Length - 1 - i] *= (float)i / fade;
		float max = 0;
		foreach (float s in samples)
			max = Math.Max(max, Math.Abs(s));
		if (max > 0)
		{
			for (int i = 0; i < samples.Length; i++)
				samples[i] *= 0.9f / max;
		}
		return samples;
	}
}

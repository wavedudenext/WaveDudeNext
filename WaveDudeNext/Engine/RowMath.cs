// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alexander Graef <alex@graef.cc>
//
// This file is part of WaveDude Next, a 2D wave simulator.
//
// Based on the work of
//   Jeroen Vleggaar (Huygens Optics), author of Wave Dude 2D, Copyright (C) 2026
//     <https://www.youtube.com/@HuygensOptics>, <https://www.huygensoptics.com>
//   0x23 (Diffraction Limited), author of WaveSimulator2D
//     <https://www.youtube.com/@DiffractionLimited>, <https://github.com/0x23/WaveSimulator2D>
//
// WaveDude Next is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
// <https://www.gnu.org/licenses/>.

using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;

namespace WaveDudeNext.Engine;

/// <summary>
/// The operations on a row of samples that the capture's conversion (<see cref="FrameConversion"/>) and the 16-bit
/// screen path (<see cref="Rendering.ScreenRenderer"/>) are made of: floats to halves and back, square roots, a
/// product, a scaled sum.
/// <para>
/// Everywhere but in a browser they are <see cref="TensorPrimitives"/>' (System.Numerics.Tensors), as they were.
/// In a browser they are the loops of this class (2026-11-21, the user's choice after the measurement): compiled
/// ahead of time for WebAssembly, the framework's generic implementations took 34.6 ms for the square roots of a
/// Full HD plane, row by row on the pool's threads, against 0.14 ms natively, and 5.9 ms against 0.34 for its
/// halves, which made a capture of both planes 15 times and the screen path 14.5 times slower than native, where
/// the rest of the library is 1.6 to 3.5 times; a plain loop in lanes takes 0.6 ms for the roots there. Natively
/// the framework's square root is the faster one (it has eight lanes where <see cref="Vector{T}"/> has four on a
/// processor without AVX2), so the desktop keeps what it had.
/// </para>
/// <para>
/// The values are the same, bit for bit: the two conversions were compared with the framework's over every input
/// there is (all 4,294,967,296 float bit patterns, NaN payloads included, and all 65,536 halves), and
/// <c>library_check</c> compares every loop here with the framework's on each run. All methods are pure and may be
/// called from any thread; a row may be converted in place only where its summary says so.
/// </para>
/// </summary>
internal static class RowMath
{
	/// <summary>Each float as the nearest <see cref="Half"/> (ties to even), <paramref name="source"/>.Length of them.</summary>
	/// <exception cref="ArgumentException">The destination is shorter than the source.</exception>
	public static void ToHalves(ReadOnlySpan<float> source, Span<Half> destination)
	{
		if (OperatingSystem.IsBrowser())
		{
			HalvesInLanes(source, destination);
		}
		else
		{
			TensorPrimitives.ConvertToHalf(source, destination);
		}
	}

	/// <summary>Each <see cref="Half"/> as its float, exactly, <paramref name="source"/>.Length of them.</summary>
	/// <exception cref="ArgumentException">The destination is shorter than the source.</exception>
	public static void ToSingles(ReadOnlySpan<Half> source, Span<float> destination)
	{
		if (OperatingSystem.IsBrowser())
		{
			SinglesInLanes(source, destination);
		}
		else
		{
			TensorPrimitives.ConvertToSingle(source, destination);
		}
	}

	/// <summary>Each value's square root, in place.</summary>
	public static void Roots(Span<float> values)
	{
		if (OperatingSystem.IsBrowser())
		{
			RootsInLanes(values);
		}
		else
		{
			TensorPrimitives.Sqrt(values, values);
		}
	}

	/// <summary>Each value's square, in place.</summary>
	public static void Square(Span<float> values)
	{
		if (OperatingSystem.IsBrowser())
		{
			SquareInLanes(values);
		}
		else
		{
			TensorPrimitives.Multiply(values, values, values);
		}
	}

	/// <summary><paramref name="destination"/>[i] = <paramref name="source"/>[i] × <paramref name="factor"/>.</summary>
	/// <exception cref="ArgumentException">The destination is shorter than the source.</exception>
	public static void Scale(ReadOnlySpan<float> source, float factor, Span<float> destination)
	{
		if (OperatingSystem.IsBrowser())
		{
			ScaleInLanes(source, factor, destination);
		}
		else
		{
			TensorPrimitives.Multiply(source, factor, destination);
		}
	}

	/// <summary><paramref name="sum"/>[i] += <paramref name="source"/>[i] × <paramref name="factor"/>, the product rounded before it is added.</summary>
	/// <exception cref="ArgumentException">The sum is shorter than the source.</exception>
	public static void AddScaled(ReadOnlySpan<float> source, float factor, Span<float> sum)
	{
		if (OperatingSystem.IsBrowser())
		{
			AddScaledInLanes(source, factor, sum);
		}
		else
		{
			TensorPrimitives.MultiplyAdd(source, factor, sum, sum);
		}
	}

	private static void Fits(int source, int destination)
	{
		if (destination < source)
		{
			throw new ArgumentException("The destination is shorter than the source.");
		}
	}

	// The shifts of the two conversions. In a browser they are WebAssembly's own instructions, asked for by name
	// (2026-11-22): the compiler there does not make them of Vector128's general shifts, which left the halves'
	// loop at 7.9 ns a value on one thread and the singles' at 3.2, against 1.3 and 0.7 with these; every other
	// lane operation of the loops was the processor's own already (measured one by one). Everywhere else they are
	// the general ones; which is taken is settled when the method is compiled.

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Vector128<int> Left(Vector128<int> value, int count) =>
		PackedSimd.IsSupported ? PackedSimd.ShiftLeft(value, count) : Vector128.ShiftLeft(value, count);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Vector128<uint> Left(Vector128<uint> value, int count) =>
		PackedSimd.IsSupported ? PackedSimd.ShiftLeft(value, count) : Vector128.ShiftLeft(value, count);

	/// <summary>The sign bit's copies come in from the left.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Vector128<int> RightSigned(Vector128<int> value, int count) =>
		PackedSimd.IsSupported ? PackedSimd.ShiftRightArithmetic(value, count) : Vector128.ShiftRightArithmetic(value, count);

	/// <summary>Zeros come in from the left.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Vector128<int> RightUnsigned(Vector128<int> value, int count) =>
		PackedSimd.IsSupported ? PackedSimd.ShiftRightLogical(value, count) : Vector128.ShiftRightLogical(value, count);

	/// <summary>
	/// <see cref="ToHalves"/> four at a time with integer operations alone (Fabian Giesen's float_to_half_fast3_rtne,
	/// with a NaN's payload carried over as the framework carries it); the last one to three by the cast.
	/// </summary>
	internal static void HalvesInLanes(ReadOnlySpan<float> source, Span<Half> destination)
	{
		Fits(source.Length, destination.Length);
		Vector128<uint> signMask = Vector128.Create(0x80000000u);
		// from here up a float is an infinity in a half
		Vector128<int> overflow = Vector128.Create((127 + 16) << 23);
		Vector128<int> quietBit = Vector128.Create(0x200);
		Vector128<int> payloadMask = Vector128.Create(0x3ff);
		Vector128<int> infinity = Vector128.Create(0x7c00);
		// the smallest float that is a normal half
		Vector128<int> minNormal = Vector128.Create((127 - 14) << 23);
		// added as a float it rounds a subnormal half's mantissa in the float's own arithmetic
		Vector128<int> subnormalMagic = Vector128.Create(((127 - 15) + (23 - 10) + 1) << 23);
		// moves the exponent to the half's and rounds the mantissa half up; the odd bit below makes that ties to even
		Vector128<int> normalBias = Vector128.Create(0xfff - ((127 - 15) << 23));
		ref float from = ref MemoryMarshal.GetReference(source);
		ref ushort to = ref Unsafe.As<Half, ushort>(ref MemoryMarshal.GetReference(destination));
		int i = 0;
		for (; i <= source.Length - 4; i += 4)
		{
			Vector128<float> value = Vector128.LoadUnsafe(ref from, (nuint)i);
			Vector128<uint> sign = value.AsUInt32() & signMask;
			Vector128<float> magnitude = (value.AsUInt32() ^ sign).AsSingle();
			Vector128<int> bits = magnitude.AsInt32();
			Vector128<int> isNaN = ~Vector128.Equals(magnitude, magnitude).AsInt32();
			Vector128<int> isRegular = Vector128.GreaterThan(overflow, bits);
			Vector128<int> special = (isNaN & (quietBit | (RightUnsigned(bits, 13) & payloadMask))) | infinity;
			Vector128<int> isSubnormal = Vector128.GreaterThan(minNormal, bits);
			Vector128<int> subnormal = (magnitude + subnormalMagic.AsSingle()).AsInt32() - subnormalMagic;
			Vector128<int> mantissaOdd = RightSigned(Left(bits, 31 - 13), 31);
			Vector128<int> normal = RightUnsigned(bits + normalBias - mantissaOdd, 13);
			Vector128<int> regular = Vector128.ConditionalSelect(isSubnormal, subnormal, normal);
			Vector128<int> half = Vector128.ConditionalSelect(isRegular, regular, special) | RightSigned(sign.AsInt32(), 16);
			// the low 16 bits of each of the four, side by side
			Vector128<ushort> narrow = Vector128.Narrow(half.AsUInt32(), half.AsUInt32());
			Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref to, i)), narrow.AsUInt64().ToScalar());
		}
		for (; i < source.Length; i++)
		{
			destination[i] = (Half)source[i];
		}
	}

	/// <summary>
	/// <see cref="ToSingles"/> four at a time: the steps of <see cref="Half"/>'s own conversion to a float, lane by
	/// lane; the last one to three by the cast.
	/// </summary>
	internal static void SinglesInLanes(ReadOnlySpan<Half> source, Span<float> destination)
	{
		Fits(source.Length, destination.Length);
		Vector128<uint> signMask = Vector128.Create(0x8000u);
		Vector128<uint> exponentMask = Vector128.Create(0x7C00u);
		// the smallest normal half as a float, which a subnormal half is built on and then loses again
		Vector128<uint> lowerBound = Vector128.Create(0x3880_0000u);
		// what a half's exponent is short of a float's, once the bits are in the float's places
		Vector128<uint> exponentOffset = Vector128.Create(0x3800_0000u);
		Vector128<uint> valueMask = Vector128.Create(0x0FFF_E000u);
		ref ushort from = ref Unsafe.As<Half, ushort>(ref MemoryMarshal.GetReference(source));
		ref float to = ref MemoryMarshal.GetReference(destination);
		int i = 0;
		for (; i <= source.Length - 4; i += 4)
		{
			Vector128<uint> half = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref from, i)))).AsUInt16());
			Vector128<uint> sign = Left(half & signMask, 16);
			Vector128<uint> exponent = half & exponentMask;
			Vector128<uint> isSubnormal = Vector128.Equals(exponent, Vector128<uint>.Zero);
			Vector128<uint> isSpecial = Vector128.Equals(exponent, exponentMask);
			Vector128<uint> subnormalBase = isSubnormal & lowerBound;
			Vector128<uint> offset = exponentOffset | subnormalBase;
			// twice the offset for an infinity or a NaN: the exponent becomes the float's all ones
			offset += offset & isSpecial;
			Vector128<uint> bits = (Left(half, 13) & valueMask) + offset;
			Vector128<float> magnitude = bits.AsSingle() - subnormalBase.AsSingle();
			(magnitude.AsUInt32() | sign).AsSingle().StoreUnsafe(ref to, (nuint)i);
		}
		for (; i < source.Length; i++)
		{
			destination[i] = (float)source[i];
		}
	}

	/// <summary><see cref="Roots"/> in lanes of <see cref="Vector{T}"/>.</summary>
	internal static void RootsInLanes(Span<float> values)
	{
		ref float first = ref MemoryMarshal.GetReference(values);
		int i = 0;
		for (; i <= values.Length - Vector<float>.Count; i += Vector<float>.Count)
		{
			Vector.SquareRoot(Vector.LoadUnsafe(ref first, (nuint)i)).StoreUnsafe(ref first, (nuint)i);
		}
		for (; i < values.Length; i++)
		{
			values[i] = MathF.Sqrt(values[i]);
		}
	}

	/// <summary><see cref="Square"/> in lanes of <see cref="Vector{T}"/>.</summary>
	internal static void SquareInLanes(Span<float> values)
	{
		ref float first = ref MemoryMarshal.GetReference(values);
		int i = 0;
		for (; i <= values.Length - Vector<float>.Count; i += Vector<float>.Count)
		{
			Vector<float> lane = Vector.LoadUnsafe(ref first, (nuint)i);
			(lane * lane).StoreUnsafe(ref first, (nuint)i);
		}
		for (; i < values.Length; i++)
		{
			values[i] *= values[i];
		}
	}

	/// <summary><see cref="Scale"/> in lanes of <see cref="Vector{T}"/>.</summary>
	internal static void ScaleInLanes(ReadOnlySpan<float> source, float factor, Span<float> destination)
	{
		Fits(source.Length, destination.Length);
		ref float from = ref MemoryMarshal.GetReference(source);
		ref float to = ref MemoryMarshal.GetReference(destination);
		Vector<float> factors = new(factor);
		int i = 0;
		for (; i <= source.Length - Vector<float>.Count; i += Vector<float>.Count)
		{
			(Vector.LoadUnsafe(ref from, (nuint)i) * factors).StoreUnsafe(ref to, (nuint)i);
		}
		for (; i < source.Length; i++)
		{
			destination[i] = source[i] * factor;
		}
	}

	/// <summary><see cref="AddScaled"/> in lanes of <see cref="Vector{T}"/>.</summary>
	internal static void AddScaledInLanes(ReadOnlySpan<float> source, float factor, Span<float> sum)
	{
		Fits(source.Length, sum.Length);
		ref float from = ref MemoryMarshal.GetReference(source);
		ref float to = ref MemoryMarshal.GetReference(sum);
		Vector<float> factors = new(factor);
		int i = 0;
		for (; i <= source.Length - Vector<float>.Count; i += Vector<float>.Count)
		{
			(Vector.LoadUnsafe(ref from, (nuint)i) * factors + Vector.LoadUnsafe(ref to, (nuint)i)).StoreUnsafe(ref to, (nuint)i);
		}
		for (; i < source.Length; i++)
		{
			sum[i] = source[i] * factor + sum[i];
		}
	}
}

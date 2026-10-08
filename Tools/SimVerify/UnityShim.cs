// Minimal Unity API shim for the headless ballistics/damage verification.
// Only the members WarArena.Ballistics and WarArena.Vehicles actually touch.

using System;

namespace UnityEngine
{
    public static class Mathf
    {
        public const float Deg2Rad = 0.0174532924f;
        public const float Rad2Deg = 57.29578f;

        public static float Abs(float v) => Math.Abs(v);
        public static int Abs(int v) => Math.Abs(v);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static float Sqrt(float v) => (float)Math.Sqrt(v);
        public static float Round(float v) => (float)Math.Round(v, MidpointRounding.ToEven);
        public static float Pow(float a, float b) => (float)Math.Pow(a, b);
        public static float Exp(float v) => (float)Math.Exp(v);
        public static float Log(float v) => (float)Math.Log(v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        public static float Cos(float r) => (float)Math.Cos(r);
        public static float Acos(float v) => (float)Math.Acos(v);
        public static float Dot(float ax, float ay, float az, float bx, float by, float bz)
            => ax * bx + ay * by + az * bz;

        /// <summary>Unity's Random is used only for the fire-on-destroy roll.</summary>
        public static float Abs01(float v) => Clamp01(v);
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 forward => new Vector3(0, 0, 1);

        public float magnitude => Mathf.Sqrt(x * x + y * y + z * z);

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float s) => new Vector3(a.x * s, a.y * s, a.z * s);
        public static Vector3 operator /(Vector3 a, float s) => new Vector3(a.x / s, a.y / s, a.z / s);

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;

        public override string ToString() => $"({x}, {y}, {z})";
    }

    public class Quaternion
    {
        public static Quaternion identity => new Quaternion();
    }

    /// <summary>
    /// Rigid transform stand-in. Only the two inverse operations the damage model
    /// needs, with correct rigid-body inverse semantics (rotate by conjugate, then
    /// translate by the negated rotated position).
    /// </summary>
    public class Transform
    {
        public Vector3 position;
        public Vector3 forward = new Vector3(0, 0, 1);

        public Vector3 TransformPoint(Vector3 p) => p + position;
        public Vector3 InverseTransformPoint(Vector3 p) => p - position;

        public Vector3 InverseTransformDirection(Vector3 d) => d;

        public static implicit operator Vector3(Transform t) => t.position;
    }

    public class Object
    {
        public string name = string.Empty;
    }

    public class Component : Object { }

    public class MonoBehaviour : Component { }

    /// <summary>Mirrors UnityEngine.Random for the fire-on-destroy roll.</summary>
    public static class Random
    {
        private static readonly System.Random Rng = new System.Random(12345);
        public static float value => (float)Rng.NextDouble();
    }
}
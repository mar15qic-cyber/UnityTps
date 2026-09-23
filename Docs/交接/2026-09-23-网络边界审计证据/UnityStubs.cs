// Pure logic audit only. These stubs do not simulate Unity physics or FishNet.
using System;
namespace UnityEngine
{
    public struct Vector2(float x, float y)
    {
        public float x=x, y=y;
        public static Vector2 zero => default;
        public static Vector2 ClampMagnitude(Vector2 v, float max)
        { float m=MathF.Sqrt(v.x*v.x+v.y*v.y); return m>max ? new(v.x*max/m,v.y*max/m):v; }
    }
    public struct Vector3(float x, float y, float z)
    {
        public float x=x,y=y,z=z;
        public static Vector3 zero => default;
        public float sqrMagnitude => x*x+y*y+z*z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b);
        public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;
        public static Vector3 Lerp(Vector3 a,Vector3 b,float t)=>a+(b-a)*Math.Clamp(t,0,1);
        public static float Angle(Vector3 a,Vector3 b)=>throw new NotSupportedException("Not used by audit");
    }
    public struct Quaternion
    {
        public Vector3 eulerAngles => default;
        public static Quaternion Slerp(Quaternion a,Quaternion b,float t)=>throw new NotSupportedException("Not used by audit");
    }
    public static class Mathf
    {
        public static float Abs(float v)=>MathF.Abs(v);
        public static float Exp(float v)=>MathF.Exp(v);
        public static float DeltaAngle(float a,float b)=>((b-a+540)%360)-180;
    }
    public static class Time { public static int frameCount; public static double unscaledTimeAsDouble; }
}
namespace Game.Gameplay.Movement { public enum LocomotionState { Idle, Walk, Sprint, Jump, Air, Land } }

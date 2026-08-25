using System;
using UnityEngine;

namespace Game.Gameplay.Movement
{
    public enum MovementSimulationMode
    {
        OfflineLocal,
        PredictedOwner,
        ServerAuthority,
        RemoteProxy
    }

    [Serializable]
    public struct MovementCommand
    {
        public Vector2 Move;
        public bool Sprint;
        public bool Jump;
        public float YawDelta;
        public uint Tick;

        public MovementCommand(Vector2 move, bool sprint, bool jump, float yawDelta, uint tick)
        {
            Move = Vector2.ClampMagnitude(move, 1f);
            Sprint = sprint;
            Jump = jump;
            YawDelta = yawDelta;
            Tick = tick;
        }
    }

    [Serializable]
    public struct MovementSnapshot
    {
        public uint Tick;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 HorizontalVelocity;
        public float VerticalVelocity;
        public LocomotionState LocomotionState;
        public float GaitPhase;
    }
}

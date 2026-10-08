using UnityEngine;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Tank input contract. An interface so play-mode tests inject a deterministic
    /// fake instead of simulating devices, which the Input System does not accept
    /// reliably in batch mode.
    ///
    /// Note what is NOT here: no edge detection. Drive is a held axis, and the
    /// simulation samples it once per tick, so there is nothing to debounce.
    /// Firing is a held boolean and the reload timer in the simulation is what stops
    /// it becoming a machine gun.
    /// </summary>
    public interface ITankInputReader
    {
        /// <summary>-1 reverse .. +1 forward</summary>
        float ReadThrottle();

        /// <summary>-1 left .. +1 right</summary>
        float ReadSteer();

        /// <summary>Turret fine trim, -1..1, applied on top of mouse aim.</summary>
        float ReadTurretTrim();

        /// <summary>Whether the fire control is currently held.</summary>
        bool ReadFire();

        /// <summary>True on the frame the camera toggle is pressed.</summary>
        bool ReadCameraToggle();
    }

    /// <summary>
    /// Aircraft input contract: the standard six, plus fire and the mode switch.
    ///
    /// Held axes, no edge detection, for the same reason as the tank: the flight
    /// model integrates these directly every tick, and fire is rate-limited by the
    /// gun's rate of fire rather than by the input layer.
    /// </summary>
    public interface IAircraftInputReader
    {
        /// <summary>-1 nose down .. +1 nose up</summary>
        float ReadPitch();

        /// <summary>-1 roll left .. +1 roll right</summary>
        float ReadRoll();

        /// <summary>Rudder, -1..1</summary>
        float ReadYaw();

        /// <summary>Throttle change, -1..1</summary>
        float ReadThrottle();

        /// <summary>Gun trigger, held.</summary>
        bool ReadFire();

        /// <summary>True on the frame the ground/air mode switch is pressed.</summary>
        bool ReadModeToggle();
    }
}
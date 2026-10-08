using UnityEngine;
using UnityEngine.InputSystem;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Real keyboard and mouse input for the tank.
    ///
    /// Reads HELD state only. Fire is deliberately not edge-triggered here: the
    /// reload timer lives in the battle loop, so a held fire control produces a
    /// steady rate of fire limited by the gun's reload, which is the behaviour a
    /// player expects.
    /// </summary>
    public class KeyboardTankInput : ITankInputReader
    {
        private Keyboard _keyboard;
        private Mouse _mouse;
        private bool _cameraToggleWasDown;

        private Keyboard Keyboard => _keyboard != null ? _keyboard : (_keyboard = Keyboard.current);
        private Mouse Mouse => _mouse != null ? _mouse : (_mouse = Mouse.current);

        public float ReadThrottle()
        {
            Keyboard k = Keyboard;
            if (k == null) return 0f;
            float v = 0f;
            if (Down(k, Key.W)) v += 1f;
            if (Down(k, Key.S)) v -= 1f;
            return v;
        }

        public float ReadSteer()
        {
            Keyboard k = Keyboard;
            if (k == null) return 0f;
            float v = 0f;
            if (Down(k, Key.D)) v += 1f;
            if (Down(k, Key.A)) v -= 1f;
            return v;
        }

        public float ReadTurretTrim()
        {
            Keyboard k = Keyboard;
            if (k == null) return 0f;
            float v = 0f;
            if (Down(k, Key.E)) v += 1f;
            if (Down(k, Key.Q)) v -= 1f;
            return v;
        }

        public bool ReadFire()
        {
            Keyboard k = Keyboard;
            Mouse m = Mouse;
            if (m != null && m.leftButton.isPressed) return true;
            return Down(k, Key.Space);
        }

        /// <summary>
        /// Edge-detected on purpose: a camera toggle is a discrete action, and
        /// reading the held state would flip the camera every frame.
        /// </summary>
        public bool ReadCameraToggle()
        {
            Keyboard k = Keyboard;
            if (k == null) return false;
            bool down = Down(k, Key.C);
            bool edge = down && !_cameraToggleWasDown;
            _cameraToggleWasDown = down;
            return edge;
        }

        private static bool Down(Keyboard k, Key key)
        {
            return k != null && k[key].isPressed;
        }
    }
}
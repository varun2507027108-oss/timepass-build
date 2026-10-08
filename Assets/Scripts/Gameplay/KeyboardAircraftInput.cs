using UnityEngine;
using UnityEngine.InputSystem;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Keyboard flight controls using the standard "pitch is roll" mapping.
    ///
    /// Arrow/W-S control pitch, A-D control roll, Q-E apply rudder, and Shift/Ctrl
    /// manage the throttle. A pure stick-roll mapping reads better on a keyboard
    /// than raw pitch, because rolling and pulling are almost always the same
    /// input in a dogfight.
    ///
    /// Every axis is a held axis. Throttle is a rate rather than a position, so
    /// releasing the key holds the setting, which is what a throttle lever does.
    /// </summary>
    public class KeyboardAircraftInput : IAircraftInputReader
    {
        private Keyboard _keyboard;
        private bool _modeToggleWasDown;

        private Keyboard Keyboard => _keyboard != null ? _keyboard : (_keyboard = Keyboard.current);

        public float ReadPitch()
        {
            Keyboard k = Keyboard;
            if (k == null) return 0f;
            float v = 0f;
            if (Down(k, Key.W) || Down(k, Key.UpArrow)) v += 1f;
            if (Down(k, Key.S) || Down(k, Key.DownArrow)) v -= 1f;
            return v;
        }

        public float ReadRoll()
        {
            Keyboard k = Keyboard;
            if (k == null) return 0f;
            float v = 0f;
            if (Down(k, Key.D) || Down(k, Key.RightArrow)) v += 1f;
            if (Down(k, Key.A) || Down(k, Key.LeftArrow)) v -= 1f;
            return v;
        }

        public float ReadYaw()
        {
            Keyboard k = Keyboard;
            if (k == null) return 0f;
            float v = 0f;
            if (Down(k, Key.E)) v += 1f;
            if (Down(k, Key.Q)) v -= 1f;
            return v;
        }

        public float ReadThrottle()
        {
            Keyboard k = Keyboard;
            if (k == null) return 0f;
            float v = 0f;
            if (Down(k, Key.LeftShift) || Down(k, Key.PageUp)) v += 1f;
            if (Down(k, Key.LeftCtrl) || Down(k, Key.PageDown)) v -= 1f;
            return v;
        }

        public bool ReadFire()
        {
            Keyboard k = Keyboard;
            if (k == null) return false;
            return Down(k, Key.Space);
        }

        /// <summary>Edge-detected: a mode switch is a discrete action, not a held axis.</summary>
        public bool ReadModeToggle()
        {
            Keyboard k = Keyboard;
            if (k == null) return false;
            bool down = Down(k, Key.Tab);
            bool edge = down && !_modeToggleWasDown;
            _modeToggleWasDown = down;
            return edge;
        }

        private static bool Down(Keyboard k, Key key)
        {
            return k != null && k[key].isPressed;
        }
    }
}
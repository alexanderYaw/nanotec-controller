using System;
using System.Runtime.InteropServices;

namespace MotorControlApp
{
    /// <summary>
    /// Decoded snapshot of the joystick. Each direction is digital: -1, 0, or +1, in
    /// machine-positive convention (right / up / + = +1). Deadman must be held for any
    /// motion; Fast selects the high jog speed.
    /// </summary>
    public readonly record struct JoystickState(
        bool Connected, int X, int Y, int Z, int R, bool Deadman, bool Fast);

    /// <summary>
    /// Minimal digital-joystick reader over the Windows multimedia API
    /// (winmm <c>joyGetPosEx</c>). No external package, no TFM change — works with any
    /// controller Windows lists in <c>joy.cpl</c>. Axis positions are quantised to
    /// -1/0/+1 (a digital stick parks at the extremes), and the POV hat is folded into
    /// X/Y so a D-pad works too. Z and Θ also accept buttons, since a basic stick often
    /// lacks a third/fourth axis.
    ///
    /// Reads only: the caller owns the poll timer, maps the state onto
    /// <see cref="MultiAxisController"/>, and owns the STOP/safety policy (deadman,
    /// disconnect, focus loss).
    ///
    /// Default button map (1-based as shown in joy.cpl):
    ///   1 = Deadman (hold to allow motion)   2 = Fast modifier
    ///   3 = Z-   4 = Z+                       5 = Θ-   6 = Θ+
    /// </summary>
    public sealed class Joystick
    {
        private const int JOYSTICKID1 = 0;
        private const int JOY_RETURNALL = 0x000000FF; // X,Y,Z,R,U,V,POV,buttons
        private const int JOYERR_NOERROR = 0;
        private const int POV_CENTERED = 0xFFFF;

        // Axis range is 0..65535 (centre ~32767). A digital stick sits at the rails.
        private const int LOW = 16384;
        private const int HIGH = 49152;

        // Button bitmasks (0-based bit -> 1-based label in joy.cpl).
        private const int BTN_DEADMAN = 0x0001; // button 1
        private const int BTN_FAST    = 0x0002; // button 2
        private const int BTN_Z_NEG   = 0x0004; // button 3
        private const int BTN_Z_POS   = 0x0008; // button 4
        private const int BTN_T_NEG   = 0x0010; // button 5
        private const int BTN_T_POS   = 0x0020; // button 6

        private readonly int _joyId;
        public Joystick(int joyId = JOYSTICKID1) => _joyId = joyId;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOYINFOEX
        {
            public int dwSize, dwFlags;
            public int dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
            public int dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
        }

        [DllImport("winmm.dll")]
        private static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

        [DllImport("winmm.dll")]
        private static extern int joyGetNumDevs();

        /// <summary>Reads the joystick now. Returns Connected=false if none is present.</summary>
        public JoystickState Poll()
        {
            var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
            if (joyGetNumDevs() == 0 || joyGetPosEx(_joyId, ref info) != JOYERR_NOERROR)
                return new JoystickState(false, 0, 0, 0, 0, false, false);

            int x = Quantise(info.dwXpos);          // +1 = right
            int y = -Quantise(info.dwYpos);         // winmm Y is +down → invert so +1 = up

            // Fold the POV hat into X/Y (0=N/up, 9000=E/right, 18000=S, 27000=W).
            if (info.dwPOV != POV_CENTERED)
            {
                int deg = info.dwPOV / 100;
                if (deg >= 45 && deg <= 135) x = +1; else if (deg >= 225 && deg <= 315) x = -1;
                if (deg >= 315 || deg <= 45) y = +1; else if (deg >= 135 && deg <= 225) y = -1;
            }

            int b = info.dwButtons;
            int z = ButtonDir(b, BTN_Z_NEG, BTN_Z_POS); if (z == 0) z = -Quantise(info.dwZpos);
            int r = ButtonDir(b, BTN_T_NEG, BTN_T_POS); if (r == 0) r = Quantise(info.dwRpos);

            return new JoystickState(
                Connected: true, X: x, Y: y, Z: z, R: r,
                Deadman: (b & BTN_DEADMAN) != 0, Fast: (b & BTN_FAST) != 0);
        }

        private static int ButtonDir(int buttons, int negMask, int posMask)
        {
            bool neg = (buttons & negMask) != 0, pos = (buttons & posMask) != 0;
            return pos == neg ? 0 : pos ? +1 : -1; // both or neither pressed = 0
        }

        private static int Quantise(int pos) => pos < LOW ? -1 : pos > HIGH ? +1 : 0;
    }
}

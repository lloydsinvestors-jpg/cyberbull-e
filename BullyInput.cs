// Bully mode input (Austin, 2026-10-08: "Bully-mode toggle"). One key flips Schedule I <-> Bully controls.
// In Bully mode Jimmy uses Bully: Scholarship Edition's own layout (official PC manual):
//   pad:      LB/RB cycle weapon, RT fire, LT lock-on, X melee, Y grapple/interact, B jump, A sprint, R3 crouch, L3 look back
//   keyboard: scroll wheel cycle weapon, left click fire, right click lock-on, Left Alt melee, R grapple, Space jump,
//             Left Shift sprint, X crouch, C look back, wheel click put the weapon away
// Schedule I keeps everything Bully doesn't claim (WASD, E to interact/deal, Tab phone, I inventory...).
// The claimed buttons are taken off Schedule I by patching its single input gate, GameInput.GetButton*/MouseScrollDelta.
using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppScheduleOne;
using UnityEngine;

namespace BullyLink
{
    public static class BullyInput
    {
        public static bool BullyMode;
        /// <summary>A Schedule I screen / station / car has the player (Mod.SchedOneUiOpen): every key is the game's.</summary>
        public static bool GameHasControl;

        // ---- Xbox pad (XInput, read directly, so it works whatever Schedule I thinks the device is) ----
        [StructLayout(LayoutKind.Sequential)]
        struct XState { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState(int user, out XState s);

        public const ushort DUp = 0x1, DDown = 0x2, DLeft = 0x4, DRight = 0x8, Start = 0x10, Back = 0x20, L3 = 0x40, R3 = 0x80,
            LB = 0x100, RB = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
        static ushort _pad, _padPrev;
        static bool _lt, _rt, _ltPrev, _rtPrev;
        public static bool PadConnected;

        // ---- keyboard / mouse (only while Schedule I has the focus) ----
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        public const int VK_LBUTTON = 1, VK_RBUTTON = 2, VK_MBUTTON = 4, VK_LMENU = 0xA4, VK_LSHIFT = 0xA0, VK_SPACE = 0x20,
            VK_R = 0x52, VK_X = 0x58, VK_C = 0x43, VK_F = 0x46, VK_T = 0x54, VK_G = 0x47, VK_F6 = 0x75, VK_F8 = 0x77, VK_F10 = 0x79;
        static readonly bool[] _key = new bool[256], _keyPrev = new bool[256];
        public static bool Focused { get; private set; }

        static float _scroll;            // Schedule I's scroll this frame, captured before it's hidden from the game
        internal static float RawScroll;
        public static float Scroll => _scroll;

        public static void Update()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            Focused = pid == (uint)Environment.ProcessId;
            for (int vk = 1; vk < 256; vk++)
            {
                _keyPrev[vk] = _key[vk];
                _key[vk] = Focused && (GetAsyncKeyState(vk) & 0x8000) != 0;
            }
            _padPrev = _pad; _ltPrev = _lt; _rtPrev = _rt;
            PadConnected = XInputGetState(0, out var s) == 0;
            _pad = PadConnected && Focused ? s.Buttons : (ushort)0;
            _lt = PadConnected && Focused && s.LT > 100;
            _rt = PadConnected && Focused && s.RT > 100;
            StickX = PadConnected && Focused ? Dead(s.LX / 32767f) : 0f;
            StickY = PadConnected && Focused ? Dead(s.LY / 32767f) : 0f;
            Trigger = PadConnected && Focused ? s.RT / 255f : 0f;
            try { _ = GameInput.MouseScrollDelta; _scroll = RawScroll; } catch { _scroll = 0; }   // the patch below records the raw value
        }

        public static float StickX, StickY, Trigger;     // left stick (deadzoned) and right trigger, for the bike / moped
        static float Dead(float v) => Mathf.Abs(v) < 0.2f ? 0f : v;

        public static bool Key(int vk) => _key[vk];
        public static bool KeyDown(int vk) => _key[vk] && !_keyPrev[vk];
        public static bool Pad(ushort b) => (_pad & b) != 0;
        public static bool PadDown(ushort b) => (_pad & b) != 0 && (_padPrev & b) == 0;

        // ---- Bully actions ----
        public static bool ToggleDown => KeyDown(VK_F6) || (Pad(L3) && Pad(R3) && (PadDown(L3) || PadDown(R3)));
        public static bool Fire => Key(VK_LBUTTON) || _rt;
        public static bool FireDown => KeyDown(VK_LBUTTON) || (_rt && !_rtPrev);
        /// <summary>First person as Jimmy (set by Mod.View). v0.18 made right click punch here; v0.20.1 put both views on
        /// Bully's PC layout instead (hold right = lock on, left = swing). Kept for whatever needs to know the view.</summary>
        public static bool FirstPerson;
        // v0.20.1 (Austin: "do the same thing for first person where you can hold right click to targwt and left click to
        // swing"): right click is the lock-on in both views again; first person no longer punches with it
        public static bool LockOn => (Key(VK_RBUTTON) && !Kit.SiEquipped) || _lt;   // a Schedule I gun aims instead
        public static bool Melee => Key(VK_LMENU) || Pad(X);
        public static bool MeleeDown => KeyDown(VK_LMENU) || PadDown(X);
        /// <summary>Bully's fist button: Alt / pad X, and left click while the fists are up (both views).</summary>
        // v0.20 (Austin: "his punches dont hit in 3rd person"; the trace: only right clicks = lock-ons, never a punch): with
        // the fists up, left click punches in both views like Bully's PC layout (lock on with right, punch with left)
        public static bool Punch => Melee || (Kit.BullyFists && Key(VK_LBUTTON));
        public static bool PunchDown => MeleeDown || (Kit.BullyFists && KeyDown(VK_LBUTTON));
        public static bool GrappleDown => KeyDown(VK_R) || PadDown(Y);
        public static bool TauntDown => KeyDown(VK_T) || PadDown(DUp);       // social, while locked on
        public static bool GreetDown => KeyDown(VK_G) || PadDown(DDown);
        public static bool UnequipDown => KeyDown(VK_MBUTTON);
        public static int Cycle => (_scroll > 0.01f || PadDown(RB)) ? 1 : (_scroll < -0.01f || PadDown(LB)) ? -1 : 0;
        static bool _meleePrevForGame;

        // ---- what Schedule I sees while Bully mode is on ----
        internal static bool Override(GameInput.ButtonCode code, bool original, int kind /*0 held, 1 down, 2 up*/)
        {
            if (!BullyMode || GameHasControl) return original;
            switch (code)
            {
                case GameInput.ButtonCode.PrimaryClick:      // left click is Bully's fire; Alt / X is the punch Schedule I throws
                    if (Kit.Grabbing) return false;          // ...except while Jimmy has someone by the collar (Alt / X = shove)
                    if (Kit.BullyFists) return false;        // ...or his Bully fists are up (Kit.Fists)
                    {
                        bool alt = kind == 0 ? Melee : kind == 1 ? MeleeDown : (!Melee && _meleePrevForGame);
                        return Kit.SiEquipped ? original || alt : alt;   // v0.18: a Schedule I item in hand gets its own left click (shoot, swing, use)
                    }
                case GameInput.ButtonCode.SecondaryClick:    // right click / LT = Bully lock-on
                    if (Kit.SiEquipped) return original;     // v0.18: a Schedule I item in hand gets its own right click (aim)
                    return false;
                case GameInput.ButtonCode.Reload:            // R = Bully grapple
                case GameInput.ButtonCode.InventoryLeft:
                case GameInput.ButtonCode.InventoryRight:    // scroll / bumpers = Bully weapon wheel
                    return false;
                case GameInput.ButtonCode.SkateboardDismount:   // Bully: Y / R steps off the board
                    return original || (Skate.Riding && (kind == 0 ? Key(VK_R) || Pad(Y) : kind == 1 ? KeyDown(VK_R) || PadDown(Y) : false));
                case GameInput.ButtonCode.Jump:
                    return original || (kind == 0 ? Pad(B) : kind == 1 ? PadDown(B) : false);
                case GameInput.ButtonCode.Sprint:
                    return original || (kind == 0 ? Pad(A) : kind == 1 ? PadDown(A) : false);
                case GameInput.ButtonCode.Crouch:
                    return original || (kind == 0 ? Key(VK_X) || Pad(R3) : kind == 1 ? KeyDown(VK_X) || PadDown(R3) : false);
                default:
                    return original;
            }
        }

        public static void LateUpdate()
        {
            _meleePrevForGame = Melee;
        }
    }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetButton))]
    static class PatchGetButton { static void Postfix(GameInput.ButtonCode buttonCode, ref bool __result) => __result = BullyInput.Override(buttonCode, __result, 0); }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetButtonDown))]
    static class PatchGetButtonDown { static void Postfix(GameInput.ButtonCode buttonCode, ref bool __result) => __result = BullyInput.Override(buttonCode, __result, 1); }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetButtonUp))]
    static class PatchGetButtonUp { static void Postfix(GameInput.ButtonCode buttonCode, ref bool __result) => __result = BullyInput.Override(buttonCode, __result, 2); }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.MouseScrollDelta), MethodType.Getter)]
    static class PatchScroll { static void Postfix(ref float __result) { BullyInput.RawScroll = __result; if (BullyInput.BullyMode && !BullyInput.GameHasControl) __result = 0f; } }
}

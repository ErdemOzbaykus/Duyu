using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Duyu
{
    // Mirrors engine/params.h. Append only.
    public enum P
    {
        MixerOn, MicOn, TransOn, MasterVol, MasterMute, Ceiling,
        Vol, Mute, StreamVol, StreamMute, Boost, Bass, Voice, Treble, EqOn, Preamp,
        SmartOn, SmartAmount, SmartTarget, DenoiseOn, DenoiseAmount,
        SpatialOn, Immersion, Distance, SpeakerAngle,
        ChatMixOn, ChatMix, StreamerOn, StreamListen, SpatialTest,
        MicGain, NsAmount, ImpactAmount, GateOn, GateAuto, GateThreshold,
        CompOn, CompAmount, StabilizerOn, Sidetone, MicTest,
        TransGain, TransMode, TransTone, TransBalance, TransWidth, TransDenoise,
        TransOwnVoice, TransAware, TransDuck, TransGuard, TransRaw,
        COUNT
    }

    public enum M
    {
        GameL, GameR, ChatL, ChatR, MediaL, MediaR, AuxL, AuxR, MicL, MicR,
        OutL, OutR, StreamL, StreamR, Trans, TransLatencyMs, Speech, Feedback,
        MicReduction, MixerRunning, MainRunning, MicRunning, TransRunning,
        Captured, TestState, Speaker, GuardTrips, Pending, COUNT
    }

    public enum Role { OutMain, OutStream, OutMic, OutGame, OutChat, OutMedia, OutAux, InMic, InTrans, COUNT }

    public static class Ch
    {
        public const int Game = 0, Chat = 1, Media = 2, Aux = 3, Mic = 4, Master = 5, Count = 6;
        public static readonly string[] Names = { "Oyun", "Sohbet", "Medya", "Diğer", "Mikrofon", "Kulaklık" };
        public static readonly string[] Keys = { "Game", "Chat", "Media", "Aux", "Mic", "Master" };
    }

    internal static class Native
    {
        private const string Dll = "Duyu.Engine.dll";
        private const CallingConvention C = CallingConvention.Cdecl;

        [DllImport(Dll, CallingConvention = C, CharSet = CharSet.Unicode)] public static extern IntPtr duyu_create(string dir, uint onlyPid);
        [DllImport(Dll, CallingConvention = C)] public static extern void duyu_destroy(IntPtr e);
        [DllImport(Dll, CallingConvention = C)] public static extern void duyu_set(IntPtr e, int id, int index, float v);
        [DllImport(Dll, CallingConvention = C)] public static extern void duyu_set_band(IntPtr e, int ch, int band, float on, float type, float f, float gain, float q);
        [DllImport(Dll, CallingConvention = C, CharSet = CharSet.Unicode)] public static extern void duyu_set_device(IntPtr e, int role, string id);
        [DllImport(Dll, CallingConvention = C, CharSet = CharSet.Unicode)] public static extern void duyu_assign(IntPtr e, string exe, int ch);
        [DllImport(Dll, CallingConvention = C)] public static extern void duyu_mic_test(IntPtr e);
        [DllImport(Dll, CallingConvention = C)] public static extern void duyu_meters(IntPtr e, [Out] float[] values, int count);
        [DllImport(Dll, CallingConvention = C, CharSet = CharSet.Unicode)] private static extern int duyu_devices(IntPtr e, StringBuilder b, int cap);
        [DllImport(Dll, CallingConvention = C, CharSet = CharSet.Unicode)] private static extern int duyu_sessions(IntPtr e, StringBuilder b, int cap);
        [DllImport(Dll, CallingConvention = C)] public static extern float duyu_filter_db(int type, float f, float gain, float q, float at);
        [DllImport(Dll, CallingConvention = C, CharSet = CharSet.Unicode)] public static extern void duyu_guard(string dir, uint parentPid, uint onlyPid);

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        public static string Devices(IntPtr e)
        {
            var b = new StringBuilder(32768);
            return duyu_devices(e, b, b.Capacity) <= b.Capacity ? b.ToString() : "";
        }

        public static string Sessions(IntPtr e)
        {
            var b = new StringBuilder(32768);
            return duyu_sessions(e, b, b.Capacity) <= b.Capacity ? b.ToString() : "";
        }
    }
}
